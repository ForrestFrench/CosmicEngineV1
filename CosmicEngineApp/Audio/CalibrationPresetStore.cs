using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CosmicEngine.App.Audio
{
    /// <summary>
    /// Calibration Presets v0.1: simple local JSON persistence for named
    /// CalibrationPreset snapshots. Deliberately a plain file, not a database - a
    /// handful of named setups (a few dozen at most) never justifies one, and a
    /// flat JSON file is trivially inspectable/editable/backed-up by the user.
    ///
    /// Storage location: "Config/calibration-presets.json", relative to the
    /// process's working directory - the same convention every other relative
    /// path in this codebase already uses (Shaders/, DiagnosticReports/), which is
    /// always the CosmicEngineApp/ project folder in every documented way this app
    /// is launched (dotnet run, run-show.sh, the double-clickable .command
    /// wrapper). No OS-specific "app support" directory is used, since this
    /// project has no precedent for one and adding it here would be a bigger,
    /// less consistent change than following the pattern already established.
    ///
    /// The file is deliberately NOT tracked in git (see .gitignore) - it is local
    /// runtime user data (which setups a given performer has saved), not source.
    /// Built-in default presets are seeded into it on first run only, and only
    /// when the file does not already exist - an existing file (even an empty
    /// "presets": [] one) is never touched by the default-seeding logic, so a
    /// user's own presets can never be silently overwritten by an app update.
    /// </summary>
    public static class CalibrationPresetStore
    {
        private const int SchemaVersion = 1;
        private static readonly string DirPath = Path.Combine("Config");
        private static readonly string FilePath = Path.Combine(DirPath, "calibration-presets.json");
        private static readonly object _lock = new object();

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        private class PresetFile
        {
            [JsonPropertyName("version")]
            public int Version { get; set; } = SchemaVersion;

            [JsonPropertyName("presets")]
            public List<CalibrationPreset> Presets { get; set; } = new List<CalibrationPreset>();
        }

        public class PresetSummary
        {
            public string Name { get; set; } = "";
            public string UpdatedAt { get; set; } = "";
            public string TargetInterface { get; set; } = "";
        }

        /// <summary>Lightweight list for the dashboard's preset dropdown - names/metadata only, not full curve data.</summary>
        public static List<PresetSummary> List()
        {
            lock (_lock)
            {
                var file = LoadOrInitialize();
                return file.Presets
                    .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(p => new PresetSummary { Name = p.Name, UpdatedAt = p.UpdatedAt, TargetInterface = p.TargetInterface })
                    .ToList();
            }
        }

        public static CalibrationPreset? Get(string name)
        {
            lock (_lock)
            {
                var file = LoadOrInitialize();
                return file.Presets.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
            }
        }

        /// <summary>Create-or-update by name (case-insensitive) - this is "Save" (updates the active preset).</summary>
        public static void Upsert(CalibrationPreset preset)
        {
            lock (_lock)
            {
                var file = LoadOrInitialize();
                int existing = file.Presets.FindIndex(p => string.Equals(p.Name, preset.Name, StringComparison.OrdinalIgnoreCase));
                if (existing >= 0)
                {
                    preset.CreatedAt = file.Presets[existing].CreatedAt; // preserve original creation time
                    file.Presets[existing] = preset;
                }
                else
                {
                    file.Presets.Add(preset);
                }
                Persist(file);
            }
        }

        /// <summary>Create under a brand-new name only - this is "Save As New". Returns false if the name is already taken.</summary>
        public static bool SaveAsNew(CalibrationPreset preset)
        {
            lock (_lock)
            {
                var file = LoadOrInitialize();
                if (file.Presets.Any(p => string.Equals(p.Name, preset.Name, StringComparison.OrdinalIgnoreCase)))
                    return false;
                file.Presets.Add(preset);
                Persist(file);
                return true;
            }
        }

        public static bool Delete(string name)
        {
            lock (_lock)
            {
                var file = LoadOrInitialize();
                int removed = file.Presets.RemoveAll(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
                if (removed > 0) Persist(file);
                return removed > 0;
            }
        }

        // --- internal file handling -------------------------------------------

        private static PresetFile LoadOrInitialize()
        {
            try
            {
                if (!File.Exists(FilePath))
                {
                    var seeded = new PresetFile { Version = SchemaVersion, Presets = BuildDefaults() };
                    Persist(seeded);
                    Console.WriteLine($"[CalibrationPresets] No preset file found - created {FilePath} with 4 built-in defaults.");
                    return seeded;
                }

                string json = File.ReadAllText(FilePath);
                var file = JsonSerializer.Deserialize<PresetFile>(json, JsonOptions);
                if (file == null) throw new JsonException("Deserialized to null.");
                file.Presets ??= new List<CalibrationPreset>();
                return file;
            }
            catch (Exception ex)
            {
                // Never crash on a missing/corrupt preset file (required by the
                // brief) - and never silently discard a corrupt file either: move
                // it aside with a timestamp so the user's data isn't lost, log
                // clearly, and start fresh with the built-in defaults.
                Console.WriteLine($"[CalibrationPresets] Could not read {FilePath} ({ex.GetType().Name}: {ex.Message}) - preserving it and starting fresh with defaults.");
                TryQuarantineCorruptFile();
                var fresh = new PresetFile { Version = SchemaVersion, Presets = BuildDefaults() };
                Persist(fresh);
                return fresh;
            }
        }

        private static void TryQuarantineCorruptFile()
        {
            try
            {
                if (!File.Exists(FilePath)) return;
                string quarantinePath = FilePath + $".corrupt-{DateTime.UtcNow:yyyyMMdd_HHmmss}";
                File.Move(FilePath, quarantinePath);
                Console.WriteLine($"[CalibrationPresets] Unreadable file preserved at {quarantinePath}.");
            }
            catch
            {
                // Best-effort only - if even this fails, proceed with in-memory
                // defaults rather than crash the engine over a preset file.
            }
        }

        private static void Persist(PresetFile file)
        {
            Directory.CreateDirectory(DirPath);
            string json = JsonSerializer.Serialize(file, JsonOptions);
            File.WriteAllText(FilePath, json);
        }

        private static List<CalibrationPreset> BuildDefaults()
        {
            string now = DateTime.UtcNow.ToString("o");
            List<CalibrationPreset> Make(string name, string curvePreset)
            {
                var temp = new InputCalibration();
                temp.ApplyPreset(curvePreset);
                var data = CalibrationPreset.InputPresetData.CaptureFrom(temp);
                return new List<CalibrationPreset>
                {
                    new CalibrationPreset
                    {
                        Name = name,
                        CreatedAt = now,
                        UpdatedAt = now,
                        Notes = "Built-in default, seeded on first run.",
                        TargetInterface = "Generic",
                        ChannelA = 0,
                        ChannelB = 1,
                        InputA = data,
                        InputB = data,
                        AudioDeviceName = null,
                        ChannelCountAtSave = CalibrationEngine.ChannelCount
                    }
                };
            }

            var defaults = new List<CalibrationPreset>();
            defaults.AddRange(Make("Linear Default", "Linear"));
            defaults.AddRange(Make("Sensitive", "Sensitive"));
            defaults.AddRange(Make("Compressed", "Compressed"));
            defaults.AddRange(Make("S-Curve", "S-Curve"));
            return defaults;
        }
    }
}
