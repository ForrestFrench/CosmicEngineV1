#version 330 core
in vec2 vUV;
out vec4 fragColor;

// LAVA LAMP / ANALOG PSYCH - World 02 (v0.2 - Analog Liquid Light Prototype)
//
// Still a cheap 2D metaball field, NOT a volumetric raymarch - v0.1's whole
// point (safe on integrated GPUs, cheap fixed-size loops, no noise/hash
// sampling) is preserved. v0.2 upgrades the LOOK from "simple orbiting
// circles" toward an analog liquid-light/oil-projector feel using only
// analytic sin/cos math (no new textures, no new uniforms, no C# changes):
//   - blobs are angularly lobed (not perfectly circular) and slowly writhe
//   - blobs rise/fall + drift instead of orbiting on a fixed circular path
//   - a second, slower/dimmer "back" layer adds depth/parallax
//   - a cheap multi-frequency interference pattern gives blobs internal
//     texture instead of a flat gradient fill
//   - the palette is re-tuned warm (amber/orange/magenta) for the front
//     layer with the back layer sampling a cooler part of the same palette,
//     for tasteful warm/cool contrast instead of an all-purple wash
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
// specific reference shader. v0.2: re-tuned so low t (~0.0-0.35, where the
// front/main blob layer lives) reads warm amber/orange/magenta, and a
// separate mid-t band (used by the back layer, see backT below) reads
// cooler blue/violet - giving the "warm/cool color mixing" and "occasional
// cyan/blue contrast" called for in the art direction without the palette
// randomly wandering between the two.
vec3 palette(float t) {
    vec3 a = vec3(0.58, 0.32, 0.38);
    vec3 b = vec3(0.42, 0.32, 0.42);
    vec3 c = vec3(0.85, 0.70, 0.55);
    vec3 d = vec3(0.05, 0.15, 0.55);
    return a + b * cos(6.28318 * (c * t + d));
}

// One metaball-field layer: a handful of analytically-moving soft blobs,
// each angularly lobed (not circular) for an organic, non-"screensaver
// blob" silhouette. Shared by both the front and back layers below (called
// twice with different parameters) rather than duplicating the loop.
//
// Motion (v0.2): each blob rises and falls (yPos, full-range sine) with a
// slower two-frequency horizontal drift (xDrift) layered on top, instead of
// v0.1's fixed circular orbit - reads as blobs actually rising/sinking like
// a real lava lamp's convection currents, with gentle organic drift rather
// than a mechanical orbit path.
//
// Shape (v0.2): `lobe` modulates the effective radius by the angle from
// each blob's own center, using two different angular frequencies/phases
// per blob (via `fi`) so no two blobs share the same silhouette, and the
// whole lobe pattern slowly rotates with uTime - the blob's edge visibly
// writhes/breathes over 5-15s instead of staying a static circle.
float layerField(vec2 wp, int count, float speedMul, float sizeMul, float seedOffset) {
    float field = 0.0;
    for (int i = 0; i < MAX_BLOBS; i++) {
        if (i >= count) break;

        float fi = float(i) + seedOffset;
        float phase  = fi * 2.399963; // golden-angle-ish spacing, avoids visible symmetry between blobs
        float vSpeed = uBlobSpeed * speedMul * (0.035 + 0.012 * fi);
        float hSpeed = uBlobSpeed * speedMul * (0.05  + 0.017 * fi);

        float yPos   = 0.50 * sin(uTime * vSpeed + phase);
        float xDrift = 0.26 * sin(uTime * hSpeed * 0.75 + phase * 1.31)
                     + 0.09 * sin(uTime * hSpeed * 1.85 + phase * 0.62);
        vec2  center = vec2(xDrift, yPos);

        vec2  diff = wp - center;
        float ang  = atan(diff.y, diff.x);
        float lobe = 1.0
                   + 0.30 * sin(ang * 3.0 + fi * 1.9 + uTime * 0.10)
                   + 0.14 * sin(ang * 5.0 - fi * 2.7 - uTime * 0.14);
        float d2 = dot(diff, diff) / max(lobe * lobe, 0.2);

        float r = (0.09 + 0.025 * sin(fi * 3.1 + 1.0)) * uBlobSize * sizeMul;
        field += (r * r) / max(d2, 0.0008);
    }
    return field;
}

// Cheap internal texture (v0.2): a sum of three sine waves at different
// frequencies/phases, two of them evaluated on a ROTATED copy of the input
// coordinate (q2, ~40 degrees off-axis from q1) rather than all three on the
// same x/y axes. First version sampled all three directly on wp.x/wp.y with
// small-integer frequency ratios (9:5, 4:11) - at higher uColorIntensity
// (i.e. louder audio) this constructively interfered into a strong, obvious
// diagonal checkerboard/grid, exactly the "obvious square/cell artifact"
// the art direction explicitly rules out (caught via a mock-peak-audio
// screenshot check, not just guessed). Rotating two of the three samples
// off-axis breaks that axis-aligned beat pattern - a standard technique for
// avoiding grid artifacts in sine-sum "interference" textures.
float innerTexture(vec2 wp, float t) {
    vec2 q2 = vec2(wp.x * 0.766 - wp.y * 0.643, wp.x * 0.643 + wp.y * 0.766);
    float n1 = sin(wp.x * 5.3 + wp.y * 3.1 + t * 0.35);
    float n2 = sin(q2.x  * 4.1 - q2.y * 6.7 - t * 0.28 + 1.7);
    float n3 = sin((wp.x * 0.6 + q2.y * 0.8) * 4.7 + t * 0.5 - 0.6);
    return n1 * 0.4 + n2 * 0.35 + n3 * 0.25;
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

    // Back layer (v0.2 - depth/parallax): fewer, larger, slower, dimmer
    // blobs sampled at a "zoomed out" coordinate (wp * 0.8) for a cheap
    // sense of distance, with a large seed offset so its motion never
    // syncs with the front layer. Profile-aware like the front layer, but
    // capped low - this is a background layer, it should stay cheap and
    // stay visually secondary.
    int   backCount = uBlobCount >= 7 ? 3 : 2;
    float backField = layerField(wp * 0.80, backCount, 0.55, 1.7, 37.0);
    float backShape  = smoothstep(0.55, 1.45, backField);
    float backGlow   = smoothstep(0.12, 0.85, backField) * uGlowStrength * 0.5;

    // Front layer (v0.2 - main blobs): same field function, full profile
    // blob count, normal speed/size.
    float frontField = layerField(wp, uBlobCount, 1.0, 1.0, 0.0);
    float frontShape = smoothstep(0.65, 1.55, frontField);
    float frontGlow  = smoothstep(0.15, 0.95, frontField) * uGlowStrength;

    // Slowly animated background gradient - visible and moving even where
    // both layers are 0, so the frame is never a flat/empty void. Deep,
    // low-brightness warm-red/purple per the art direction.
    float bgT = 0.15 + 0.05 * sin(uTime * 0.03) + uv.y * 0.18 + uHueShift * 0.30;
    vec3  bg  = palette(bgT) * 0.28;

    // Back layer color: sampled from a deliberately different (cooler) part
    // of the same palette than the front layer (see palette() comment),
    // dimmer and composited at partial opacity even at full shape - reads
    // as a translucent layer of liquid behind the main blobs, not a second
    // set of opaque circles.
    float backT     = 0.50 + 0.18 * sin(backShape * 2.2 - uTime * 0.04) + uHueShift * 0.5;
    vec3  backColor = palette(backT) * uColorIntensity * 0.55;
    vec3  withBack  = mix(bg, backColor, backShape * 0.62);

    // Front layer color, driven by the bounded `shape` value (not the raw
    // unbounded field - see v0.1 comment history) plus the internal
    // interference texture, gated to only show inside the blob (`* shape`).
    // First tuned at 0.16, which read as barely-visible on screen - raised to
    // 0.26 after a direct zoomed-crop check so the sheen actually reads at
    // normal viewing distance, not just under a pixel-level crop.
    // uHueShift's contribution to frontT was 0.6 at first - checked at a
    // simulated audio peak (uHueShift maxes at 0.6) and it fully flipped the
    // palette from warm amber/orange to blue/violet, i.e. a full hue swap,
    // not the "modestly increase brightness/saturation" the art direction
    // asks for. Cut to 0.15 so peak audio nudges the hue (toward magenta/
    // red) without leaving the warm family; uColorIntensity (already audio-
    // driven) does the actual brightness/saturation lift.
    float frontT     = 0.12 + 0.16 * sin(frontShape * 3.0 + uTime * 0.05) + uHueShift * 0.15;
    vec3  frontColor = palette(frontT) * uColorIntensity;
    // `wp * 6.0` (first version) pushed innerTexture's already-textured
    // internal frequencies (~3-7) up to an effective ~20-40 - many cycles
    // across a single blob's ~0.1-0.2 unit radius, which read as a regular
    // dot/grid pattern (caught via a mock-peak-audio screenshot check, where
    // higher uColorIntensity made it much more visible). A blob is small, so
    // the coordinate needs to stay close to 1:1 scale to keep at most 1-2
    // visible cycles across it - reads as soft marbling, not a grid.
    float tex        = innerTexture(wp * 1.4, uTime);
    frontColor *= 1.0 + 0.26 * tex * frontShape;

    vec3 color = mix(withBack, frontColor, frontShape);

    // Cheap in-shader "glow": additive halo from the wider/softer field band,
    // pulsing slowly with uTime and scaled by Guitar 1 energy - approximates a
    // bloom pass without an extra blur/render target. Gated by (1 - shape) so
    // it only adds at each layer's soft edge, not on top of an already-opaque
    // core.
    float pulse = 0.7 + 0.3 * sin(uTime * 1.3);
    color += frontColor * frontGlow * (1.0 - frontShape) * pulse * 0.6;
    color += backColor  * backGlow  * (1.0 - backShape)  * pulse * 0.35;

    // Audio-reactive ripple (v0.2): uWobbleFreqMul already carries Guitar 2
    // treble energy (baseline 1.0 under silence, rises with treble/attack
    // transients) - reused directly rather than adding a new uniform, so a
    // pick transient visibly flashes a soft ripple at the blob edges without
    // any new audio-detection code.
    float treblePulse = smoothstep(1.0, 2.2, uWobbleFreqMul);
    color += frontColor * treblePulse * 0.25 * (1.0 - frontShape);

    fragColor = vec4(clamp(color, 0.0, 1.0), 1.0);
}
