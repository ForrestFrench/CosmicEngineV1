#version 400 core
// Phase 1 Hybrid Proof composite shader - deliberately minimal per architect scope:
// one blend uniform, no blend modes, no effect stack.
in vec2 vUv;
out vec4 FragColor;

uniform sampler2D uVideoTex;
uniform sampler2D uChildTex;
uniform float uBlend;      // 0 = pure video, 1 = pure child (procedural) world
uniform int   uVideoReady; // 0 if the decoder failed to open (VisionBoard clip missing/ffmpeg absent)
uniform float uTime;

// --- Phase 3 Effect Stack v1 (MILESTONE_BREAKDOWN.md Phase 3) ---
// Uniform-driven, applied AFTER the existing video/child mix(), in the existing composite shader -
// no extra passes/framebuffers, per spec. Order below is deliberate and fixed (mirror -> grade ->
// grayscale -> vignette) so combined-effect results are predictable/reproducible.
uniform int   uMirrorX;      // 1 = flip U before sampling both layers
uniform int   uMirrorY;      // 1 = flip V before sampling both layers
uniform float uGrayscale;    // 0 = full color, 1 = full luminance mix
uniform float uGradeLift;    // -0.5..0.5, additive shift on shadows/overall (lift)
uniform float uGradeGamma;   // 0.2..3.0, pow() midtone curve (gamma)
uniform float uGradeGain;    // 0..2, multiplicative highlight scale (gain)
uniform float uVignette;     // 0 = none, 1 = strong edge darkening

void main()
{
    // Mirror: flip sample UV for BOTH layers so the whole composite (video + child + later
    // effects) mirrors together, not just one layer - applied before any texture() read.
    vec2 uv = vUv;
    if (uMirrorX == 1) uv.x = 1.0 - uv.x;
    if (uMirrorY == 1) uv.y = 1.0 - uv.y;

    vec3 childColor = texture(uChildTex, uv).rgb;

    vec3 videoColor;
    if (uVideoReady == 1)
    {
        videoColor = texture(uVideoTex, uv).rgb;
    }
    else
    {
        // Decoder-unavailable fallback: a dim, clearly-synthetic dark-teal field (not black, not
        // magenta-placeholder - just enough to visually communicate "no clip loaded" without being
        // mistaken for a real video frame) so the composite still demonstrates the child-world/
        // blend machinery even when ffmpeg or the clip is unavailable.
        videoColor = vec3(0.02, 0.05, 0.06) + 0.01 * sin(uTime);
    }

    // Existing Phase 1 hybrid blend - fully preserved.
    vec3 color = mix(videoColor, childColor, clamp(uBlend, 0.0, 1.0));

    // Color grade: simple lift/gamma/gain, applied per-channel.
    // lift: additive shadow shift; gain: multiplicative highlight scale; gamma: midtone curve.
    color = color * uGradeGain + uGradeLift;
    color = max(color, 0.0);
    color = pow(color, vec3(1.0 / max(uGradeGamma, 0.01)));

    // Grayscale: mix toward Rec.709 luminance.
    float luma = dot(color, vec3(0.2126, 0.7152, 0.0722));
    color = mix(color, vec3(luma), clamp(uGrayscale, 0.0, 1.0));

    // Vignette: radial darkening from center, in the (pre-mirror) screen UV so it always frames
    // the visible composite regardless of mirror state.
    vec2 centered = vUv - 0.5;
    float dist = length(centered) * 1.4142; // normalize so corners reach ~1.0
    float vig = 1.0 - clamp(uVignette, 0.0, 1.0) * smoothstep(0.3, 1.0, dist);
    color *= vig;

    FragColor = vec4(clamp(color, 0.0, 1.0), 1.0);
}
