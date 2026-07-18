#version 400 core
// Visual Composer Sandbox (artistic-exploration pass, NOT a phased-roadmap milestone).
// Reuses World05 HybridTest's proven seams: video texture layer + procedural child-world
// RenderTarget texture, mixed by a blend uniform, with the same Phase 3 effect-stack
// (grade/grayscale/mirror/vignette) applied last. Adds two small, composition-specific,
// GPU-cheap inline techniques that no existing world provides as a reusable service:
// a luminance-seeded drifting-particle/ember field, and an independent raking-light sweep
// that moves across the frame on its own schedule, unrelated to the source footage's own
// lighting. Per COSMIC_ENGINE_PHILOSOPHY.md: the goal is to add what a camera could never
// capture, not to hang generic fog over a video clip.
in vec2 vUv;
out vec4 FragColor;

uniform sampler2D uVideoTex;
uniform sampler2D uChildTex;
uniform int   uVideoReady;
uniform float uTime;

uniform float uBlend;              // video/child mix (Tuning.ComposerBlend)
uniform vec3  uTint;                // per-composition color tint on the particle/light layers
uniform float uParticleSeed;        // per-composition hash offset so fields don't look identical
uniform int   uParticlesEnabled;
uniform float uParticleDensity;     // Tuning.ComposerParticleDensity
uniform float uLightRakeAngle;      // radians
uniform float uLightRakeSpeed;      // cycles/sec
uniform float uLighting;            // Tuning.ComposerLighting
uniform float uVolumetricDensity;   // Tuning.ComposerVolumetricDensity (fed into child world upstream; also scales child mix here)
uniform int   uDissolveAtTail;      // 1 = ramp toward child+particles as uLoopPhase approaches 1
uniform float uLoopPhase;           // 0..1, this composition's position within its own loop window
uniform float uAudioNudge;          // calibrated audio, additive, already scaled by ComposerAudioReactivity

// --- Effect stack (ported unchanged from World05 hybrid.frag) ---
uniform int   uMirrorX;
uniform int   uMirrorY;
uniform float uGrayscale;
uniform float uGradeLift;
uniform float uGradeGamma;
uniform float uGradeGain;
uniform float uVignette;

float hash(vec2 p)
{
    return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453123);
}

// Cheap cell-based drifting motes: NOT a general particle system - a single inline technique
// sized for this shader only. Each screen cell spawns a mote whose upward drift speed and
// twinkle phase are derived from a hash of the cell + uParticleSeed, so different compositions
// get visually distinct fields from the same code. Brightness is gated by local video luminance
// so motes read as "embers/spores drawn out of the footage" rather than random noise on top.
float motes(vec2 uv, float videoLuma, float t)
{
    float density = 22.0; // cells across the frame
    vec2 grid = uv * density;
    vec2 cell = floor(grid);
    vec2 f = fract(grid);

    float h = hash(cell + uParticleSeed);
    float drift = fract(t * (0.05 + 0.10 * h) + h * 7.0);
    vec2 center = vec2(hash(cell + 1.7), 1.0 - drift); // rise toward the top of the cell over time
    float d = length(f - center);
    float twinkle = 0.5 + 0.5 * sin(t * (2.0 + 3.0 * h) + h * 30.0);
    float spot = smoothstep(0.10, 0.0, d) * twinkle;

    float lumaGate = smoothstep(0.15, 0.55, videoLuma); // only bright source pixels spawn motes
    return spot * mix(0.15, 1.0, lumaGate);
}

// Independent raking light: a soft diagonal band that sweeps across the frame on its own
// uTime-only schedule, unrelated to anything in the source footage - a lighting decision the
// renderer makes on top of the plate, not a re-derivation of the plate's own lighting.
float rakingLight(vec2 uv, float t)
{
    vec2 dir = vec2(cos(uLightRakeAngle), sin(uLightRakeAngle));
    float phase = fract(t * uLightRakeSpeed);
    float pos = dot(uv - 0.5, dir) + 0.5;
    float band = smoothstep(0.0, 0.18, 0.18 - abs(pos - phase));
    return band;
}

void main()
{
    vec2 uv = vUv;
    if (uMirrorX == 1) uv.x = 1.0 - uv.x;
    if (uMirrorY == 1) uv.y = 1.0 - uv.y;

    vec3 childColor = texture(uChildTex, uv).rgb * clamp(uVolumetricDensity, 0.0, 2.0);

    vec3 videoColor;
    if (uVideoReady == 1)
    {
        videoColor = texture(uVideoTex, uv).rgb;
    }
    else
    {
        videoColor = vec3(0.02, 0.03, 0.05) + 0.01 * sin(uTime);
    }
    float videoLuma = dot(videoColor, vec3(0.2126, 0.7152, 0.0722));

    float blend = clamp(uBlend, 0.0, 1.0);
    if (uDissolveAtTail == 1)
    {
        // In the final ~25% of the loop, dissolve the video plate away in favor of the
        // procedural/particle layer rather than hard-cutting at the loop point.
        float tailRamp = smoothstep(0.75, 1.0, uLoopPhase);
        blend = clamp(blend + tailRamp * 0.6, 0.0, 1.0);
    }

    vec3 color = mix(videoColor, childColor, blend);

    if (uParticlesEnabled == 1)
    {
        float m = motes(uv, videoLuma, uTime) * clamp(uParticleDensity, 0.0, 2.0);
        color += uTint * m * (0.6 + 0.8 * uAudioNudge);
    }

    float raking = rakingLight(uv, uTime) * clamp(uLighting, 0.0, 2.0);
    color += uTint * raking * 0.35 * (0.5 + 0.5 * uAudioNudge);

    // Effect stack (unchanged from World05 hybrid.frag) applied last.
    color = color * uGradeGain + uGradeLift;
    color = max(color, 0.0);
    color = pow(color, vec3(1.0 / max(uGradeGamma, 0.01)));

    float luma = dot(color, vec3(0.2126, 0.7152, 0.0722));
    color = mix(color, vec3(luma), clamp(uGrayscale, 0.0, 1.0));

    vec2 centered = vUv - 0.5;
    float dist = length(centered) * 1.4142;
    float vig = 1.0 - clamp(uVignette, 0.0, 1.0) * smoothstep(0.3, 1.0, dist);
    color *= vig;

    FragColor = vec4(clamp(color, 0.0, 1.0), 1.0);
}
