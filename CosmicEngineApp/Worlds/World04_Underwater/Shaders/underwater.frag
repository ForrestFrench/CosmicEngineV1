#version 330 core
in vec2 vUV;
out vec4 fragColor;

// UNDERWATER / JELLYFISH / CAUSTIC LIGHT - World 04 (Phase 1: atmosphere
// prototype only, v0.1)
//
// Shader-only, fullscreen-quad, analytic 2D scene - same architecture family
// as Lava Lamp / Wind Turbine Fire: no raymarch, no textures, no mesh
// pipeline. A dark, cool, three-zone water column - dim green-teal light
// entry near the top, desaturated slate-blue midwater, near-black
// blue-violet abyss at the bottom - lit by slow-swaying analytic god rays,
// upper-water caustic shimmer, drifting haze/murk, and marine-snow
// particulate. Phase 1 is deliberately atmosphere-only: no jellyfish, no
// tentacles, no silhouettes, no refraction warp - all explicitly deferred to
// a later phase. A sparse, hard-gated bioluminescent-mote shimmer at high
// bloom is the only forward-looking hint of what's coming, and draws no
// actual creature shapes.
//
// Guitar 1 (Creator) -> uLightDrive: ray/caustic/glow brightness and the
// uBloom accumulation drive (uLightDrive itself already carries the C#-side
// asymmetric attack/release envelope - see UnderwaterScene.cs - so it swells
// and lingers rather than twitching with the raw signal). Guitar 2
// (Sculptor) -> uCurrentDrive: current speed / particle drift;
// uCurrentTurbulence adds transient turbulence, mirroring Wind Turbine
// Fire's smokeTurbulence shape.
//
// Color discipline: glow stays on a deep-blue -> cyan -> pale-green ramp
// only. A violet nudge is allowed only via a small uBloom-gated mix weight
// on the bioluminescent motes (Phase 2 foreshadowing) - never a hue flip,
// nothing neon, nothing full-saturation.
//
// Two GLSL gotchas this codebase has been bitten by before (see
// wind_turbine_fire.frag): (a) after the uv.y flip + aspect correction, p.y
// is negated once so positive p.y consistently means "up" for every
// constant below; (b) noise helpers are named vnoise2 (not noise2) to avoid
// colliding with GLSL's built-in vec2-returning noise2() on some drivers.

uniform float uTime;
uniform float uBloom;              // 0 (calm/dark) .. 1 (fully bloomed), continuous
uniform float uLightDrive;         // Guitar 1 / Creator, already envelope-shaped, 0-1ish
uniform float uCurrentDrive;       // Guitar 2 / Sculptor, 0-1
uniform float uCurrentTurbulence;  // 0-1ish, baseline never 0 (set in C#)
uniform int   uParticleCount;      // P1-aware: Safe uses fewer, High more

const int MAX_PARTICLES = 64;
const int RAY_COUNT = 4;

// --- cheap analytic noise (no textures) ------------------------------------

float hash1(float n) { return fract(sin(n) * 43758.5453123); }

float hash2(vec2 p) { return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453123); }

// Named vnoise2 (not noise2) - GLSL has a built-in noise2(vec2) returning
// vec2, and a same-named float-returning function collides with it on some
// drivers.
float vnoise2(vec2 p) {
    vec2 i = floor(p);
    vec2 f = fract(p);
    float a = hash2(i);
    float b = hash2(i + vec2(1.0, 0.0));
    float c = hash2(i + vec2(0.0, 1.0));
    float d = hash2(i + vec2(1.0, 1.0));
    vec2  u = f * f * (3.0 - 2.0 * f);
    return mix(a, b, u.x) + (c - a) * u.y * (1.0 - u.x) + (d - b) * u.x * u.y;
}

// 3-octave FBM, deliberately off-power-of-two lacunarity (2.02) to avoid
// repeating tiling - matches wind_turbine_fire.frag's fbm2 exactly.
float fbm2(vec2 p, int octaves) {
    float sum  = 0.0;
    float amp  = 0.5;
    float freq = 1.0;
    for (int i = 0; i < 3; i++) {
        if (i >= octaves) break;
        sum  += amp * vnoise2(p * freq);
        freq *= 2.02;
        amp  *= 0.5;
    }
    return sum;
}

// --- God rays ----------------------------------------------------------

// Ray origin sits above the top of frame (p.y ~0.5 is the visible top), one
// per ray, evenly fanned out.
vec2 rayOrigin(int i) {
    float fi = float(i);
    float baseX = mix(-0.55, 0.55, (fi + 0.5) / float(RAY_COUNT));
    // Design correction (Phase 1, iteration 2): the origin was originally
    // 0.78 - well above the visible top of frame (p.y ~0.5) - which meant
    // the exponential depth-attenuation below had already eaten most of
    // each ray's brightness before it even entered frame, reading as
    // "barely there" at rest. Moved to 0.60 (just above the visible top)
    // so rays are close to full brightness right at the top edge and still
    // die out by mid-to-lower frame, matching the intended "visibly die
    // before the bottom third" falloff instead of "already faint at the top".
    return vec2(baseX, 0.60);
}

// Slow, asymmetric sway - two incommensurate sine frequencies/phases per ray
// (not a single symmetric oscillation) so the sway never reads as
// mechanical.
float rayAngle(int i) {
    float fi = float(i);
    float phase = hash1(fi * 7.77 + 3.0) * 6.2831;
    float sway = 0.10 * sin(uTime * 0.11 + phase)
               + 0.045 * sin(uTime * 0.077 - phase * 1.3);
    float baseAngle = mix(-0.20, 0.20, (fi + 0.5) / float(RAY_COUNT));
    return baseAngle + sway;
}

// Cheap band+attenuation envelope only (no per-ray FBM) - reused both as the
// base of the full ray render below and, critically, as the sampled
// intensity that marine-snow particles multiply their own brightness by.
// Deliberately cheaper than rayField() (no fbm2 call) since it is evaluated
// once per particle per frame (up to MAX_PARTICLES times) rather than once
// per pixel - the geometric footprint/sway alone is enough to make
// particles glint convincingly as they cross a light shaft.
float rayEnvelope(vec2 pos) {
    float total = 0.0;
    for (int i = 0; i < RAY_COUNT; i++) {
        vec2  origin = rayOrigin(i);
        float angle  = rayAngle(i);
        vec2  dir    = normalize(vec2(sin(angle), -1.0));
        vec2  d      = pos - origin;
        float along  = max(dot(d, dir), 0.0);
        vec2  perpD  = vec2(-dir.y, dir.x);
        float perp   = dot(d, perpD);
        float width  = 0.060 + 0.06 * along;
        float band   = exp(-(perp * perp) / (width * width));
        // Exponential attenuation with depth - dies well before the bottom
        // third of frame. Origin sits at p.y=0.60 (just above the visible
        // top edge at p.y=0.5), so a ray is still close to full brightness
        // at the top of frame and reaches near-zero by around p.y ~ -0.1 to
        // -0.2 (comfortably before the bottom third, which starts near
        // p.y ~ -0.17).
        float atten  = exp(-along * 3.0);
        total += band * atten;
    }
    return total;
}

// Full on-screen god-ray visual - per-ray FBM intensity modulation scrolling
// along the ray's own length, on top of the same band+attenuation shape as
// rayEnvelope() above.
float rayField(vec2 pos) {
    float total = 0.0;
    for (int i = 0; i < RAY_COUNT; i++) {
        vec2  origin = rayOrigin(i);
        float angle  = rayAngle(i);
        vec2  dir    = normalize(vec2(sin(angle), -1.0));
        vec2  d      = pos - origin;
        float along  = max(dot(d, dir), 0.0);
        vec2  perpD  = vec2(-dir.y, dir.x);
        float perp   = dot(d, perpD);
        float width  = 0.060 + 0.06 * along;
        float band   = exp(-(perp * perp) / (width * width));
        float atten  = exp(-along * 3.0);

        float rn = fbm2(vec2(perp * 10.0, along * 2.2 - uTime * 0.18 + float(i) * 17.3), 2);
        total += band * atten * (0.55 + 0.55 * rn);
    }
    return total;
}

void main() {
    vec2 uv = vUV;
    uv.y = 1.0 - uv.y;

    float aspect = 1.7778;
    vec2  p = (uv - 0.5);
    p.x *= aspect;
    // Positive p.y = up for every constant below (matches wind_turbine_fire.frag's convention).
    p.y = -p.y;

    // depthT: 0 at the very top of frame (light entry), 1 at the very
    // bottom (abyss). Perturbed with low-frequency horizontal noise so the
    // three-zone gradient is never a flat ramp.
    float depthT = clamp(0.5 - p.y, 0.0, 1.0);
    float depthNoise = (fbm2(vec2(p.x * 1.1 + 3.0, uTime * 0.015), 2) - 0.5) * 0.12;
    float depthTN = clamp(depthT + depthNoise, 0.0, 1.0);

    // --- Three-zone vertical water gradient ------------------------------
    vec3 zoneTop    = vec3(0.045, 0.130, 0.120); // dim green-teal, light entry
    vec3 zoneMid    = vec3(0.032, 0.052, 0.098); // desaturated slate-blue
    vec3 zoneBottom = vec3(0.006, 0.009, 0.022); // near-black blue-violet abyss

    vec3 color = mix(zoneTop, zoneMid, smoothstep(0.0, 0.45, depthTN));
    color = mix(color, zoneBottom, smoothstep(0.45, 1.0, depthTN));

    // --- God rays ----------------------------------------------------------
    // Brightness = time-only baseline (alive under silence) + uLightDrive
    // lift + uBloom lift, so rays never disappear entirely at rest.
    float rf = rayField(p);
    float rayBrightnessMul = 0.55 + 0.65 * uLightDrive + 0.45 * uBloom;
    vec3  rayColorDeep  = vec3(0.16, 0.48, 0.50);
    vec3  rayColorBloom = vec3(0.48, 0.90, 0.62);
    vec3  rayColor = mix(rayColorDeep, rayColorBloom, clamp(uBloom, 0.0, 1.0) * 0.55);
    color += rayColor * rf * rayBrightnessMul;

    // --- Caustic shimmer ----------------------------------------------------
    // Two independently scrolling ridged-noise layers, multiplied together,
    // masked to the upper third of frame AND modulated by the local
    // (cheap-envelope) ray intensity so it never reads as a full-frame
    // static-looking overlay.
    float causticTopMask = 1.0 - smoothstep(0.0, 0.34, depthTN);
    vec2  cUV1 = p * 6.0 + vec2(uTime * 0.06, -uTime * 0.03);
    vec2  cUV2 = p * 7.4 + vec2(-uTime * 0.045, uTime * 0.07);
    float cn1 = vnoise2(cUV1);
    float cn2 = vnoise2(cUV2);
    float ridge1 = pow(1.0 - abs(2.0 * cn1 - 1.0), 2.2);
    float ridge2 = pow(1.0 - abs(2.0 * cn2 - 1.0), 2.2);
    float caustic = ridge1 * ridge2;
    float causticLocalRay = clamp(rayEnvelope(p) * 1.3, 0.0, 1.6);
    float causticMask = causticTopMask * causticLocalRay;
    vec3  causticColor = vec3(0.30, 0.68, 0.56);
    color += causticColor * caustic * causticMask * (0.45 + 0.55 * uLightDrive + 0.40 * uBloom) * 0.85;

    // --- Haze/murk: FBM layers with domain-warped lateral current drift ----
    // Baseline drift never zero even at rest; denser/darker toward the
    // bottom.
    vec2  hazeWarp = vec2(fbm2(p * 0.6 + vec2(0.0, uTime * 0.015), 2) * 0.35, 0.0);
    float driftBase = 0.015 + 0.09 * uCurrentDrive;
    vec2  hazeUV1 = (p + hazeWarp) * 1.4 + vec2(uTime * driftBase, uTime * 0.006);
    vec2  hazeUV2 = (p + hazeWarp) * 2.3 + vec2(-uTime * driftBase * 0.7, -uTime * 0.009);
    float haze1 = fbm2(hazeUV1, 3);
    float haze2 = fbm2(hazeUV2, 2);
    float hazeCombined = clamp(haze1 * 0.6 + haze2 * 0.4, 0.0, 1.0);
    float hazeDensity = mix(0.10, 0.42, depthTN);
    vec3  hazeTint = vec3(0.010, 0.018, 0.032);
    color = mix(color, hazeTint, hazeCombined * hazeDensity * 0.55);

    // --- Marine-snow particles -----------------------------------------------
    // Fixed loop capped by uParticleCount, applying the Entry-33 ember
    // lessons directly: squared-hash size skew toward small, pow(hash,2.8)
    // brightness skew so most particles are dim and only a few are bright,
    // slow sink + current drift + 3 incommensurate-sine wander (never
    // straight-line motion), two implicit depth tiers via a single
    // correlated hash driving size/speed/brightness together. Critical
    // mechanic: each particle's brightness is multiplied by the god-ray
    // intensity sampled at its own position (rayEnvelope above), so
    // particles visibly glint as they drift through light shafts and nearly
    // vanish in the dark water between them - this is what makes them read
    // as underwater particulate rather than lens dust.
    for (int i = 0; i < MAX_PARTICLES; i++) {
        if (i >= uParticleCount) break;

        float fi   = float(i);
        float seed = fi * 19.61 + 7.0;

        // Single correlated hash drives size/speed/brightness together,
        // producing two implicit depth tiers (near = bigger/faster/
        // brighter, far = smaller/slower/dimmer) without a hard branch.
        float tier = hash1(seed + 9.0);

        float sizeHash = hash1(seed + 1.0);
        sizeHash *= sizeHash; // squared skew toward small

        float lifeSpeed = mix(0.015, 0.045, tier) * (0.9 + 0.2 * hash1(seed + 2.0));
        float t   = uTime * lifeSpeed + hash1(seed + 3.0) * 20.0;
        float cyc = fract(t);

        float topY = 0.58, bottomY = -0.58;
        float y = mix(topY, bottomY, cyc); // continuous slow sink, wraps at cycle end

        float baseX  = mix(-0.9, 0.9, hash1(seed + 4.0));
        float driftX = (0.05 + 0.35 * uCurrentDrive) * cyc; // baseline drift never zero

        // 3 incommensurate sine perturbations - never a straight line.
        float turb = 0.03  * sin(t * 2.7 + seed)
                   + 0.018 * sin(t * 6.1 - seed * 1.3)
                   + 0.010 * sin(t * 1.3 + seed * 2.9);

        vec2 particlePos = vec2(baseX + driftX + turb * (0.5 + uCurrentTurbulence), y);
        float dist = length(p - particlePos);

        float bMax = pow(hash1(seed + 5.0), 2.8);
        float brightness = bMax * mix(0.6, 1.0, tier);

        float glint = rayEnvelope(particlePos);
        brightness *= (0.16 + 1.1 * clamp(glint, 0.0, 1.5));

        vec3  particleColor = mix(vec3(0.55, 0.75, 0.75), vec3(0.75, 0.95, 0.85), tier);
        // Design correction (Phase 1, iteration 2): the original size range
        // (0.0007-0.0024 p-units, i.e. well under 2px at 1280x720) rendered
        // as effectively invisible sub-pixel dots against a static
        // screenshot. Roughly tripled so marine snow reads as small but
        // clearly visible specks (still small - Entry-33's "most are dim,
        // only a few bright" skew is unchanged above).
        float sizePx = mix(0.0022, 0.0068, sizeHash) * mix(0.7, 1.25, tier);

        color += particleColor * brightness * smoothstep(sizePx, 0.0, dist) * 2.0;
    }

    // --- Bioluminescent motes (Phase 2 foreshadowing only, no creature shapes) ---
    // Ember-style hard gating: zero baseline, absent entirely at rest, only
    // appears as bloom rises above ~0.6.
    float moteGate = smoothstep(0.60, 0.85, uBloom);
    if (moteGate > 0.001) {
        const int MOTE_COUNT = 10;
        for (int i = 0; i < MOTE_COUNT; i++) {
            float fi   = float(i);
            float seed = fi * 29.3 + 2.0;

            float t   = uTime * 0.07 + hash1(seed) * 10.0;
            float mx  = mix(-0.85, 0.85, hash1(seed + 1.0)) + 0.05 * sin(t * 2.0 + seed);
            float my  = mix(0.40, -0.40, hash1(seed + 2.0)) + 0.05 * sin(uTime * 0.3 + seed);
            vec2  motePos = vec2(mx, my);
            float dist = length(p - motePos);

            float pulse = 0.5 + 0.5 * sin(uTime * 1.3 + seed * 3.0);
            float sizeM = 0.0016;

            vec3 moteColor = mix(vec3(0.35, 0.85, 0.75), vec3(0.55, 0.78, 0.95), hash1(seed + 3.0));
            // Violet nudge only at very high bloom - a nudge, never a hue flip.
            moteColor = mix(moteColor, vec3(0.55, 0.35, 0.75), smoothstep(0.8, 1.0, uBloom) * 0.15);

            float brightnessM = moteGate * pulse * smoothstep(sizeM, 0.0, dist);
            color += moteColor * brightnessM * 0.8;
        }
    }

    // --- Final grade / vignette / readability guard -------------------------
    float vig = smoothstep(1.0, 0.30, length(p));
    color *= mix(0.75, 1.0, vig);

    // Readability guard: exposure only nudges up modestly with bloom, so
    // even a fully-bloomed frame stays dark-dominant rather than washing
    // out toward a flat blue wash.
    float exposure = 0.92 + 0.08 * uBloom;
    color *= exposure;

    fragColor = vec4(clamp(color, 0.0, 1.0), 1.0);
}
