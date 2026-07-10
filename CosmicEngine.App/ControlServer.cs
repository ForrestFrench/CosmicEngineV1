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

        public static void Start()
        {
            _listener = new HttpListener();
            _listener.Prefixes.Add("http://localhost:8080/");
            _listener.Start();

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
                    audioCapturing = app?.AudioCapturing ?? false,
                    seed           = app?.CurrentSeedInfo ?? "-"
                });
                Respond(ctx, 200, json, "application/json");
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
                CosmicEngineApp.Current?.RequestSwitch(sceneId, profile);
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
                CosmicEngineApp.Current?.RequestQuit();
                Respond(ctx, 200, "{}");
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
                    Tuning.DimLevel
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
</style>
</head>
<body>

<h1>COSMIC ENGINE — SCENE DASHBOARD</h1>
<p style='color:#666;font-size:12px'>Launch, review, or switch scenes without typing CLI commands.</p>

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

<script>
const defaults = {
  Smoothing: 0.40, BassFloor: 0.08, MidFloor: 0.004, TrebleFloor: 0.001,
  BassMax: 35, MidMax: 12, TrebleMax: 0.4,
  BassBrightness: 1.20, DimLevel: 0.20
};

const sliders = document.querySelectorAll('input[type=range]');

function send(key, val) {
  fetch('/set', { method: 'POST', body: JSON.stringify({ [key]: parseFloat(val) }) });
}

sliders.forEach(s => {
  s.addEventListener('input', () => {
    document.getElementById(s.id + '_v').textContent = parseFloat(s.value).toFixed(3);
    send(s.id, s.value);
  });
});

function resetDefaults() {
  sliders.forEach(s => {
    if (defaults[s.id] !== undefined) {
      s.value = defaults[s.id];
      document.getElementById(s.id + '_v').textContent = parseFloat(s.value).toFixed(3);
      send(s.id, s.value);
    }
  });
}

fetch('/values').then(r => r.json()).then(vals => {
  sliders.forEach(s => {
    if (vals[s.id] !== undefined) {
      s.value = vals[s.id];
      document.getElementById(s.id + '_v').textContent = parseFloat(s.value).toFixed(3);
    }
  });
});

// Scene Dashboard v0.1 --------------------------------------------------

function refreshStatus() {
  fetch('/status').then(r => r.json()).then(s => {
    const bar = document.getElementById('statusBar');
    if (!s.running) {
      bar.innerHTML = 'Engine not running a normal show session right now ' +
                       '(idle, or mid performance-sweep). Status will resume once a normal run is active.';
      return;
    }
    const seedPart = (s.seed && s.seed.indexOf('n/a') === -1)
      ? ' &nbsp;|&nbsp; Seed: <b>' + s.seed + '</b>' : '';
    bar.innerHTML =
      'World: <b>' + s.world + '</b> &nbsp;|&nbsp; ' +
      'Profile: <b>' + s.profile + '</b> &nbsp;|&nbsp; ' +
      'FPS: <b>' + s.fps.toFixed(1) + '</b> &nbsp;|&nbsp; ' +
      'Target: <b>' + s.renderWidth + 'x' + s.renderHeight + '</b> &nbsp;|&nbsp; ' +
      'Audio: <b>' + (s.audioCapturing ? 'capturing' : 'stopped') + '</b>' + seedPart;
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
</script>
</body>
</html>";
    }
}
