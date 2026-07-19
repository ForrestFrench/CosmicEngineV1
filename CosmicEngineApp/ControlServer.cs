using CosmicEngine.App.Audio;
using CosmicEngine.App.Engine;
using System;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace CosmicEngine.App
{
    public static class ControlServer
    {
        private static Thread? _thread;
        private static HttpListener? _listener;

        // Reliability hardening (AUDIT.md, HttpListener.Start() crash investigation):
        // on macOS/Linux, HttpListener.Start() runs through .NET's managed
        // (Mono-derived) HttpEndPointListener implementation rather than native
        // http.sys, which has known socket-race edge cases when port 8080 was very
        // recently held by another Cosmic Engine instance (e.g. one that was
        // force-killed rather than quit cleanly, or two instances started back to
        // back). Observed failure modes include HttpListenerException ("Address
        // already in use") - reproduced directly by starting two --dashboard-only
        // instances simultaneously - and, per the originally reported crash, an
        // ArgumentNullException thrown deep inside that same implementation's
        // internal accept-loop setup when OS socket state hasn't fully settled yet.
        // Both are the same underlying transient condition, so retry a few times
        // with a short delay before giving up with a clear, actionable message
        // instead of letting either exception crash the whole process unhandled.
        private const int StartMaxAttempts = 5;
        private const int StartRetryDelayMs = 400;

        public static void Start()
        {
            Exception? lastError = null;

            for (int attempt = 1; attempt <= StartMaxAttempts; attempt++)
            {
                try
                {
                    _listener = new HttpListener();
                    _listener.Prefixes.Add("http://localhost:8080/");
                    _listener.Start();
                    lastError = null;
                    break;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    _listener = null;

                    if (attempt < StartMaxAttempts)
                    {
                        Console.WriteLine(
                            $"[ControlServer] Could not start dashboard on port 8080 " +
                            $"(attempt {attempt}/{StartMaxAttempts}): {ex.GetType().Name}: {ex.Message}. " +
                            $"Retrying in {StartRetryDelayMs}ms...");
                        Thread.Sleep(StartRetryDelayMs);
                    }
                }
            }

            if (lastError != null)
            {
                throw new InvalidOperationException(
                    $"ControlServer could not start the dashboard on http://localhost:8080 after " +
                    $"{StartMaxAttempts} attempts. Port 8080 may still be in use by a previous Cosmic " +
                    "Engine instance (especially if it was force-killed rather than quit normally via " +
                    "the dashboard's Quit button) - wait a few seconds and try again, or run " +
                    "`lsof -i :8080` to check for a lingering process.",
                    lastError);
            }

            _thread = new Thread(Loop)
            {
                IsBackground = true,
                Name = "ControlServer"
            };
            _thread.Start();

            Console.WriteLine("Control panel: http://localhost:8080");
        }

        public static void Stop()
        {
            _listener?.Stop();
        }

        private static void Loop()
        {
            while (_listener!.IsListening)
            {
                try
                {
                    var ctx = _listener.GetContext();
                    Handle(ctx);
                }
                catch { }
            }
        }

        private static void Handle(HttpListenerContext ctx)
        {
            string path = ctx.Request.Url?.AbsolutePath ?? "/";

            // Scene Dashboard v0.1 --------------------------------------------------

            if (path == "/status")
            {
                var app = CosmicEngineApp.Current;
                string json = JsonSerializer.Serialize(new
                {
                    running        = app != null,
                    world          = app?.CurrentWorldName ?? "(none - dashboard idle)",
                    profile        = app?.CurrentProfileName ?? "-",
                    fps            = app?.LastObservedFps ?? 0f,
                    renderWidth    = app?.CurrentRenderWidth ?? 0,
                    renderHeight   = app?.CurrentRenderHeight ?? 0,
                    // AudioEngine is a process-wide static, not owned by any one
                    // CosmicEngineApp instance - reading it directly (rather than
                    // through app?.AudioCapturing) means Dashboard-Only Launcher
                    // Mode reports the real capture state even before any scene
                    // (and therefore any CosmicEngineApp) exists yet.
                    audioCapturing = Audio.AudioEngine.IsCapturing,
                    seed           = app?.CurrentSeedInfo ?? "-",
                    // Calibrated Audio Reactivity Integration v0.1: CalibrationEngine
                    // is a process-wide static too (same reasoning as AudioEngine
                    // above), so its live post-curve values are meaningful to show
                    // here even before any scene is running. "raw + calibrated blend"
                    // is deliberately precise, not "calibrated" alone - scenes add a
                    // modest calibrated contribution on top of their existing
                    // raw-audio-derived response, they do not replace it. See
                    // AUDIT.md Entry 27.
                    calibratedA      = CalibrationEngine.InputA.CurveOutput,
                    calibratedB      = CalibrationEngine.InputB.CurveOutput,
                    sceneAudioSource = "raw + calibrated blend"
                });
                Respond(ctx, 200, json, "application/json");
                return;
            }

            // Read-only bridge for external visual instruments such as the Video
            // Exploration Media Console. This exposes normalized control intent,
            // never PCM audio, and does not own or restart audio capture.
            if (path == "/audio/reactivity")
            {
                Respond(ctx, 200, JsonSerializer.Serialize(GuitarIntentResponse.Snapshot()), "application/json");
                return;
            }

            if (path == "/scenes")
            {
                var scenes = Array.ConvertAll(SceneRegistry.All, s => new
                {
                    id             = s.Id,
                    displayName    = s.DisplayName,
                    description    = s.Description,
                    status         = s.Status,
                    defaultProfile = s.DefaultProfile,
                    showable       = s.Showable,
                    showSeed       = s.ShowSeed
                });
                Respond(ctx, 200, JsonSerializer.Serialize(scenes), "application/json");
                return;
            }

            if (ctx.Request.HttpMethod == "POST" && path == "/launch")
            {
                using var reader = new System.IO.StreamReader(ctx.Request.InputStream);
                var doc = JsonDocument.Parse(reader.ReadToEnd());
                string? sceneId = doc.RootElement.TryGetProperty("sceneId", out var s) ? s.GetString() : null;
                string? profile = doc.RootElement.TryGetProperty("profile", out var p) ? p.GetString() : null;

                // Dashboard-Only Launcher Mode: if no CosmicEngineApp exists yet (the
                // process was started with --dashboard-only and no scene has been
                // picked yet), there's nothing for RequestSwitch to switch - route to
                // DashboardHost instead, which creates the first CosmicEngineApp (and
                // its GameWindow) on the main thread. Once a scene is running, this
                // falls back to the existing in-process live-switch path unchanged.
                if (CosmicEngineApp.Current != null)
                    CosmicEngineApp.Current.RequestSwitch(sceneId, profile);
                else
                    DashboardHost.RequestLaunch(sceneId, profile);

                Respond(ctx, 200, "{}");
                return;
            }

            if (ctx.Request.HttpMethod == "POST" && path == "/restart")
            {
                CosmicEngineApp.Current?.RequestRestart();
                Respond(ctx, 200, "{}");
                return;
            }

            if (ctx.Request.HttpMethod == "POST" && path == "/quit")
            {
                // Dashboard-Only Launcher Mode: quitting before any scene was ever
                // launched has no CosmicEngineApp to signal - route to DashboardHost's
                // own quit flag so the dashboard-only wait loop (and the process) can
                // still shut down cleanly.
                if (CosmicEngineApp.Current != null)
                    CosmicEngineApp.Current.RequestQuit();
                else
                    DashboardHost.RequestQuit();

                Respond(ctx, 200, "{}");
                return;
            }

            // Dashboard Calibration Tab v0.1 -----------------------------------------
            // Deliberately separate from the Scene Dashboard and "THE DEEPEST SPACE"
            // Tuning.cs sliders above - this answers "how loud is this input, and how
            // do we map it to a normalized 0-1 visual-control value", not "how should
            // a scene react to that value". See Audio/InputCalibration.cs and
            // Audio/CalibrationEngine.cs for the model itself.

            if (path == "/calibration/status")
            {
                Respond(ctx, 200, JsonSerializer.Serialize(CalibrationEngine.Snapshot()), "application/json");
                return;
            }

            if (ctx.Request.HttpMethod == "POST" && path == "/calibration/channels")
            {
                using var reader = new System.IO.StreamReader(ctx.Request.InputStream);
                var doc = JsonDocument.Parse(reader.ReadToEnd());
                int a = doc.RootElement.TryGetProperty("a", out var av) ? av.GetInt32() : CalibrationEngine.ChannelA;
                int b = doc.RootElement.TryGetProperty("b", out var bv) ? bv.GetInt32() : CalibrationEngine.ChannelB;
                CalibrationEngine.SetChannels(a, b);
                Respond(ctx, 200, "{}");
                return;
            }

            if (ctx.Request.HttpMethod == "POST" && path == "/calibration/manual")
            {
                using var reader = new System.IO.StreamReader(ctx.Request.InputStream);
                var doc = JsonDocument.Parse(reader.ReadToEnd());
                var input = CalibrationEngine.ResolveInput(
                    doc.RootElement.TryGetProperty("input", out var iv) ? iv.GetString() : null);
                if (doc.RootElement.TryGetProperty("field", out var fv) &&
                    doc.RootElement.TryGetProperty("value", out var vv))
                {
                    float val = vv.GetSingle();
                    switch (fv.GetString())
                    {
                        case "gain":          input.Gain = Math.Clamp(val, 0f, InputCalibration.MaxGain); break;
                        case "gateThreshold": input.GateThreshold = val; break;
                        case "smoothing":     input.Smoothing = val; break;
                        case "outputCeiling": input.OutputCeiling = val; break;
                    }
                }
                Respond(ctx, 200, "{}");
                return;
            }

            if (ctx.Request.HttpMethod == "POST" && path == "/calibration/preset")
            {
                using var reader = new System.IO.StreamReader(ctx.Request.InputStream);
                var doc = JsonDocument.Parse(reader.ReadToEnd());
                string? presetInput = doc.RootElement.TryGetProperty("input", out var piv) ? piv.GetString() : null;
                string preset = doc.RootElement.TryGetProperty("preset", out var pv) ? pv.GetString() ?? "Linear" : "Linear";

                if (string.Equals(presetInput, "Both", StringComparison.OrdinalIgnoreCase))
                {
                    CalibrationEngine.InputA.ApplyPreset(preset);
                    CalibrationEngine.InputB.ApplyPreset(preset);
                }
                else
                {
                    CalibrationEngine.ResolveInput(presetInput).ApplyPreset(preset);
                }
                Respond(ctx, 200, "{}");
                return;
            }

            if (ctx.Request.HttpMethod == "POST" && path == "/calibration/curve")
            {
                using var reader = new System.IO.StreamReader(ctx.Request.InputStream);
                var doc = JsonDocument.Parse(reader.ReadToEnd());
                var input = CalibrationEngine.ResolveInput(
                    doc.RootElement.TryGetProperty("input", out var iv) ? iv.GetString() : null);
                float p1x = doc.RootElement.TryGetProperty("p1x", out var a1) ? a1.GetSingle() : input.CurveP1X;
                float p1y = doc.RootElement.TryGetProperty("p1y", out var a2) ? a2.GetSingle() : input.CurveP1Y;
                float p2x = doc.RootElement.TryGetProperty("p2x", out var a3) ? a3.GetSingle() : input.CurveP2X;
                float p2y = doc.RootElement.TryGetProperty("p2y", out var a4) ? a4.GetSingle() : input.CurveP2Y;
                input.SetCustomCurve(p1x, p1y, p2x, p2y);
                Respond(ctx, 200, "{}");
                return;
            }

            if (ctx.Request.HttpMethod == "POST" && path == "/calibration/reset")
            {
                using var reader = new System.IO.StreamReader(ctx.Request.InputStream);
                var doc = JsonDocument.Parse(reader.ReadToEnd());
                string? resetInput = doc.RootElement.TryGetProperty("input", out var riv) ? riv.GetString() : null;

                if (string.Equals(resetInput, "Both", StringComparison.OrdinalIgnoreCase))
                {
                    CalibrationEngine.InputA.ResetDefaults();
                    CalibrationEngine.InputB.ResetDefaults();
                }
                else
                {
                    CalibrationEngine.ResolveInput(resetInput).ResetDefaults();
                }
                Respond(ctx, 200, "{}");
                return;
            }

            // Calibrated Audio Reactivity Integration v0.1: a clearly-labeled test
            // pulse for proving the full calibration -> scene pipeline works without
            // a real guitar/interface connected. Sets a temporary raw-level override
            // (see InputCalibration.TestOverrideRawLevel) that flows through the
            // exact same gain/gate/curve/smoothing/peak pipeline as real audio, then
            // (client-side) clears itself after a few seconds. Never invoked
            // automatically - only ever by an explicit dashboard button click.
            if (ctx.Request.HttpMethod == "POST" && path == "/calibration/testinput")
            {
                using var reader = new System.IO.StreamReader(ctx.Request.InputStream);
                var doc = JsonDocument.Parse(reader.ReadToEnd());
                string? testInput = doc.RootElement.TryGetProperty("input", out var tiv) ? tiv.GetString() : null;
                float? value = doc.RootElement.TryGetProperty("value", out var tvv) && tvv.ValueKind != JsonValueKind.Null
                    ? tvv.GetSingle()
                    : (float?)null;

                CalibrationEngine.SetTestOverride(testInput, value);
                Respond(ctx, 200, "{}");
                return;
            }

            // Calibration Presets v0.1: save/load/delete named full-setup snapshots
            // (routing + both inputs' manual controls + curves) to a local JSON
            // file (Audio/CalibrationPresetStore.cs). See that file's doc comment
            // for the storage-location rationale and corrupt-file handling.

            if (path == "/calibration/presets")
            {
                var summaries = CalibrationPresetStore.List().ConvertAll(p => new
                {
                    name = p.Name,
                    updatedAt = p.UpdatedAt,
                    targetInterface = p.TargetInterface
                });
                Respond(ctx, 200, JsonSerializer.Serialize(summaries), "application/json");
                return;
            }

            if (ctx.Request.HttpMethod == "POST" && path == "/calibration/presets/save")
            {
                using var reader = new System.IO.StreamReader(ctx.Request.InputStream);
                var doc = JsonDocument.Parse(reader.ReadToEnd());
                string? name = doc.RootElement.TryGetProperty("name", out var nv) ? nv.GetString() : null;
                string notes = doc.RootElement.TryGetProperty("notes", out var ntv) ? (ntv.GetString() ?? "") : "";
                string target = doc.RootElement.TryGetProperty("targetInterface", out var tgv) ? (tgv.GetString() ?? "Generic") : "Generic";

                if (string.IsNullOrWhiteSpace(name))
                {
                    Respond(ctx, 200, JsonSerializer.Serialize(new { ok = false, error = "Preset name is required." }), "application/json");
                    return;
                }
                var preset = CalibrationPreset.CaptureCurrent(name.Trim(), notes, target);
                CalibrationPresetStore.Upsert(preset);
                Respond(ctx, 200, JsonSerializer.Serialize(new { ok = true }), "application/json");
                return;
            }

            if (ctx.Request.HttpMethod == "POST" && path == "/calibration/presets/saveas")
            {
                using var reader = new System.IO.StreamReader(ctx.Request.InputStream);
                var doc = JsonDocument.Parse(reader.ReadToEnd());
                string? name = doc.RootElement.TryGetProperty("name", out var nv) ? nv.GetString() : null;
                string notes = doc.RootElement.TryGetProperty("notes", out var ntv) ? (ntv.GetString() ?? "") : "";
                string target = doc.RootElement.TryGetProperty("targetInterface", out var tgv) ? (tgv.GetString() ?? "Generic") : "Generic";

                if (string.IsNullOrWhiteSpace(name))
                {
                    Respond(ctx, 200, JsonSerializer.Serialize(new { ok = false, error = "Preset name is required." }), "application/json");
                    return;
                }
                var preset = CalibrationPreset.CaptureCurrent(name.Trim(), notes, target);
                bool created = CalibrationPresetStore.SaveAsNew(preset);
                Respond(ctx, 200, JsonSerializer.Serialize(created
                    ? new { ok = true, error = "" }
                    : new { ok = false, error = $"A preset named '{name.Trim()}' already exists. Choose a different name or use Save to update it." }), "application/json");
                return;
            }

            if (ctx.Request.HttpMethod == "POST" && path == "/calibration/presets/load")
            {
                using var reader = new System.IO.StreamReader(ctx.Request.InputStream);
                var doc = JsonDocument.Parse(reader.ReadToEnd());
                string? name = doc.RootElement.TryGetProperty("name", out var nv) ? nv.GetString() : null;

                var preset = string.IsNullOrWhiteSpace(name) ? null : CalibrationPresetStore.Get(name);
                if (preset == null)
                {
                    Respond(ctx, 200, JsonSerializer.Serialize(new { ok = false, error = $"Preset '{name}' not found." }), "application/json");
                    return;
                }
                string warning = preset.ApplyTo();
                Respond(ctx, 200, JsonSerializer.Serialize(new { ok = true, warning, loadedName = preset.Name }), "application/json");
                return;
            }

            if (ctx.Request.HttpMethod == "POST" && path == "/calibration/presets/delete")
            {
                using var reader = new System.IO.StreamReader(ctx.Request.InputStream);
                var doc = JsonDocument.Parse(reader.ReadToEnd());
                string? name = doc.RootElement.TryGetProperty("name", out var nv) ? nv.GetString() : null;

                bool deleted = !string.IsNullOrWhiteSpace(name) && CalibrationPresetStore.Delete(name);
                Respond(ctx, 200, JsonSerializer.Serialize(new { ok = deleted }), "application/json");
                return;
            }

            // -------------------------------------------------------------------------

            if (ctx.Request.HttpMethod == "POST" && path == "/set")
            {
                using var reader = new System.IO.StreamReader(ctx.Request.InputStream);
                string body = reader.ReadToEnd();
                var doc = JsonDocument.Parse(body);

                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    float val = prop.Value.GetSingle();
                    switch (prop.Name)
                    {
                        case "Smoothing":      Tuning.Smoothing      = val; break;
                        case "BassFloor":      Tuning.BassFloor      = val; break;
                        case "MidFloor":       Tuning.MidFloor       = val; break;
                        case "TrebleFloor":    Tuning.TrebleFloor    = val; break;
                        case "BassMax":        Tuning.BassMax        = val; break;
                        case "MidMax":         Tuning.MidMax         = val; break;
                        case "TrebleMax":      Tuning.TrebleMax      = val; break;
                        case "BassBrightness": Tuning.BassBrightness = val; break;
                        case "DimLevel":       Tuning.DimLevel       = val; break;
                        // World03 Wind Turbine Fire (Phase 1.1): clamp defensively here too,
                        // on top of WindTurbineFireScene's own per-frame clamp and the
                        // slider's own min/max - belt and suspenders against any caller
                        // sending an out-of-range value directly to /set.
                        case "WindTurbineFireEvolutionSeconds":
                            Tuning.WindTurbineFireEvolutionSeconds = Math.Clamp(val, 30f, 300f);
                            break;
                        // World04 Underwater (Phase 1): same defensive clamp pattern
                        // as WindTurbineFireEvolutionSeconds above.
                        case "UnderwaterEvolutionSeconds":
                            Tuning.UnderwaterEvolutionSeconds = Math.Clamp(val, 30f, 300f);
                            break;
                        // World05 Hybrid Test (Phase 1 "Hybrid Proof"): same defensive-clamp
                        // pattern as the two scene-evolution sliders above.
                        case "HybridBlend":
                            Tuning.HybridBlend = Math.Clamp(val, 0f, 1f);
                            break;
                        // Phase 3 Effect Stack v1 (World05 HybridTest only) - same defensive-clamp
                        // pattern as HybridBlend above. Mirror toggles are represented as 0/1 range
                        // sliders (not a new checkbox mechanism) so they reuse the existing generic
                        // slider send()/init() JS wiring unchanged.
                        case "HybridGrayscale":
                            Tuning.HybridGrayscale = Math.Clamp(val, 0f, 1f);
                            break;
                        case "HybridMirrorX":
                            Tuning.HybridMirrorX = val >= 0.5f;
                            break;
                        case "HybridMirrorY":
                            Tuning.HybridMirrorY = val >= 0.5f;
                            break;
                        case "HybridGradeLift":
                            Tuning.HybridGradeLift = Math.Clamp(val, -0.5f, 0.5f);
                            break;
                        case "HybridGradeGamma":
                            Tuning.HybridGradeGamma = Math.Clamp(val, 0.2f, 3.0f);
                            break;
                        case "HybridGradeGain":
                            Tuning.HybridGradeGain = Math.Clamp(val, 0f, 2f);
                            break;
                        case "HybridVignette":
                            Tuning.HybridVignette = Math.Clamp(val, 0f, 1f);
                            break;
                        case "HybridPlaybackSpeed":
                            Tuning.HybridPlaybackSpeed = Math.Clamp(val, 0.25f, 2.0f);
                            break;
                        // World06 Visual Composer Sandbox (artistic-exploration pass) - same
                        // defensive-clamp pattern as World05 Hybrid above.
                        case "ComposerIndex":
                            Tuning.ComposerIndex = (int)Math.Round(val);
                            break;
                        case "ComposerBlend":
                            Tuning.ComposerBlend = Math.Clamp(val, 0f, 1f);
                            break;
                        case "ComposerVolumetricDensity":
                            Tuning.ComposerVolumetricDensity = Math.Clamp(val, 0f, 2f);
                            break;
                        case "ComposerParticleDensity":
                            Tuning.ComposerParticleDensity = Math.Clamp(val, 0f, 2f);
                            break;
                        case "ComposerLighting":
                            Tuning.ComposerLighting = Math.Clamp(val, 0f, 2f);
                            break;
                        case "ComposerAudioReactivity":
                            Tuning.ComposerAudioReactivity = Math.Clamp(val, 0f, 2f);
                            break;
                        case "ComposerGrayscale":
                            Tuning.ComposerGrayscale = Math.Clamp(val, 0f, 1f);
                            break;
                        case "ComposerMirrorX":
                            Tuning.ComposerMirrorX = val >= 0.5f;
                            break;
                        case "ComposerMirrorY":
                            Tuning.ComposerMirrorY = val >= 0.5f;
                            break;
                        case "ComposerGradeLift":
                            Tuning.ComposerGradeLift = Math.Clamp(val, -0.5f, 0.5f);
                            break;
                        case "ComposerGradeGamma":
                            Tuning.ComposerGradeGamma = Math.Clamp(val, 0.2f, 3.0f);
                            break;
                        case "ComposerGradeGain":
                            Tuning.ComposerGradeGain = Math.Clamp(val, 0f, 2f);
                            break;
                        case "ComposerVignette":
                            Tuning.ComposerVignette = Math.Clamp(val, 0f, 1f);
                            break;
                        case "ComposerPlaybackSpeed":
                            Tuning.ComposerPlaybackSpeed = Math.Clamp(val, 0.25f, 2.0f);
                            break;
                    }
                }

                Respond(ctx, 200, "{}");
                return;
            }

            if (path == "/values")
            {
                string json = JsonSerializer.Serialize(new
                {
                    Tuning.Smoothing,
                    Tuning.BassFloor,
                    Tuning.MidFloor,
                    Tuning.TrebleFloor,
                    Tuning.BassMax,
                    Tuning.MidMax,
                    Tuning.TrebleMax,
                    Tuning.BassBrightness,
                    Tuning.DimLevel,
                    Tuning.WindTurbineFireEvolutionSeconds,
                    Tuning.UnderwaterEvolutionSeconds,
                    Tuning.HybridBlend,
                    Tuning.HybridGrayscale,
                    HybridMirrorX = Tuning.HybridMirrorX ? 1 : 0,
                    HybridMirrorY = Tuning.HybridMirrorY ? 1 : 0,
                    Tuning.HybridGradeLift,
                    Tuning.HybridGradeGamma,
                    Tuning.HybridGradeGain,
                    Tuning.HybridVignette,
                    Tuning.HybridPlaybackSpeed,
                    Tuning.ComposerIndex,
                    Tuning.ComposerBlend,
                    Tuning.ComposerVolumetricDensity,
                    Tuning.ComposerParticleDensity,
                    Tuning.ComposerLighting,
                    Tuning.ComposerAudioReactivity,
                    Tuning.ComposerGrayscale,
                    ComposerMirrorX = Tuning.ComposerMirrorX ? 1 : 0,
                    ComposerMirrorY = Tuning.ComposerMirrorY ? 1 : 0,
                    Tuning.ComposerGradeLift,
                    Tuning.ComposerGradeGamma,
                    Tuning.ComposerGradeGain,
                    Tuning.ComposerVignette,
                    Tuning.ComposerPlaybackSpeed
                });
                Respond(ctx, 200, json, "application/json");
                return;
            }

            Respond(ctx, 200, Html(), "text/html");
        }

        private static void Respond(HttpListenerContext ctx, int code, string body, string mime = "application/json")
        {
            byte[] buf = Encoding.UTF8.GetBytes(body);
            ctx.Response.StatusCode = code;
            ctx.Response.ContentType = mime;
            ctx.Response.ContentLength64 = buf.Length;
            ctx.Response.OutputStream.Write(buf, 0, buf.Length);
            ctx.Response.OutputStream.Close();
        }

        private static string Html() => @"<!DOCTYPE html>
<html>
<head>
<meta charset='utf-8'>
<title>Cosmic Engine — Scene Dashboard</title>
<style>
  body { background: #050308; color: #ccc; font-family: monospace; padding: 30px; max-width: 720px; margin: 0 auto; }
  h1 { color: #ff2266; letter-spacing: 2px; }
  h2 { color: #ff2266; letter-spacing: 1px; margin-bottom: 4px; font-size: 20px; }
  h3 { color: #22ddff; margin-top: 28px; margin-bottom: 8px; }
  .row { display: flex; align-items: center; margin: 10px 0; gap: 12px; }
  label { width: 170px; font-size: 13px; color: #aaa; }
  input[type=range] { flex: 1; accent-color: #ff2266; }
  .val { width: 48px; text-align: right; font-size: 13px; color: #ff2266; }
  .reset { margin-top: 28px; background: #222; color: #ff2266; border: 1px solid #ff2266;
           padding: 8px 20px; cursor: pointer; font-family: monospace; letter-spacing: 1px; }
  .reset:hover { background: #ff2266; color: #000; }
  .divider { border: none; border-top: 1px solid #222; margin: 40px 0; }

  /* Scene Dashboard v0.1 */
  .status-bar { background: #0d0d14; border: 1px solid #222; border-radius: 4px; padding: 12px 16px;
                font-size: 13px; color: #9fe; margin-bottom: 16px; line-height: 1.8; }
  .status-bar b { color: #22ddff; }
  .toggle-row { font-size: 13px; color: #aaa; margin-bottom: 20px; }
  .toggle-row input { accent-color: #ff2266; margin-right: 6px; }
  .scene-grid { display: grid; grid-template-columns: 1fr 1fr; gap: 16px; }
  @media (max-width: 560px) { .scene-grid { grid-template-columns: 1fr; } }
  .scene-card { background: #0d0d14; border: 1px solid #333; border-radius: 6px; padding: 16px; }
  .scene-card h4 { margin: 0 0 6px 0; color: #22ddff; font-size: 16px; }
  .scene-card .desc { font-size: 12px; color: #999; margin: 0 0 10px 0; min-height: 32px; }
  .scene-card .status { font-size: 11px; color: #ffbb33; margin: 0 0 4px 0; }
  .scene-card .default-profile { font-size: 11px; color: #666; margin: 0 0 12px 0; }
  .scene-card .buttons { display: flex; flex-wrap: wrap; gap: 8px; }
  .scene-card button { background: #1a1a22; color: #ccc; border: 1px solid #444;
                        padding: 6px 12px; font-size: 12px; cursor: pointer; font-family: monospace; border-radius: 3px; }
  .scene-card button:hover { border-color: #22ddff; color: #22ddff; }
  .quit-btn { margin-top: 20px; background: #2a0d0d; color: #ff5555; border: 1px solid #ff5555;
              padding: 8px 20px; cursor: pointer; font-family: monospace; letter-spacing: 1px; border-radius: 3px; }
  .quit-btn:hover { background: #ff5555; color: #000; }

  /* Tabs */
  .tabs { display: flex; gap: 4px; margin-bottom: 20px; border-bottom: 1px solid #222; }
  .tab-btn { background: none; border: none; color: #666; padding: 10px 18px; font-family: monospace;
             font-size: 14px; letter-spacing: 1px; cursor: pointer; border-bottom: 2px solid transparent; }
  .tab-btn.active { color: #ff2266; border-bottom: 2px solid #ff2266; }
  .tab-btn:hover { color: #22ddff; }
  .tab-panel { display: none; }
  .tab-panel.active { display: block; }

  /* Dashboard Calibration Tab v0.1 */
  .calib-columns { display: grid; grid-template-columns: 1fr 1fr; gap: 24px; }
  @media (max-width: 700px) { .calib-columns { grid-template-columns: 1fr; } }
  .calib-card { background: #0d0d14; border: 1px solid #333; border-radius: 6px; padding: 16px; margin-bottom: 20px; }
  .calib-card h4 { margin: 0 0 10px 0; color: #22ddff; font-size: 15px; }
  .chan-select { background: #1a1a22; color: #ccc; border: 1px solid #444; padding: 6px 10px;
                 font-family: monospace; font-size: 13px; border-radius: 3px; }
  .warn-text { color: #ffbb33; font-size: 12px; margin-top: 8px; }
  .limit-text { color: #666; font-size: 11px; margin-top: 10px; line-height: 1.6; }
  .meter-track { position: relative; background: #1a1a22; border: 1px solid #333; height: 26px;
                 border-radius: 3px; overflow: hidden; margin: 6px 0; }
  .meter-zone { position: absolute; top: 0; bottom: 0; background: rgba(80,220,120,0.14); }
  .meter-fill { position: absolute; top: 0; bottom: 0; left: 0; width: 0%;
                background: linear-gradient(90deg,#2299ff,#22ddaa); transition: width 0.08s linear; }
  .meter-fill.hot { background: linear-gradient(90deg,#ffaa22,#ff8833); }
  .meter-fill.clip { background: #ff2244; }
  .meter-peak { position: absolute; top: 0; bottom: 0; width: 2px; background: #fff; left: 0; }
  .meter-sub { height: 12px; }
  .meter-label { font-size: 10px; color: #666; letter-spacing: 1px; margin-top: 8px; }
  .meter-readout { display: flex; flex-wrap: wrap; gap: 10px; font-size: 11px; color: #999; margin-top: 4px; }
  .meter-readout b { color: #ccc; }
  .clip-badge { display: inline-block; font-size: 10px; padding: 2px 8px; border-radius: 3px; margin-left: 8px;
                background: #222; color: #666; vertical-align: middle; }
  .clip-badge.active { background: #ff2244; color: #fff; }
  .clip-badge.test-badge.active { background: #ffbb33; color: #000; }
  .curve-svg { background: #0a0a10; border: 1px solid #333; border-radius: 4px; display: block; }
  .curve-point { cursor: grab; }
  .preset-row { display: flex; gap: 6px; flex-wrap: wrap; margin: 10px 0; }
  .preset-btn { background: #1a1a22; color: #ccc; border: 1px solid #444; padding: 5px 10px; font-size: 11px;
                cursor: pointer; font-family: monospace; border-radius: 3px; }
  .preset-btn:hover { border-color: #22ddff; color: #22ddff; }
  .preset-btn.active-preset { border-color: #ff2266; color: #ff2266; }
  .manual-row { display: flex; align-items: center; gap: 8px; margin: 6px 0; font-size: 12px; }
  .manual-row label { width: 116px; color: #aaa; }
  .manual-row input[type=range] { flex: 1; accent-color: #22ddff; }
  .manual-row .val { width: 46px; text-align: right; color: #22ddff; }
  .calib-status { font-size: 12px; color: #9fe; line-height: 1.8; background: #0d0d14; border: 1px solid #222;
                  border-radius: 4px; padding: 12px 16px; margin-bottom: 20px; }
  .calib-status b { color: #22ddff; }
  .reset-small { background: #222; color: #ff5555; border: 1px solid #ff5555; padding: 5px 12px; font-size: 11px;
                 cursor: pointer; font-family: monospace; border-radius: 3px; margin-top: 10px; }
  .reset-small:hover { background: #ff5555; color: #000; }
</style>
</head>
<body>

<h1>COSMIC ENGINE — SCENE DASHBOARD</h1>
<p style='color:#666;font-size:12px'>Launch, review, or switch scenes without typing CLI commands.</p>

<div class='tabs'>
  <button class='tab-btn active' data-tab='scenes'>Scenes / Show</button>
  <button class='tab-btn' data-tab='calibration'>Calibration</button>
</div>

<div id='tab-scenes' class='tab-panel active'>

<div class='status-bar' id='statusBar'>Loading engine status…</div>

<div class='toggle-row'>
  <label><input type='checkbox' id='showExperimental'> Show experimental scenes</label>
</div>

<div class='scene-grid' id='sceneGrid'>Loading scenes…</div>

<button class='quit-btn' onclick='quitEngine()'>Quit Engine</button>

<hr class='divider'>

<h1>THE DEEPEST SPACE</h1>
<p style='color:#666;font-size:12px'>A new pattern generates every time you launch the app. These controls tune how the audio drives it.</p>

<h3>SMOOTHING</h3>
<div class='row'><label>Smoothing</label><input type='range' id='Smoothing' min='0' max='0.95' step='0.01'><span class='val' id='Smoothing_v'></span></div>

<h3>AUDIO FLOORS (silence threshold)</h3>
<div class='row'><label>Bass Floor</label><input type='range' id='BassFloor' min='0' max='0.3' step='0.001'><span class='val' id='BassFloor_v'></span></div>
<div class='row'><label>Mid Floor</label><input type='range' id='MidFloor' min='0' max='0.05' step='0.0001'><span class='val' id='MidFloor_v'></span></div>
<div class='row'><label>Treble Floor</label><input type='range' id='TrebleFloor' min='0' max='0.01' step='0.00001'><span class='val' id='TrebleFloor_v'></span></div>

<h3>AUDIO MAXIMUMS (full scale point)</h3>
<div class='row'><label>Bass Max</label><input type='range' id='BassMax' min='1' max='100' step='0.5'><span class='val' id='BassMax_v'></span></div>
<div class='row'><label>Mid Max</label><input type='range' id='MidMax' min='1' max='50' step='0.5'><span class='val' id='MidMax_v'></span></div>
<div class='row'><label>Treble Max</label><input type='range' id='TrebleMax' min='0.05' max='2' step='0.01'><span class='val' id='TrebleMax_v'></span></div>

<h3>OVERALL BRIGHTNESS</h3>
<div class='row'><label>Dim Level</label><input type='range' id='DimLevel' min='0' max='1' step='0.01'><span class='val' id='DimLevel_v'></span></div>
<div class='row'><label>Bass Brightness</label><input type='range' id='BassBrightness' min='0' max='3' step='0.01'><span class='val' id='BassBrightness_v'></span></div>

<button class='reset' onclick='resetDefaults()'>RESET DEFAULTS</button>

<hr class='divider'>

<h3>WIND TURBINE FIRE — SCENE-SPECIFIC (not a Deepest Space audio control)</h3>
<p style='color:#666;font-size:12px'>How long sustained loud playing takes to fully evolve the Wind Turbine Fire scene (World03) from cold/calm to fully hot. Only affects that one scene — set it to match your song length.</p>
<div class='row'><label>Fire Evolution Time</label><input type='range' id='WindTurbineFireEvolutionSeconds' min='30' max='300' step='1'><span class='val' id='WindTurbineFireEvolutionSeconds_v'></span></div>

<h3>UNDERWATER (COSMIC REEF) — SCENE-SPECIFIC (not a Deepest Space audio control)</h3>
<p style='color:#666;font-size:12px'>How long sustained light drive takes to evolve the Cosmic Reef scene (World04) end-to-end - from calm/dark underwater bloom through the cosmic breach that follows it. Governs the whole visual arc, not just the initial bloom. Only affects that one scene — set it to match your song length.</p>
<div class='row'><label>Visual Evolution Time</label><input type='range' id='UnderwaterEvolutionSeconds' min='30' max='300' step='1'><span class='val' id='UnderwaterEvolutionSeconds_v'></span></div>

<h3>HYBRID TEST (PROTOTYPE) — SCENE-SPECIFIC (not a Deepest Space audio control)</h3>
<p style='color:#666;font-size:12px'>Phase 1 hybrid-video proof (World05). Blend between the hardcoded video clip layer (0) and the composited procedural child world (1). Only affects the Hybrid Test scene.</p>
<div class='row'><label>Video / World Blend</label><input type='range' id='HybridBlend' min='0' max='1' step='0.01'><span class='val' id='HybridBlend_v'></span></div>

<h3>EFFECTS (PHASE 3) — HYBRID TEST ONLY</h3>
<p style='color:#666;font-size:12px'>Effect Stack v1, applied in the composite shader after the video/world blend above. Only affects the Hybrid Test scene (World05).</p>
<div class='row'><label>Grayscale</label><input type='range' id='HybridGrayscale' min='0' max='1' step='0.01'><span class='val' id='HybridGrayscale_v'></span></div>
<div class='row'><label>Mirror X</label><input type='range' id='HybridMirrorX' min='0' max='1' step='1'><span class='val' id='HybridMirrorX_v'></span></div>
<div class='row'><label>Mirror Y</label><input type='range' id='HybridMirrorY' min='0' max='1' step='1'><span class='val' id='HybridMirrorY_v'></span></div>
<div class='row'><label>Grade Lift</label><input type='range' id='HybridGradeLift' min='-0.5' max='0.5' step='0.01'><span class='val' id='HybridGradeLift_v'></span></div>
<div class='row'><label>Grade Gamma</label><input type='range' id='HybridGradeGamma' min='0.2' max='3' step='0.01'><span class='val' id='HybridGradeGamma_v'></span></div>
<div class='row'><label>Grade Gain</label><input type='range' id='HybridGradeGain' min='0' max='2' step='0.01'><span class='val' id='HybridGradeGain_v'></span></div>
<div class='row'><label>Vignette</label><input type='range' id='HybridVignette' min='0' max='1' step='0.01'><span class='val' id='HybridVignette_v'></span></div>
<div class='row'><label>Playback Speed</label><input type='range' id='HybridPlaybackSpeed' min='0.25' max='2' step='0.01'><span class='val' id='HybridPlaybackSpeed_v'></span></div>

<h3>VISUAL COMPOSER SANDBOX (World06)</h3>
<p style='color:#666;font-size:12px'>Artistic-exploration sandbox, not a phased-roadmap scene. Cycle through hardcoded compositions and tune each layer independently. Only affects the Visual Composer scene (World06).</p>
<div class='row'><label>Composition Index</label><input type='range' id='ComposerIndex' min='0' max='8' step='1'><span class='val' id='ComposerIndex_v'></span></div>
<div class='row'><label>Video / Procedural Blend</label><input type='range' id='ComposerBlend' min='0' max='1' step='0.01'><span class='val' id='ComposerBlend_v'></span></div>
<div class='row'><label>Volumetric Density</label><input type='range' id='ComposerVolumetricDensity' min='0' max='2' step='0.01'><span class='val' id='ComposerVolumetricDensity_v'></span></div>
<div class='row'><label>Particle Density</label><input type='range' id='ComposerParticleDensity' min='0' max='2' step='0.01'><span class='val' id='ComposerParticleDensity_v'></span></div>
<div class='row'><label>Lighting</label><input type='range' id='ComposerLighting' min='0' max='2' step='0.01'><span class='val' id='ComposerLighting_v'></span></div>
<div class='row'><label>Audio Reactivity</label><input type='range' id='ComposerAudioReactivity' min='0' max='2' step='0.01'><span class='val' id='ComposerAudioReactivity_v'></span></div>
<div class='row'><label>Grayscale</label><input type='range' id='ComposerGrayscale' min='0' max='1' step='0.01'><span class='val' id='ComposerGrayscale_v'></span></div>
<div class='row'><label>Mirror X</label><input type='range' id='ComposerMirrorX' min='0' max='1' step='1'><span class='val' id='ComposerMirrorX_v'></span></div>
<div class='row'><label>Mirror Y</label><input type='range' id='ComposerMirrorY' min='0' max='1' step='1'><span class='val' id='ComposerMirrorY_v'></span></div>
<div class='row'><label>Grade Lift</label><input type='range' id='ComposerGradeLift' min='-0.5' max='0.5' step='0.01'><span class='val' id='ComposerGradeLift_v'></span></div>
<div class='row'><label>Grade Gamma</label><input type='range' id='ComposerGradeGamma' min='0.2' max='3' step='0.01'><span class='val' id='ComposerGradeGamma_v'></span></div>
<div class='row'><label>Grade Gain</label><input type='range' id='ComposerGradeGain' min='0' max='2' step='0.01'><span class='val' id='ComposerGradeGain_v'></span></div>
<div class='row'><label>Vignette</label><input type='range' id='ComposerVignette' min='0' max='1' step='0.01'><span class='val' id='ComposerVignette_v'></span></div>
<div class='row'><label>Playback Speed</label><input type='range' id='ComposerPlaybackSpeed' min='0.25' max='2' step='0.01'><span class='val' id='ComposerPlaybackSpeed_v'></span></div>

</div>

<div id='tab-calibration' class='tab-panel'>

<div class='calib-status' id='calibStatusBar'>Loading calibration status…</div>

<div class='calib-card'>
  <h4>PRESETS <span id='presetDirtyBadge' class='clip-badge test-badge'>UNSAVED</span></h4>
  <div class='row'>
    <label>Saved presets</label>
    <select class='chan-select' id='presetDropdown' style='flex:1'></select>
  </div>
  <div class='row'>
    <label>Name</label>
    <input type='text' id='presetNameField' class='chan-select' style='flex:1' placeholder='e.g. Clarett Practice Guitar'>
  </div>
  <div class='row'>
    <label>Target interface</label>
    <select class='chan-select' id='presetTargetField'>
      <option value='Generic'>Generic</option>
      <option value='Clarett'>Clarett</option>
      <option value='Scarlett'>Scarlett</option>
    </select>
  </div>
  <div class='preset-row'>
    <button class='preset-btn preset-mgmt-btn' id='presetSaveBtn'>SAVE</button>
    <button class='preset-btn preset-mgmt-btn' id='presetSaveAsBtn'>SAVE AS NEW</button>
    <button class='preset-btn preset-mgmt-btn' id='presetLoadBtn'>LOAD SELECTED</button>
    <button class='preset-btn preset-mgmt-btn' id='presetDeleteBtn' style='color:#ff5555;border-color:#ff5555'>DELETE SELECTED</button>
  </div>
  <div class='warn-text' id='presetMessage' style='display:none'></div>
  <p class='limit-text'>Presets save routing (Input A/B channel), gain/gate/smoothing/output ceiling, and the response curve for both inputs, to a local file (<code>Config/calibration-presets.json</code>) - never uploaded anywhere. Loading a preset applies immediately, including to any scene currently running. A preset saved for a Clarett's channels 3/4 will safely leave routing unchanged (with a warning) if loaded while only 2 channels are available - see REPORT.md / AUDIT.md.</p>
</div>

<div class='calib-card'>
  <h4>INPUT ROUTING</h4>
  <div class='row'>
    <label>Visual Input A</label>
    <select class='chan-select' id='chanA'></select>
  </div>
  <div class='row'>
    <label>Visual Input B</label>
    <select class='chan-select' id='chanB'></select>
  </div>
  <div class='warn-text' id='chanWarning' style='display:none'>⚠ Input A and B are set to the same channel — both meters will show identical levels.</div>
  <p class='limit-text'>Only 2 channels are currently exposed by the audio backend (stereo capture: Input 1 = Left, Input 2 = Right), regardless of how many physical inputs the connected interface has. On an 8-input Clarett, only its first two capture channels are reachable today — see REPORT.md / AUDIT.md for the planned Clarett multi-channel follow-up.</p>
</div>

<div class='calib-columns'>
  <div class='calib-card'>
    <h4>INPUT A METER <span class='clip-badge' id='clipA'>CLIP</span> <span class='clip-badge test-badge' id='testA'>TEST</span></h4>
    <div class='meter-track'><div class='meter-zone' style='left:15%;width:70%'></div><div class='meter-fill' id='fillA'></div><div class='meter-peak' id='peakA'></div></div>
    <div class='meter-track meter-sub'><div class='meter-fill' id='fillOutA' style='background:linear-gradient(90deg,#aa66ff,#ff66cc)'></div></div>
    <div class='meter-readout'>
      <span>Raw: <b id='rawA'>0.00</b></span>
      <span>Smoothed: <b id='smoothA'>0.00</b></span>
      <span>Peak: <b id='peakValA'>0.00</b></span>
      <span>Output: <b id='outA'>0.00</b></span>
    </div>
    <div class='meter-label'>TOP = INPUT LEVEL (SHADED ZONE = TARGET RANGE) &nbsp;|&nbsp; BOTTOM = POST-CURVE OUTPUT</div>
    <button class='reset-small test-pulse-btn' id='testPulseA' data-input='A' style='color:#ffbb33;border-color:#ffbb33'>SEND TEST PULSE (5s, no real audio required)</button>
  </div>
  <div class='calib-card'>
    <h4>INPUT B METER <span class='clip-badge' id='clipB'>CLIP</span> <span class='clip-badge test-badge' id='testB'>TEST</span></h4>
    <div class='meter-track'><div class='meter-zone' style='left:15%;width:70%'></div><div class='meter-fill' id='fillB'></div><div class='meter-peak' id='peakB'></div></div>
    <div class='meter-track meter-sub'><div class='meter-fill' id='fillOutB' style='background:linear-gradient(90deg,#aa66ff,#ff66cc)'></div></div>
    <div class='meter-readout'>
      <span>Raw: <b id='rawB'>0.00</b></span>
      <span>Smoothed: <b id='smoothB'>0.00</b></span>
      <span>Peak: <b id='peakValB'>0.00</b></span>
      <span>Output: <b id='outB'>0.00</b></span>
    </div>
    <div class='meter-label'>TOP = INPUT LEVEL (SHADED ZONE = TARGET RANGE) &nbsp;|&nbsp; BOTTOM = POST-CURVE OUTPUT</div>
    <button class='reset-small test-pulse-btn' id='testPulseB' data-input='B' style='color:#ffbb33;border-color:#ffbb33'>SEND TEST PULSE (5s, no real audio required)</button>
  </div>
</div>

<div class='calib-columns'>
  <div class='calib-card'>
    <h4>INPUT A RESPONSE CURVE</h4>
    <svg class='curve-svg' id='curveA' width='260' height='260' viewBox='0 0 260 260'></svg>
    <div class='preset-row' id='presetRowA'>
      <button class='preset-btn' data-input='A' data-preset='Linear'>Linear</button>
      <button class='preset-btn' data-input='A' data-preset='Sensitive'>Sensitive</button>
      <button class='preset-btn' data-input='A' data-preset='Compressed'>Compressed</button>
      <button class='preset-btn' data-input='A' data-preset='S-Curve'>S-Curve</button>
    </div>
    <h4 style='margin-top:18px'>INPUT A MANUAL CONTROLS</h4>
    <div class='manual-row'><label>Visual-control gain</label><input type='range' id='gainA' min='0' max='32' step='0.25'><span class='val' id='gainA_v'></span></div>
    <div class='manual-row'><label>Gate Threshold</label><input type='range' id='gateA' min='0' max='0.3' step='0.005'><span class='val' id='gateA_v'></span></div>
    <div class='manual-row'><label>Smoothing</label><input type='range' id='smoothingA' min='0' max='0.95' step='0.01'><span class='val' id='smoothingA_v'></span></div>
    <div class='manual-row'><label>Output Ceiling</label><input type='range' id='ceilA' min='0.1' max='1' step='0.01'><span class='val' id='ceilA_v'></span></div>
    <button class='reset-small' data-input='A'>RESET INPUT A</button>
  </div>

  <div class='calib-card'>
    <h4>INPUT B RESPONSE CURVE</h4>
    <svg class='curve-svg' id='curveB' width='260' height='260' viewBox='0 0 260 260'></svg>
    <div class='preset-row' id='presetRowB'>
      <button class='preset-btn' data-input='B' data-preset='Linear'>Linear</button>
      <button class='preset-btn' data-input='B' data-preset='Sensitive'>Sensitive</button>
      <button class='preset-btn' data-input='B' data-preset='Compressed'>Compressed</button>
      <button class='preset-btn' data-input='B' data-preset='S-Curve'>S-Curve</button>
    </div>
    <h4 style='margin-top:18px'>INPUT B MANUAL CONTROLS</h4>
    <div class='manual-row'><label>Visual-control gain</label><input type='range' id='gainB' min='0' max='32' step='0.25'><span class='val' id='gainB_v'></span></div>
    <div class='manual-row'><label>Gate Threshold</label><input type='range' id='gateB' min='0' max='0.3' step='0.005'><span class='val' id='gateB_v'></span></div>
    <div class='manual-row'><label>Smoothing</label><input type='range' id='smoothingB' min='0' max='0.95' step='0.01'><span class='val' id='smoothingB_v'></span></div>
    <div class='manual-row'><label>Output Ceiling</label><input type='range' id='ceilB' min='0.1' max='1' step='0.01'><span class='val' id='ceilB_v'></span></div>
    <button class='reset-small' data-input='B'>RESET INPUT B</button>
  </div>
</div>

<p class='limit-text'>Calibration values (gain/gate/smoothing/ceiling/curve) are not yet persisted to disk — they reset to defaults on engine restart. Scenes do not yet consume these post-curve values (Stellar Nursery and Lava Lamp still use their original raw audio path unchanged) — this tab establishes the calibration layer and meters; scene integration is a documented next step. See REPORT.md / ROADMAP.md.</p>

</div>

<script>
const defaults = {
  Smoothing: 0.40, BassFloor: 0.08, MidFloor: 0.004, TrebleFloor: 0.001,
  BassMax: 35, MidMax: 12, TrebleMax: 0.4,
  BassBrightness: 1.20, DimLevel: 0.20,
  WindTurbineFireEvolutionSeconds: 240,
  UnderwaterEvolutionSeconds: 240,
  HybridBlend: 0.5,
  HybridGrayscale: 0.0, HybridMirrorX: 0, HybridMirrorY: 0,
  HybridGradeLift: 0.0, HybridGradeGamma: 1.0, HybridGradeGain: 1.0,
  HybridVignette: 0.0, HybridPlaybackSpeed: 1.0,
  ComposerIndex: 0, ComposerBlend: 0.5,
  ComposerVolumetricDensity: 1.0, ComposerParticleDensity: 1.0,
  ComposerLighting: 1.0, ComposerAudioReactivity: 1.0,
  ComposerGrayscale: 0.0, ComposerMirrorX: 0, ComposerMirrorY: 0,
  ComposerGradeLift: 0.0, ComposerGradeGamma: 1.0, ComposerGradeGain: 1.0,
  ComposerVignette: 0.0, ComposerPlaybackSpeed: 1.0
};

const sliders = document.querySelectorAll('input[type=range]');

function send(key, val) {
  fetch('/set', { method: 'POST', body: JSON.stringify({ [key]: parseFloat(val) }) });
}

// World03 Wind Turbine Fire (Phase 1.1): this one slider gets a seconds + mm:ss
// display instead of the generic sliders' toFixed(3) - registered as an extra
// listener alongside (not instead of) the generic one below, so it always runs
// last and overrides the display text with the friendlier format.
function formatEvolutionSeconds(totalSeconds) {
  const s = Math.round(parseFloat(totalSeconds));
  const m = Math.floor(s / 60);
  const rem = s % 60;
  return s + 's (' + m + ':' + String(rem).padStart(2, '0') + ')';
}
const evolutionSlider = document.getElementById('WindTurbineFireEvolutionSeconds');
// World04 Underwater (Phase 1): reuses the same formatEvolutionSeconds mm:ss
// display helper as the Wind Turbine Fire slider above, registered as its
// own extra listener the same way.
const underwaterEvolutionSlider = document.getElementById('UnderwaterEvolutionSeconds');

sliders.forEach(s => {
  s.addEventListener('input', () => {
    document.getElementById(s.id + '_v').textContent = parseFloat(s.value).toFixed(3);
    send(s.id, s.value);
  });
});

if (evolutionSlider) {
  evolutionSlider.addEventListener('input', () => {
    document.getElementById('WindTurbineFireEvolutionSeconds_v').textContent =
      formatEvolutionSeconds(evolutionSlider.value);
  });
}

if (underwaterEvolutionSlider) {
  underwaterEvolutionSlider.addEventListener('input', () => {
    document.getElementById('UnderwaterEvolutionSeconds_v').textContent =
      formatEvolutionSeconds(underwaterEvolutionSlider.value);
  });
}

function resetDefaults() {
  sliders.forEach(s => {
    if (defaults[s.id] !== undefined) {
      s.value = defaults[s.id];
      document.getElementById(s.id + '_v').textContent = parseFloat(s.value).toFixed(3);
      send(s.id, s.value);
    }
  });
  if (evolutionSlider) {
    document.getElementById('WindTurbineFireEvolutionSeconds_v').textContent =
      formatEvolutionSeconds(evolutionSlider.value);
  }
  if (underwaterEvolutionSlider) {
    document.getElementById('UnderwaterEvolutionSeconds_v').textContent =
      formatEvolutionSeconds(underwaterEvolutionSlider.value);
  }
}

fetch('/values').then(r => r.json()).then(vals => {
  sliders.forEach(s => {
    if (vals[s.id] !== undefined) {
      s.value = vals[s.id];
      document.getElementById(s.id + '_v').textContent = parseFloat(s.value).toFixed(3);
    }
  });
  if (evolutionSlider && vals.WindTurbineFireEvolutionSeconds !== undefined) {
    document.getElementById('WindTurbineFireEvolutionSeconds_v').textContent =
      formatEvolutionSeconds(evolutionSlider.value);
  }
  if (underwaterEvolutionSlider && vals.UnderwaterEvolutionSeconds !== undefined) {
    document.getElementById('UnderwaterEvolutionSeconds_v').textContent =
      formatEvolutionSeconds(underwaterEvolutionSlider.value);
  }
});

// Scene Dashboard v0.1 --------------------------------------------------

function refreshStatus() {
  fetch('/status').then(r => r.json()).then(s => {
    const bar = document.getElementById('statusBar');
    if (!s.running) {
      // Dashboard-Only Launcher Mode: this is the normal, expected state right
      // after launching with --dashboard-only, before any scene has been picked -
      // not an error. Also covers the pre-existing idle/mid-performance-sweep case.
      bar.innerHTML = 'No visual running. Choose a scene below.';
      return;
    }
    const seedPart = (s.seed && s.seed.indexOf('n/a') === -1)
      ? ' &nbsp;|&nbsp; Seed: <b>' + s.seed + '</b>' : '';
    bar.innerHTML =
      'World: <b>' + s.world + '</b> &nbsp;|&nbsp; ' +
      'Profile: <b>' + s.profile + '</b> &nbsp;|&nbsp; ' +
      'FPS: <b>' + s.fps.toFixed(1) + '</b> &nbsp;|&nbsp; ' +
      'Target: <b>' + s.renderWidth + 'x' + s.renderHeight + '</b> &nbsp;|&nbsp; ' +
      'Audio: <b>' + (s.audioCapturing ? 'capturing' : 'stopped') + '</b>' + seedPart +
      '<br>Scene audio source: <b>' + s.sceneAudioSource + '</b> &nbsp;|&nbsp; ' +
      'Calibrated A: <b>' + s.calibratedA.toFixed(2) + '</b> &nbsp;|&nbsp; ' +
      'Calibrated B: <b>' + s.calibratedB.toFixed(2) + '</b>';
  }).catch(() => {
    document.getElementById('statusBar').textContent = 'Engine status unavailable (server not reachable).';
  });
}

let allScenes = [];

function renderScenes() {
  const showExperimental = document.getElementById('showExperimental').checked;
  const grid = document.getElementById('sceneGrid');
  grid.innerHTML = '';
  allScenes.forEach(scene => {
    if (!scene.showable) return;
    const isExperimental = scene.status.toLowerCase().indexOf('experimental') !== -1;
    if (isExperimental && !showExperimental) return;

    const seedLine = scene.showSeed
      ? '<p class=\'default-profile\'>Show Seed: ' + scene.showSeed + '</p>' : '';

    const card = document.createElement('div');
    card.className = 'scene-card';
    card.innerHTML =
      '<h4>' + scene.displayName + '</h4>' +
      '<p class=\'desc\'>' + scene.description + '</p>' +
      '<p class=\'status\'>' + scene.status + '</p>' +
      '<p class=\'default-profile\'>Default profile: ' + scene.defaultProfile + '</p>' +
      seedLine +
      '<div class=\'buttons\'>' +
        '<button data-id=\'' + scene.id + '\' data-profile=\'Safe\' class=\'launch-btn\'>Launch Safe</button>' +
        '<button data-id=\'' + scene.id + '\' data-profile=\'High\' class=\'launch-btn\'>Launch High</button>' +
        '<button class=\'restart-btn\'>Restart</button>' +
      '</div>';
    grid.appendChild(card);
  });

  grid.querySelectorAll('.launch-btn').forEach(btn => {
    btn.addEventListener('click', () => launchScene(btn.dataset.id, btn.dataset.profile));
  });
  grid.querySelectorAll('.restart-btn').forEach(btn => {
    btn.addEventListener('click', restartScene);
  });
}

function launchScene(sceneId, profile) {
  fetch('/launch', { method: 'POST', body: JSON.stringify({ sceneId: sceneId, profile: profile }) });
}

function restartScene() {
  fetch('/restart', { method: 'POST', body: '{}' });
}

function quitEngine() {
  if (!confirm('Quit Cosmic Engine?')) return;
  fetch('/quit', { method: 'POST', body: '{}' });
}

document.getElementById('showExperimental').addEventListener('change', renderScenes);

fetch('/scenes').then(r => r.json()).then(scenes => {
  allScenes = scenes;
  renderScenes();
});

refreshStatus();
setInterval(refreshStatus, 2000);

// Tabs -------------------------------------------------------------------

document.querySelectorAll('.tab-btn').forEach(btn => {
  btn.addEventListener('click', () => {
    document.querySelectorAll('.tab-btn').forEach(b => b.classList.remove('active'));
    document.querySelectorAll('.tab-panel').forEach(p => p.classList.remove('active'));
    btn.classList.add('active');
    document.getElementById('tab-' + btn.dataset.tab).classList.add('active');
  });
});

// Dashboard Calibration Tab v0.1 ------------------------------------------
// Deliberately separate from the Scene Dashboard/Tuning code above - this
// polls /calibration/* only, and never touches Tuning.cs or scene state.

const SVG_SIZE = 260, SVG_PAD = 20;
function toPx(v) { return SVG_PAD + v * (SVG_SIZE - 2 * SVG_PAD); }
function toUnit(px) { return Math.max(0, Math.min(1, (px - SVG_PAD) / (SVG_SIZE - 2 * SVG_PAD))); }

function bezierComponent(t, p1, p2) {
  const mt = 1 - t;
  return 3 * mt * mt * t * p1 + 3 * mt * t * t * p2 + t * t * t;
}
function bezierDerivative(t, p1, p2) {
  const mt = 1 - t;
  return 3 * mt * mt * p1 + 6 * mt * t * (p2 - p1) + 3 * t * t * (1 - p2);
}
function evalCurve(x, p1x, p1y, p2x, p2y) {
  x = Math.max(0, Math.min(1, x));
  let t = x;
  for (let i = 0; i < 6; i++) {
    const xt = bezierComponent(t, p1x, p2x);
    const dx = bezierDerivative(t, p1x, p2x);
    if (Math.abs(dx) < 1e-5) break;
    t -= (xt - x) / dx;
    t = Math.max(0, Math.min(1, t));
  }
  return bezierComponent(t, p1y, p2y);
}

const SVG_NS = 'http://www.w3.org/2000/svg';

class CalibCurve {
  constructor(inputKey, svgId) {
    this.input = inputKey;
    this.svg = document.getElementById(svgId);
    this.p1x = 0.33; this.p1y = 0.33; this.p2x = 0.66; this.p2y = 0.66;
    this.dotX = 0; this.dotY = 0;
    this.dragging = null;
    this.lastSent = 0;
    this.buildStatic();
    this.svg.addEventListener('pointermove', e => this.onMove(e));
    this.svg.addEventListener('pointerup', e => this.onUp(e));
    this.svg.addEventListener('pointerleave', e => this.onUp(e));
    this.redraw();
  }

  line(x1, y1, x2, y2, color, dash) {
    const l = document.createElementNS(SVG_NS, 'line');
    l.setAttribute('x1', x1); l.setAttribute('y1', y1); l.setAttribute('x2', x2); l.setAttribute('y2', y2);
    l.setAttribute('stroke', color); l.setAttribute('stroke-width', '1');
    if (dash) l.setAttribute('stroke-dasharray', dash);
    return l;
  }

  point(which) {
    const c = document.createElementNS(SVG_NS, 'circle');
    c.setAttribute('r', 8); c.setAttribute('fill', 'rgba(255,34,102,0.35)');
    c.setAttribute('stroke', '#ff2266'); c.setAttribute('stroke-width', '1.5');
    c.classList.add('curve-point');
    c.addEventListener('pointerdown', e => { this.dragging = which; e.target.setPointerCapture(e.pointerId); });
    return c;
  }

  buildStatic() {
    this.svg.innerHTML = '';
    for (let i = 1; i < 4; i++) {
      const p = SVG_PAD + i * (SVG_SIZE - 2 * SVG_PAD) / 4;
      this.svg.appendChild(this.line(SVG_PAD, p, SVG_SIZE - SVG_PAD, p, '#1a1a22'));
      this.svg.appendChild(this.line(p, SVG_PAD, p, SVG_SIZE - SVG_PAD, '#1a1a22'));
    }
    this.svg.appendChild(this.line(SVG_PAD, SVG_SIZE - SVG_PAD, SVG_SIZE - SVG_PAD, SVG_PAD, '#222', '3,3'));
    this.svg.appendChild(this.line(SVG_PAD, SVG_SIZE - SVG_PAD, SVG_SIZE - SVG_PAD, SVG_SIZE - SVG_PAD, '#555'));
    this.svg.appendChild(this.line(SVG_PAD, SVG_PAD, SVG_PAD, SVG_SIZE - SVG_PAD, '#555'));

    const xLabel = document.createElementNS(SVG_NS, 'text');
    xLabel.setAttribute('x', SVG_SIZE / 2); xLabel.setAttribute('y', SVG_SIZE - 4);
    xLabel.setAttribute('fill', '#555'); xLabel.setAttribute('font-size', '9');
    xLabel.setAttribute('text-anchor', 'middle'); xLabel.textContent = 'INPUT STRENGTH →';
    this.svg.appendChild(xLabel);

    this.path = document.createElementNS(SVG_NS, 'path');
    this.path.setAttribute('stroke', '#22ddff'); this.path.setAttribute('fill', 'none');
    this.path.setAttribute('stroke-width', '2');
    this.svg.appendChild(this.path);

    this.handleLine1 = this.line(0, 0, 0, 0, '#444'); this.svg.appendChild(this.handleLine1);
    this.handleLine2 = this.line(0, 0, 0, 0, '#444'); this.svg.appendChild(this.handleLine2);

    this.dot = document.createElementNS(SVG_NS, 'circle');
    this.dot.setAttribute('r', 5); this.dot.setAttribute('fill', '#ffdd33');
    this.dot.setAttribute('opacity', '0.95');
    this.svg.appendChild(this.dot);

    this.c1 = this.point('p1'); this.svg.appendChild(this.c1);
    this.c2 = this.point('p2'); this.svg.appendChild(this.c2);
  }

  setCurve(p1x, p1y, p2x, p2y, preset) {
    if (this.dragging) return; // don't fight an active drag with a stale poll value
    this.p1x = p1x; this.p1y = p1y; this.p2x = p2x; this.p2y = p2y;
    this.redraw();
    document.querySelectorAll('#presetRow' + this.input + ' .preset-btn').forEach(b => {
      b.classList.toggle('active-preset', b.dataset.preset === preset);
    });
  }

  setDot(x, y) {
    this.dotX = x; this.dotY = y;
    this.dot.setAttribute('cx', toPx(x));
    this.dot.setAttribute('cy', SVG_SIZE - toPx(y));
  }

  redraw() {
    let d = 'M ' + toPx(0) + ' ' + (SVG_SIZE - toPx(0));
    const N = 24;
    for (let i = 1; i <= N; i++) {
      const x = i / N;
      const y = evalCurve(x, this.p1x, this.p1y, this.p2x, this.p2y);
      d += ' L ' + toPx(x) + ' ' + (SVG_SIZE - toPx(y));
    }
    this.path.setAttribute('d', d);
    this.c1.setAttribute('cx', toPx(this.p1x)); this.c1.setAttribute('cy', SVG_SIZE - toPx(this.p1y));
    this.c2.setAttribute('cx', toPx(this.p2x)); this.c2.setAttribute('cy', SVG_SIZE - toPx(this.p2y));
    this.handleLine1.setAttribute('x1', toPx(0)); this.handleLine1.setAttribute('y1', SVG_SIZE - toPx(0));
    this.handleLine1.setAttribute('x2', toPx(this.p1x)); this.handleLine1.setAttribute('y2', SVG_SIZE - toPx(this.p1y));
    this.handleLine2.setAttribute('x1', toPx(1)); this.handleLine2.setAttribute('y1', SVG_SIZE - toPx(1));
    this.handleLine2.setAttribute('x2', toPx(this.p2x)); this.handleLine2.setAttribute('y2', SVG_SIZE - toPx(this.p2y));
    this.dot.setAttribute('cx', toPx(this.dotX));
    this.dot.setAttribute('cy', SVG_SIZE - toPx(this.dotY));
  }

  onMove(e) {
    if (!this.dragging) return;
    const rect = this.svg.getBoundingClientRect();
    const scaleX = SVG_SIZE / rect.width, scaleY = SVG_SIZE / rect.height;
    const px = (e.clientX - rect.left) * scaleX;
    const py = (e.clientY - rect.top) * scaleY;
    const x = toUnit(px);
    const y = toUnit(SVG_SIZE - py);
    if (this.dragging === 'p1') { this.p1x = x; this.p1y = y; } else { this.p2x = x; this.p2y = y; }
    this.redraw();
    const now = performance.now();
    if (now - this.lastSent > 60) { this.lastSent = now; this.send(); }
  }

  onUp(e) {
    if (!this.dragging) return;
    this.dragging = null;
    this.send();
  }

  send() {
    fetch('/calibration/curve', { method: 'POST', body: JSON.stringify({
      input: this.input, p1x: this.p1x, p1y: this.p1y, p2x: this.p2x, p2y: this.p2y
    }) });
  }
}

const curveA = new CalibCurve('A', 'curveA');
const curveB = new CalibCurve('B', 'curveB');

function fmt(v) { return (v || 0).toFixed(2); }

function updateMeter(prefix, data) {
  document.getElementById('raw' + prefix).textContent = fmt(data.rawLevel);
  document.getElementById('smooth' + prefix).textContent = fmt(data.smoothedLevel);
  document.getElementById('peakVal' + prefix).textContent = fmt(data.peakLevel);
  document.getElementById('out' + prefix).textContent = fmt(data.curveOutput);

  const fill = document.getElementById('fill' + prefix);
  fill.style.width = Math.min(100, data.smoothedLevel * 100) + '%';
  fill.classList.toggle('clip', data.clipping);
  fill.classList.toggle('hot', !data.clipping && data.smoothedLevel > 0.85);

  document.getElementById('peak' + prefix).style.left = Math.min(100, data.peakHoldLevel * 100) + '%';
  document.getElementById('fillOut' + prefix).style.width = Math.min(100, data.curveOutput * 100) + '%';
  document.getElementById('clip' + prefix).classList.toggle('active', data.clipping);
  document.getElementById('test' + prefix).classList.toggle('active', !!data.testOverrideActive);
}

const manualLoaded = { A: false, B: false };
const manualFields = [['gain', 'gain'], ['gate', 'gateThreshold'], ['smoothing', 'smoothing'], ['ceil', 'outputCeiling']];

function loadManual(prefix, data) {
  if (manualLoaded[prefix]) return;
  manualLoaded[prefix] = true;
  manualFields.forEach(([id, field]) => {
    const el = document.getElementById(id + prefix);
    el.value = data[field];
    document.getElementById(id + prefix + '_v').textContent = parseFloat(data[field]).toFixed(3);
  });
}

let channelsLoaded = false;

function refreshCalibration() {
  fetch('/calibration/status').then(r => r.json()).then(s => {
    const bar = document.getElementById('calibStatusBar');
    bar.innerHTML =
      'Audio device: <b>not exposed by backend (OpenAL capture, no device-name API used)</b> &nbsp;|&nbsp; ' +
      'Capture: <b>' + (s.audioCapturing ? 'capturing' : 'stopped') + '</b> &nbsp;|&nbsp; ' +
      'Channels available: <b>' + s.channelCount + '</b> &nbsp;|&nbsp; ' +
      'Input A: <b>' + s.channelNames[s.channelA] + '</b> &nbsp;|&nbsp; ' +
      'Input B: <b>' + s.channelNames[s.channelB] + '</b>';

    updateMeter('A', s.inputA);
    updateMeter('B', s.inputB);
    loadManual('A', s.inputA);
    loadManual('B', s.inputB);

    curveA.setCurve(s.inputA.curveP1X, s.inputA.curveP1Y, s.inputA.curveP2X, s.inputA.curveP2Y, s.inputA.preset);
    curveB.setCurve(s.inputB.curveP1X, s.inputB.curveP1Y, s.inputB.curveP2X, s.inputB.curveP2Y, s.inputB.preset);
    curveA.setDot(s.inputA.smoothedLevel, s.inputA.curveOutput);
    curveB.setDot(s.inputB.smoothedLevel, s.inputB.curveOutput);

    if (!channelsLoaded) {
      channelsLoaded = true;
      const chanASel = document.getElementById('chanA');
      const chanBSel = document.getElementById('chanB');
      s.channelNames.forEach((name, idx) => {
        const optA = document.createElement('option'); optA.value = idx; optA.textContent = name;
        chanASel.appendChild(optA);
        const optB = document.createElement('option'); optB.value = idx; optB.textContent = name;
        chanBSel.appendChild(optB);
      });
      chanASel.value = s.channelA;
      chanBSel.value = s.channelB;
    }
    document.getElementById('chanWarning').style.display = (s.channelA === s.channelB) ? 'block' : 'none';

    const snapshot = captureBaselineSnapshot(s);
    if (pendingBaselineMark) {
      presetBaseline = snapshot;
      pendingBaselineMark = false;
    }
    document.getElementById('presetDirtyBadge').classList.toggle('active', presetBaseline !== null && snapshot !== presetBaseline);
  }).catch(() => {
    document.getElementById('calibStatusBar').textContent = 'Calibration status unavailable (server not reachable).';
  });
}

function sendChannels() {
  const a = parseInt(document.getElementById('chanA').value);
  const b = parseInt(document.getElementById('chanB').value);
  fetch('/calibration/channels', { method: 'POST', body: JSON.stringify({ a: a, b: b }) });
  document.getElementById('chanWarning').style.display = (a === b) ? 'block' : 'none';
}
document.getElementById('chanA').addEventListener('change', sendChannels);
document.getElementById('chanB').addEventListener('change', sendChannels);

document.querySelectorAll('.preset-btn:not(.preset-mgmt-btn)').forEach(btn => {
  btn.addEventListener('click', () => {
    fetch('/calibration/preset', { method: 'POST', body: JSON.stringify({ input: btn.dataset.input, preset: btn.dataset.preset }) });
  });
});

document.querySelectorAll('.reset-small:not(.test-pulse-btn)').forEach(btn => {
  btn.addEventListener('click', () => {
    fetch('/calibration/reset', { method: 'POST', body: JSON.stringify({ input: btn.dataset.input }) });
    manualLoaded[btn.dataset.input] = false;
  });
});

// Calibrated Audio Reactivity Integration v0.1: a clearly-labeled test pulse -
// injects a synthetic raw level (0.7) that flows through the exact same real
// gain/gate/curve/smoothing/peak pipeline as a real guitar signal, so the
// full calibration -> scene response chain can be proven without real audio
// connected. Auto-clears after 5 seconds so it can never be left stuck on.
document.querySelectorAll('.test-pulse-btn').forEach(btn => {
  btn.addEventListener('click', () => {
    const input = btn.dataset.input;
    btn.disabled = true;
    btn.textContent = 'TEST PULSE ACTIVE (5s)...';
    fetch('/calibration/testinput', { method: 'POST', body: JSON.stringify({ input: input, value: 0.7 }) });
    setTimeout(() => {
      fetch('/calibration/testinput', { method: 'POST', body: JSON.stringify({ input: input, value: null }) });
      btn.disabled = false;
      btn.textContent = 'SEND TEST PULSE (5s, no real audio required)';
    }, 5000);
  });
});

['A', 'B'].forEach(prefix => {
  manualFields.forEach(([id, field]) => {
    const el = document.getElementById(id + prefix);
    el.addEventListener('input', () => {
      document.getElementById(id + prefix + '_v').textContent = parseFloat(el.value).toFixed(3);
      fetch('/calibration/manual', { method: 'POST', body: JSON.stringify({ input: prefix, field: field, value: parseFloat(el.value) }) });
    });
  });
});

// Calibration Presets v0.1 -------------------------------------------------
// A preset is a full-setup snapshot (routing + both inputs' manual controls
// and curves) saved by name to Config/calibration-presets.json. The Unsaved
// Changes badge below is purely a UI convenience - it compares the live
// calibration state against whatever was last saved/loaded THIS session, it
// never blocks or gates any action.

let presetBaseline = null;
let pendingBaselineMark = false;

function captureBaselineSnapshot(s) {
  const pick = inp => [inp.gain, inp.gateThreshold, inp.smoothing, inp.outputCeiling,
    inp.curveP1X, inp.curveP1Y, inp.curveP2X, inp.curveP2Y];
  return JSON.stringify({ a: s.channelA, b: s.channelB, ia: pick(s.inputA), ib: pick(s.inputB) });
}

function showPresetMessage(msg, isWarning) {
  const el = document.getElementById('presetMessage');
  el.textContent = msg;
  el.style.display = 'block';
  el.style.color = isWarning ? '#ffbb33' : '#22ddaa';
}

function refreshPresets(selectName) {
  fetch('/calibration/presets').then(r => r.json()).then(list => {
    const sel = document.getElementById('presetDropdown');
    const keep = selectName || sel.value;
    sel.innerHTML = '';
    if (list.length === 0) {
      const opt = document.createElement('option');
      opt.value = ''; opt.textContent = '(no presets saved yet)';
      sel.appendChild(opt);
    }
    list.forEach(p => {
      const opt = document.createElement('option');
      opt.value = p.name;
      opt.textContent = p.name + (p.targetInterface && p.targetInterface !== 'Generic' ? ' [' + p.targetInterface + ']' : '');
      sel.appendChild(opt);
    });
    if (keep && list.some(p => p.name === keep)) sel.value = keep;
  });
}

document.getElementById('presetDropdown').addEventListener('change', () => {
  document.getElementById('presetNameField').value = document.getElementById('presetDropdown').value;
});

document.getElementById('presetSaveBtn').addEventListener('click', () => {
  const name = document.getElementById('presetNameField').value.trim();
  if (!name) { showPresetMessage('Enter a preset name first.', true); return; }
  const target = document.getElementById('presetTargetField').value;
  fetch('/calibration/presets/save', { method: 'POST', body: JSON.stringify({ name: name, targetInterface: target }) })
    .then(r => r.json()).then(res => {
      if (res.ok) {
        showPresetMessage('Saved ' + JSON.stringify(name) + '.', false);
        refreshPresets(name);
        pendingBaselineMark = true;
      } else {
        showPresetMessage(res.error || 'Save failed.', true);
      }
    });
});

document.getElementById('presetSaveAsBtn').addEventListener('click', () => {
  const name = document.getElementById('presetNameField').value.trim();
  if (!name) { showPresetMessage('Enter a preset name first.', true); return; }
  const target = document.getElementById('presetTargetField').value;
  fetch('/calibration/presets/saveas', { method: 'POST', body: JSON.stringify({ name: name, targetInterface: target }) })
    .then(r => r.json()).then(res => {
      if (res.ok) {
        showPresetMessage('Saved as new preset ' + JSON.stringify(name) + '.', false);
        refreshPresets(name);
        pendingBaselineMark = true;
      } else {
        showPresetMessage(res.error || 'Save As failed.', true);
      }
    });
});

document.getElementById('presetLoadBtn').addEventListener('click', () => {
  const name = document.getElementById('presetDropdown').value;
  if (!name) { showPresetMessage('Select a preset to load first.', true); return; }
  fetch('/calibration/presets/load', { method: 'POST', body: JSON.stringify({ name: name }) })
    .then(r => r.json()).then(res => {
      if (res.ok) {
        showPresetMessage(res.warning ? res.warning : 'Loaded ' + JSON.stringify(res.loadedName) + '.', !!res.warning);
        document.getElementById('presetNameField').value = res.loadedName;
        manualLoaded.A = false; manualLoaded.B = false; // force sliders to re-sync from server on next poll
        pendingBaselineMark = true; // baseline captured from the NEXT status poll, once fresh values land
      } else {
        showPresetMessage(res.error || 'Load failed.', true);
      }
    });
});

document.getElementById('presetDeleteBtn').addEventListener('click', () => {
  const name = document.getElementById('presetDropdown').value;
  if (!name) { showPresetMessage('Select a preset to delete first.', true); return; }
  if (!confirm('Delete preset ' + JSON.stringify(name) + '? This cannot be undone.')) return;
  fetch('/calibration/presets/delete', { method: 'POST', body: JSON.stringify({ name: name }) })
    .then(r => r.json()).then(res => {
      showPresetMessage(res.ok ? 'Deleted ' + JSON.stringify(name) + '.' : 'Delete failed.', !res.ok);
      refreshPresets();
    });
});

refreshPresets();
refreshCalibration();
setInterval(refreshCalibration, 150);
</script>
</body>
</html>";
    }
}
