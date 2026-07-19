using System.Text.Json.Serialization;

namespace CosmicEngine.App.Audio
{
    /// <summary>
    /// Read-only, normalized control snapshot for consumers outside the render
    /// engine. This carries musical intent values, never PCM audio.
    /// </summary>
    public sealed class GuitarIntentSnapshot
    {
        [JsonPropertyName("schema_version")]
        public int SchemaVersion { get; init; } = 1;

        [JsonPropertyName("sequence")]
        public long Sequence { get; init; }

        [JsonPropertyName("captured_at_monotonic_ms")]
        public double CapturedAtMonotonicMs { get; init; }

        [JsonPropertyName("capture_active")]
        public bool CaptureActive { get; init; }

        [JsonPropertyName("guitar_a")]
        public GuitarIntentChannel GuitarA { get; init; } = new GuitarIntentChannel();

        [JsonPropertyName("guitar_b")]
        public GuitarIntentChannel GuitarB { get; init; } = new GuitarIntentChannel();

        [JsonPropertyName("interaction")]
        public float Interaction { get; init; }
    }

    public sealed class GuitarIntentChannel
    {
        [JsonPropertyName("input_level")]
        public float InputLevel { get; init; }

        [JsonPropertyName("rms_energy")]
        public float RmsEnergy { get; init; }

        [JsonPropertyName("intensity")]
        public float Intensity { get; init; }

        [JsonPropertyName("bass")]
        public float Bass { get; init; }

        [JsonPropertyName("mid")]
        public float Mid { get; init; }

        [JsonPropertyName("treble")]
        public float Treble { get; init; }

        [JsonPropertyName("attack")]
        public float Attack { get; init; }

        [JsonPropertyName("sustain")]
        public float Sustain { get; init; }

        [JsonPropertyName("silent")]
        public bool Silent { get; init; } = true;
    }
}
