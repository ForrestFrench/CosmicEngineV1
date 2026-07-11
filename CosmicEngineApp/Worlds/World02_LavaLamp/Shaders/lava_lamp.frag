#version 330 core
in vec2 vUV;
out vec4 fragColor;

// LAVA LAMP / ANALOG PSYCH - World 02 (draft v0.1)
//
// Cheap 2D metaball field, NOT a volumetric raymarch: a handful of analytic
// soft blobs orbiting/drifting over time, blended with smoothstep only (no
// hard step() masks anywhere), over a slowly animated background gradient.
// Deliberately much cheaper than Stellar Nursery - a small fixed loop, no
// noise/hash sampling at all, no raymarch steps - safe on integrated GPUs.
//
// Guitar 1 (Creator): color intensity, hue shift, glow/pulse strength.
// Guitar 2 (Sculptor): blob size, distortion/wobble amount + frequency.
// Every motion/color term has a baseline driven by uTime alone, so the scene
// stays fully visible and animated under silence - audio only modulates on
// top of that floor, it never gates the scene to black.

uniform float uTime;
uniform int   uBlobCount;   // P1-aware: Safe uses fewer, High may use a few more - same shader either way

uniform float uColorIntensity;
uniform float uHueShift;
uniform float uGlowStrength;

uniform float uBlobSize;
uniform float uDistortion;
uniform float uWobbleFreqMul;

uniform float uBlobSpeed;

const int MAX_BLOBS = 8;

// Cheap cosine color palette (Inigo Quilez's well-known general-purpose
// formula) - a standard technique for saturated gradients, not tied to any
// specific reference shader. a/b/c/d tuned here for a warm/cool psych palette.
vec3 palette(float t) {
    vec3 a = vec3(0.55, 0.35, 0.55);
    vec3 b = vec3(0.45, 0.45, 0.35);
    vec3 c = vec3(1.00, 0.80, 0.60);
    vec3 d = vec3(0.25, 0.55, 0.75);
    return a + b * cos(6.28318 * (c * t + d));
}

void main() {
    vec2 uv = vUV;
    uv.y = 1.0 - uv.y;

    // Aspect-corrected centered coordinates (matches Stellar Nursery's 16:9 assumption).
    float aspect = 1.7778;
    vec2 p = (uv - 0.5);
    p.x *= aspect;

    // Domain warp: cheap sin/cos wobble, no extra noise sampling. uDistortion
    // has a nonzero floor set in C# even under silence, so the field is never
    // perfectly rigid.
    vec2 warp = vec2(
        sin(p.y * 3.0 + uTime * uWobbleFreqMul * 0.6),
        cos(p.x * 3.0 + uTime * uWobbleFreqMul * 0.5)
    ) * uDistortion * 0.15;
    vec2 wp = p + warp;

    // Metaball field: sum of soft inverse-square falloffs from a handful of
    // analytically-orbiting blob centers. Cheap, fixed-size loop, no raymarch.
    float field = 0.0;
    for (int i = 0; i < MAX_BLOBS; i++) {
        if (i >= uBlobCount) break;

        float fi = float(i);
        float speed  = uBlobSpeed * (0.12 + 0.04 * fi);
        float angle  = uTime * speed + fi * 2.399963; // golden-angle-ish spacing, avoids visible symmetry
        float orbitR = 0.30 + 0.10 * sin(fi * 1.7 + uTime * 0.05);
        vec2  center = orbitR * vec2(cos(angle), sin(angle) * 0.8);

        float r  = (0.09 + 0.025 * sin(fi * 3.1 + 1.0)) * uBlobSize;
        float d2 = dot(wp - center, wp - center);
        field += (r * r) / max(d2, 0.0008);
    }

    // Soft threshold (smoothstep only - no hard step() edges) turns the raw
    // field into a 0..1 blob "shape" mask with a gradual, glowing boundary.
    float shape = smoothstep(0.65, 1.55, field);
    float glow  = smoothstep(0.15, 0.95, field) * uGlowStrength;

    // Slowly animated background gradient - visible and moving even where
    // shape is 0, so the frame is never a flat/empty void.
    float bgT = 0.15 + 0.05 * sin(uTime * 0.03) + uv.y * 0.18 + uHueShift * 0.30;
    vec3  bg  = palette(bgT) * 0.30;

    // Color driven by `shape` (bounded 0..1), not the raw unbounded `field` -
    // `field` grows very large near each blob center (1/d^2), so using it
    // directly made sin() cycle through several colors per blob (a visible
    // "target/bullseye" ring pattern). Using the bounded shape value gives one
    // smooth color sweep from each blob's edge to its center instead.
    float blobT     = 0.50 + 0.30 * sin(shape * 3.0 + uTime * 0.05) + uHueShift;
    vec3  blobColor = palette(blobT) * uColorIntensity;

    vec3 color = mix(bg, blobColor, shape);

    // Cheap in-shader "glow": additive halo from the wider/softer field band,
    // pulsing slowly with uTime and scaled by Guitar 1 energy - approximates a
    // bloom pass without an extra blur/render target. Gated by (1.0 - shape) so
    // it only adds at the blob's soft edge, not on top of an already-opaque
    // core (that stacking was washing blob centers out to near-white).
    float pulse = 0.7 + 0.3 * sin(uTime * 1.3);
    color += blobColor * glow * (1.0 - shape) * pulse * 0.6;

    fragColor = vec4(clamp(color, 0.0, 1.0), 1.0);
}
