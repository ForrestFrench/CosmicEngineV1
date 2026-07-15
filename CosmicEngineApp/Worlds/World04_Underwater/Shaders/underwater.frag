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
// particulate. Phase 1 was atmosphere-only (no jellyfish/tentacles). Phase 2
// (this pass, v0.1) adds 3 mid-distance jellyfish forms - see the "Jellyfish
// (Phase 2)" section below for the full design - while leaving every Phase 1
// layer (gradient/rays/caustics/haze/particles/motes/grade) untouched except
// where jellyfish physically interact with it (haze occlusion sampling).
//
// Guitar 1 (Creator) -> uLightDrive: ray/caustic/glow brightness and the
// uBloom accumulation drive (uLightDrive itself already carries the C#-side
// asymmetric attack/release envelope - see UnderwaterScene.cs - so it swells
// and lingers rather than twitching with the raw signal). Guitar 2
// (Sculptor) -> uCurrentDrive: current speed / particle drift;
// uCurrentTurbulence adds transient turbulence, mirroring Wind Turbine
// Fire's smokeTurbulence shape. Phase 2 extends this same split to the
// jellyfish: Input B sets pulse *rate* ("B makes it move"), Input A sets
// pulse/rim *brightness* ("A makes it glow") - both reuse uLightDrive/
// uCurrentDrive/uCurrentTurbulence already computed for Phase 1, plus 3 new
// uJellyPhase0/1/2 uniforms (C#-integrated pulse phase - see
// UnderwaterScene.cs for why that needs real integration, not uTime*rate).
//
// Color discipline: glow stays on a deep-blue -> cyan -> pale-green ramp
// only. A violet nudge is allowed only via a small uBloom-gated mix weight
// on the bioluminescent motes and (as of Phase 2) the jellyfish rim glow -
// never a hue flip, nothing neon, nothing full-saturation.
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

// Phase 2: per-jellyfish pulse phase, 0-1 continuously wrapping, integrated
// in C# (UnderwaterScene.cs) rather than here - see that file's comments.
// Separate named uniforms (not an array) matches this codebase's existing
// per-instance-uniform convention for small fixed counts (see WindTurbine-
// FireScene's uRotorAngleFG1/FG2/BG1/BG2).
uniform float uJellyPhase0;
uniform float uJellyPhase1;
uniform float uJellyPhase2;

const int MAX_PARTICLES = 64;
const int RAY_COUNT = 4;

// Jellyfish Tentacle Rescue Pass 1 (round 6, AUDIT.md Entry 41 - see
// renderJelly()'s tentacle section for the full rationale): round 5's
// single-quadratic-Bezier-per-tentacle technique was rejected because a
// curve with exactly one control point between root and tip can only ever
// swing as one rigid bow - it cannot bend in multiple places independently,
// so it still read as "spikes pivoting on a hinge" even though it was
// technically a smooth curve. This round replaces that with a genuine
// traveling-wave polyline per tentacle (TENT_SAMPLES points sampled along
// each strand's length, see below) - multiple points at different phases
// of an oscillation at any moment, so the shape itself visibly undulates
// and that undulation travels down the strand over time.
const int MAX_TENTACLES = 18;
// 8-20 range per the brief; 16 chosen after a first pass at 10 samples with
// a wider wave-frequency range visibly under-sampled the curve into a
// faceted zigzag at normal viewing scale (caught on screenshot review) -
// see the perf note in renderJelly()'s tentacle section for the measured
// cost/quality tradeoff.
const int TENT_SAMPLES = 16;

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

// --- Jellyfish (Phase 2) -------------------------------------------------
//
// Design goals, directly applying this project's own prior mistakes
// (AUDIT.md Entries 33/39) to a new scene:
//   - Bell is a real SDF construction (dome + flared skirt, smooth-unioned),
//     not a circle, with a domain-warped rim so the silhouette edge is never
//     a perfectly smooth geometric curve.
//   - The pulse deforms the SDF itself (separate dome/skirt centers and
//     radii both move with an asymmetric contract-fast/relax-slow envelope)
//     - never a uniform scale-up/scale-down of a static shape.
//   - Phase 2 round 6 (Jellyfish Tentacle Rescue Pass 1, AUDIT.md Entry 41
//     addendum 5): round 5's single-quadratic-Bezier-per-tentacle technique
//     eliminated segment joints but was still rejected by the user as
//     "straight spikes" / "laser rays" that "pivot around their attachment
//     points" and "do not bend, trail, curl, or flow." Root cause: a
//     quadratic Bezier has exactly one control point between root and tip,
//     so it can only ever form a single smooth bow - swinging that one
//     control point via a sine term rotates the *entire strand* as one
//     rigid shape hinged at the root, which is mathematically indistinguish-
//     able from a rigid rod pivoting on a hinge no matter how smooth the
//     curve itself is. The fix is not a fancier curve - it's genuine
//     traveling-wave motion: multiple points along each tentacle's length
//     at different phases of an oscillation at any given moment, so the
//     shape itself visibly undulates (multiple bends, changing over time
//     and traveling down the strand) instead of the whole strand swinging
//     in place. See renderJelly()'s tentacle section for the full
//     implementation. This remains the same deliberate, reasoned exception
//     to Entry 33's "no loop" lesson every prior round established (every
//     strand's attach point, bend, length, taper, and phase is its own
//     independent hash, never index-derived spacing) - only the per-strand
//     motion/shape technique changed again.
//   - Bell interior is deliberately translucent (mixes back toward the
//     water color/haze already computed at this pixel) while the rim stays
//     brighter/more defined - a controlled, intentional version of what was
//     an accidental transparency bug on Wind Turbine Fire's background
//     turbines (Entry 31).
//   - No ornamental highlight/rim-light added beyond the rim glow that is
//     the core "bioluminescent organism" concept itself (Entry 39's nacelle-
//     highlight lesson: a static decorative detail was rejected as looking
//     cheap - the rim glow here is functional/audio-driven, not decorative).

// IQ-style approximate ellipse SDF - adequate for a soft mask + smoothstep
// edge, not exact Euclidean distance, consistent with how the rest of this
// file already builds masks from analytic approximations rather than true
// raymarched distance fields.
float sdEllipseApprox(vec2 p, vec2 ab) {
    float k = length(p / max(ab, vec2(0.0001)));
    return (k - 1.0) * min(ab.x, ab.y);
}

// Standard polynomial smooth-min (Quilez) - blends two SDFs into one
// continuous shape instead of a hard min() union.
float smin(float a, float b, float k) {
    float h = clamp(0.5 + 0.5 * (b - a) / max(k, 0.0001), 0.0, 1.0);
    return mix(b, a, h) - k * h * (1.0 - h);
}

// Capsule (rounded line-segment) SDF with independent radii at each end -
// gives a real geometric taper along the segment.
float sdCapsule(vec2 p, vec2 a, vec2 b, float ra, float rb) {
    vec2  pa = p - a;
    vec2  ba = b - a;
    float h  = clamp(dot(pa, ba) / max(dot(ba, ba), 0.0001), 0.0, 1.0);
    float r  = mix(ra, rb, h);
    return length(pa - ba * h) - r;
}

// Same as sdCapsule, but also returns the parametric h (0 at a, 1 at b) of
// the closest point, so a caller walking a polyline of these segments can
// track exactly how far along the *whole* strand (not just this one
// segment) the nearest surface point sits, for taper/fade purposes -
// analogous to the closest-point-t previously returned by round 5's
// sdBezierT (removed this pass - see renderJelly()'s tentacle section for
// why a single Bezier curve was replaced by a sampled traveling-wave
// polyline built from a chain of these).
vec2 sdCapsuleT(vec2 p, vec2 a, vec2 b, float ra, float rb) {
    vec2  pa = p - a;
    vec2  ba = b - a;
    float h  = clamp(dot(pa, ba) / max(dot(ba, ba), 0.0001), 0.0, 1.0);
    float r  = mix(ra, rb, h);
    return vec2(length(pa - ba * h) - r, h);
}

// Asymmetric pulse envelope from a continuously-wrapping 0-1 phase (C#-
// integrated - see UnderwaterScene.cs). Fast attack (contract), slow release
// (relax) - a real jellyfish contraction shape, not a symmetric oscillation
// and not a simple scale multiplier.
float jellyPulse(float phase) {
    const float contractFrac = 0.28;
    if (phase < contractFrac) {
        float t = phase / contractFrac;
        return pow(t, 0.55);
    } else {
        float t = (phase - contractFrac) / (1.0 - contractFrac);
        return pow(clamp(1.0 - t, 0.0, 1.0), 1.6);
    }
}

// Renders one jellyfish directly into `color` (additive/mix in place).
// `idx` picks this jellyfish's hash-derived placement/size (no C# state
// needed for that - only phase requires true time integration, see above).
// `hazeCombined`/`hazeDensity` are the Phase 1 haze values already computed
// at this pixel in main() - read (not recomputed) so a jellyfish behind
// heavier haze visibly recedes, per the plan's "reads as embedded in the
// water column" requirement.
void renderJelly(inout vec3 color, vec2 p, float idx, float phase,
                  float hazeCombined, float hazeDensity) {
    float seed = idx * 41.7 + 5.0;

    float depthScale   = mix(0.78, 1.16, hash1(seed + 1.0));
    float baseX        = mix(-0.60, 0.60, hash1(seed + 2.0));
    float baseY        = mix(-0.05, 0.24, hash1(seed + 3.0));
    float wobblePhase  = hash1(seed + 4.0) * 6.2831;

    vec2 jellySize = vec2(0.116, 0.080) * depthScale;

    // Current-driven wander (bounded, not a one-shot traversal that would
    // need wraparound) - amplitude/speed both track uCurrentDrive/
    // uCurrentTurbulence, satisfying "drift laterally with the current".
    vec2 jellyPos = vec2(
        baseX + (0.05 + 0.10 * uCurrentDrive) * sin(uTime * (0.05 + 0.03 * uCurrentTurbulence) + wobblePhase)
              + uCurrentDrive * 0.05 * sin(uTime * 0.021 + wobblePhase * 3.1),
        baseY + 0.02 * sin(uTime * 0.09 + wobblePhase * 1.7)
    );

    float contraction = jellyPulse(phase);
    vec2  local = p - jellyPos;

    // --- Bell SDF: dome (crown) + flared skirt, smooth-unioned -------------
    // Dome and skirt each respond independently to contraction (different
    // center offsets AND different radii), so the pulse genuinely reshapes
    // the silhouette (narrow+tall crown, tucked-up skirt when contracted;
    // wide+flat when relaxed) rather than scaling one static shape.
    vec2 domeCenter  = vec2(0.0, jellySize.y * mix(0.28, 0.42, contraction));
    vec2 domeRadii   = jellySize * vec2(mix(1.06, 0.90, contraction), mix(0.62, 0.78, contraction));
    vec2 skirtCenter = vec2(0.0, jellySize.y * mix(-0.55, -0.25, contraction));
    vec2 skirtRadii  = jellySize * vec2(mix(1.18, 0.96, contraction), mix(0.55, 0.40, contraction));

    // Domain-warped rim - continuous FBM perturbation of the skirt edge, not
    // a discrete scalloped shape.
    float rimParam = local.x / max(skirtRadii.x, 0.001);
    float rimWarp  = fbm2(vec2(rimParam * 3.1 + seed, uTime * 0.10 + seed), 2) - 0.5;
    vec2  skirtLocal = local - skirtCenter;
    skirtLocal.y += rimWarp * jellySize.y * 0.22;

    float dDome  = sdEllipseApprox(local - domeCenter, domeRadii);
    float dSkirt = sdEllipseApprox(skirtLocal, skirtRadii);
    float bellD  = smin(dDome, dSkirt, jellySize.y * 0.35);

    float edgeAA   = jellySize.y * 0.10;
    float bellMask = 1.0 - smoothstep(-edgeAA, edgeAA, bellD);

    // Depth/haze occlusion - reuses the Phase 1 haze already computed at
    // this pixel (no new haze layer) plus a per-jellyfish distance term, so
    // farther jellyfish read as receding into the water column.
    float distFactor = clamp((1.16 - depthScale) / 0.38, 0.0, 1.0);
    float occlusion  = (1.0 - hazeCombined * hazeDensity * 0.5) * mix(1.0, 0.55, distFactor);

    // Controlled translucency: bell interior mixes 35-50% back toward
    // whatever is already behind it (water/rays/caustics/haze), a deliberate
    // version of Entry 31's accidental turbine-transparency bug. Design
    // correction (Phase 2, iteration 1): the first pass used a flat interior
    // fill close in brightness/hue to the ambient water, which combined with
    // the alpha bleed made the body read as functionally invisible - the
    // bell showed only as a bright rim outline ("ring with strings", the
    // exact failure mode this pass is meant to avoid). Fixed with (a) a
    // visibly brighter/more saturated base body color and (b) a soft radial
    // core glow (brighter toward the bell's own center, fading to the rim) -
    // functional, not ornamental: it reads as the animal's own internal
    // bioluminescence diffusing through translucent tissue, and is itself
    // driven by uLightDrive/uBloom like everything else in this scene.
    if (bellMask > 0.001) {
        vec2  coreOff  = (local - domeCenter) / max(domeRadii, vec2(0.001));
        float coreGlow = smoothstep(1.15, 0.0, length(coreOff));
        vec3  bodyBase = vec3(0.20, 0.42, 0.40);
        vec3  bodyCore = vec3(0.34, 0.62, 0.56);
        vec3  jellyBody = mix(bodyBase, bodyCore, coreGlow) * (0.55 + 0.35 * uLightDrive + 0.30 * uBloom);
        float bgBleed   = mix(0.35, 0.50, hash1(seed + 5.0));
        vec3  mixedInterior = mix(jellyBody, color, bgBleed);
        color = mix(color, mixedInterior, bellMask * occlusion);
    }

    // Rim-weighted bioluminescence - concentrated exactly at the bell
    // margin (|bellD| small), brightness on Input A/Creator, matching this
    // project's "A = light" convention. Same violet-nudge rule Phase 1
    // already established for the motes (a small uBloom-gated mix, never a
    // hue flip) is reused here rather than introduced as something new.
    float rimBand   = exp(-abs(bellD) / (jellySize.y * 0.09));
    vec3  rimColor  = mix(vec3(0.42, 0.86, 0.68), vec3(0.55, 0.80, 0.95), hash1(seed + 6.0));
    rimColor = mix(rimColor, vec3(0.55, 0.35, 0.75), smoothstep(0.80, 1.0, uBloom) * 0.15);
    float rimBrightness = (0.35 + 0.70 * uLightDrive + 0.55 * uBloom) * rimBand * occlusion;
    color += rimColor * rimBrightness * 0.75;

    // --- Tentacles (Phase 2 round 6 - traveling-wave polyline rewrite) -----
    // Design-correction history: round 5's single-quadratic-Bezier-per-
    // tentacle technique killed the segment-joint artifact but was rejected
    // by the user as "straight spikes"/"laser rays" that "pivot around
    // their attachment points" and "do not bend, trail, curl, or flow." The
    // diagnosis: a quadratic Bezier has exactly one control point between
    // root and tip, so swinging that control point via a sine term rotates
    // the *whole strand* as one rigid bow hinged at the root - a smooth
    // curve shape, but rigid-body motion underneath, indistinguishable from
    // a rod pivoting on a hinge.
    //
    // Fix: each tentacle is now sampled at TENT_SAMPLES points along its
    // length (t = 0 at the bell attach point, t = 1 at the tip) and the
    // points are connected into a polyline (a chain of short capsule
    // segments, distance evaluated directly against that polyline - no
    // smin blend needed because consecutive segments share exact endpoints
    // and, at this sample density, subtend only a few degrees each, so
    // there is no reflex-angle facet to round off). Each sample's lateral
    // offset is a genuine traveling wave:
    //     wave(t) = amplitude(t) * sin(t * waveFreq - uTime * waveSpeed + tentPhase)
    // The `t * waveFreq` term is what puts multiple bends along the length
    // at once (not just one); the `- uTime * waveSpeed` term is what makes
    // those bends travel down the strand over time instead of the whole
    // shape oscillating in place. amplitude(t) = maxAmp * pow(t, ampPow)
    // is ~0 at the root (t=0, stays anchored to the bell) and grows toward
    // the tip (t=1, trails/curls freely) - same growth shape applied to a
    // static per-tentacle bend (root fixed, curve grows toward the tip) and
    // to current drift (also stronger at the tip than the root), so "root
    // anchored, middle bends and lags, bottom trails/curls" holds for every
    // motion source at once, not just one of them.
    //
    // Every strand still draws its attach position, static bend, length,
    // taper, brightness, and now its wave frequency/speed/phase/curl from
    // its own independent hash (hash1(tentacleIndex * 17.3 + seed * 7.1 +
    // N), fresh non-colliding N offsets extending the existing 2/3/5/7/11/
    // 13/17/19/23/29 sequence established by prior rounds) - the same
    // deliberate, reasoned exception to Entry 33's "no loop over discrete
    // elements" lesson every round has preserved (nothing here is index-
    // derived beyond the hash input itself). A soft centerBias also biases
    // tentacles near the bell's own center to run shorter/thicker
    // (oral-arm-like) and ones near the rim edge to run longer/thinner
    // (true marginal tentacles), per the brief's grouping requirement -
    // a gentle correlation layered on top of the hash, not a deterministic
    // pattern.
    //
    // Attach points are solved analytically on the skirt's own already-
    // curved, already rim-warped boundary (same sdEllipseApprox + fbm2 warp
    // math the bell SDF above uses, evaluated at each tentacle's own
    // independently-hashed x, unchanged from every prior round) - there is
    // no rectangular/horizontal-band clip mask anywhere in this section, so
    // there is no mechanism that could produce a hard straight seam at the
    // bell/tentacle boundary.
    //
    // Perf note: TENT_SAMPLES=16 and MAX_TENTACLES/tentacleCount capped at
    // 24 (down from round 5's 18-30) is the tradeoff made to keep this
    // per-sample-point evaluation (~15x the per-tentacle cost of round 5's
    // single closed-form Bezier call) inside a similar fps budget - see the
    // rescue-pass REPORT.md for the measured before/after fps.
    int tentacleCount = 10 + int(floor(hash1(seed + 8.0) * 8.999)); // 10-18 - reduced from an initial 14-24 after High-profile fps measurement (see REPORT.md) showed the 16-sample traveling-wave evaluation was measurably more expensive per tentacle than round 5's single closed-form Bezier call; count was traded down to protect fps rather than sample count, to preserve the smooth-curve quality fix

    // Purely computational bounding box (NOT a visual mask - the actual
    // silhouette comes only from each tentacle's own polyline falloff
    // below) so the per-tentacle loop (and the per-tentacle hash-parameter
    // setup that precedes the reach check above) is skipped far from any
    // jellyfish. Perf (Entry 41 addendum 6): re-derived from the same
    // worst-case-reach analysis as the per-tentacle early-out above
    // (tentLen_max ~= jellySize.y*4.6; vertical reach from the attach point
    // ~= 1.2*tentLen_max plus the skirt's own radius; horizontal reach
    // ~= 1.5*tentLen_max) rather than the round-6 rescue pass's original
    // flat 8.0/6.0 padding, which was more generous than the geometry
    // actually needs. Still carries real headroom above the analytic
    // minimum (padding chosen, not the bare minimum) - tightening this is a
    // secondary win on top of the per-tentacle early-out above, since most
    // of the removed area's per-tentacle checks would already have been
    // culled by that early-out; this box mainly saves the cheap per-
    // tentacle hash-parameter setup for fragments clearly outside every
    // tentacle's possible reach.
    float tentBoundTop    = skirtCenter.y + jellySize.y * 0.6;
    float tentBoundBottom = skirtCenter.y - skirtRadii.y - jellySize.y * 6.5;
    float tentBoundHalfW  = skirtRadii.x + jellySize.x * 5.0;

    if (local.y < tentBoundTop && local.y > tentBoundBottom && abs(local.x) < tentBoundHalfW) {
        for (int ti = 0; ti < MAX_TENTACLES; ti++) {
            if (ti >= tentacleCount) break;

            float fi    = float(ti);
            float tHash = fi * 17.3 + seed * 7.1;

            // Independent per-tentacle hashes - distinct N offsets, none
            // colliding with this function's seed+1..9 uses above or each
            // other (tHash is already a different numeric domain from
            // seed+N).
            float aXFrac    = clamp(mix(-0.90, 0.90, hash1(tHash + 2.0)), -0.94, 0.94);
            float bendBias  = mix(-0.55, 0.55, hash1(tHash + 3.0));
            float bendAmt   = mix(-0.35, 0.35, hash1(tHash + 5.0));
            float centerBias = 1.0 - abs(aXFrac); // 1 at bell center, 0 at rim edge

            float tentLen   = jellySize.y * mix(2.0, 4.6, hash1(tHash + 7.0))
                                           * mix(1.0, 0.68, centerBias * 0.55); // shorter near center (oral-arm-like)
            // Delicate marginal-tentacle thickness, tapering to a fine
            // point at the tip - centerBias thickens the shorter, more
            // central strands slightly (oral-arm-like) versus the longer
            // outer marginal ones.
            float thickBase = jellySize.x * mix(0.016, 0.030, hash1(tHash + 11.0))
                                           * mix(1.0, 1.5, centerBias * 0.45);
            float thickTip  = thickBase * mix(0.08, 0.22, hash1(tHash + 13.0));
            float brightVar = mix(0.85, 1.15, hash1(tHash + 29.0));

            // Traveling-wave parameters - fresh N offsets (31/37/41/43/47),
            // none colliding with the 2/3/5/7/11/13/17/19/23/29 sequence
            // established above/by prior rounds.
            float tentPhase  = hash1(tHash + 17.0) * 6.2831853; // reused slot, now the wave phase
            // waveFreq kept modest (4-9 rad, roughly 0.6-1.4 full cycles
            // across the strand) relative to TENT_SAMPLES so each cycle is
            // covered by enough samples to read as a smooth curve rather
            // than a zigzag/lightning-bolt polyline - a first pass at
            // 5-13 rad with 10 samples under-sampled the higher end of that
            // range and produced visible sharp kinks at normal viewing
            // scale, caught on screenshot review and corrected here.
            float waveFreq   = mix(4.0, 9.0, hash1(tHash + 31.0));   // bends distributed along the length
            float waveSpeed  = mix(0.6, 1.7, hash1(tHash + 37.0));   // how fast bends travel down the strand
            float ampPow     = mix(1.5, 2.2, hash1(tHash + 41.0));   // amplitude(t) growth curve, root->tip
            float maxAmpFrac = mix(0.08, 0.22, hash1(tHash + 43.0)); // curl amount, fraction of tentLen - reduced from 0.10-0.30 alongside the frequency retune, same reason
            float currentAmt = mix(0.15, 0.35, hash1(tHash + 47.0)); // per-tentacle current-drift weight

            // Attach point: a real point on the skirt's own curved, already-
            // jagged silhouette (solved from the identical sdEllipseApprox +
            // rim-warp math used for the bell SDF's own visible edge above),
            // at this tentacle's own independently-hashed x. Unchanged from
            // every prior round - this is what fixed the straight-boundary
            // problem and it is preserved verbatim.
            float aYNorm        = sqrt(max(1.0 - aXFrac * aXFrac, 0.0));
            float attachX       = skirtCenter.x + aXFrac * skirtRadii.x;
            float attachRimParam = attachX / max(skirtRadii.x, 0.001);
            float attachRimWarp  = fbm2(vec2(attachRimParam * 3.1 + seed, uTime * 0.10 + seed), 2) - 0.5;
            float attachY        = skirtCenter.y - skirtRadii.y * aYNorm - attachRimWarp * jellySize.y * 0.22;
            vec2  attachPt       = vec2(attachX, attachY);

            // Perf (Tentacle Rescue Pass 1 follow-up, AUDIT.md Entry 41
            // addendum 6): per-tentacle early-out. Isolating the tentacle
            // loop's cost (temporarily forcing it off) showed it accounts for
            // essentially the entire round-6 fps regression (High profile
            // 48.6fps measured -> 74.9fps with the loop disabled, i.e. ~7.3ms/
            // frame at 1280x720) - the TENT_SAMPLES=16 traveling-wave sample
            // loop plus its TENT_SAMPLES-1 sdCapsuleT/smin distance walk below
            // is the expensive part, run unconditionally for every one of
            // tentacleCount (10-18) tentacles at every fragment inside the
            // per-jellyfish bounding box, regardless of whether that specific
            // tentacle is anywhere near this fragment. This check skips that
            // expensive per-sample work per-tentacle using a conservative,
            // analytically-derived (not guessed) maximum reach: every motion
            // term below (bend, wave, current drift, pulse ripple) is
            // monotonic in st and therefore maximal at the tip (st=1), where
            // the combined worst-case lateral excursion is bounded by
            // tentLen * (|bendAmt|max 0.35 + maxAmpFrac max 0.22 + current
            // term max ~0.50 + ripple max 0.10) ~= 1.17*tentLen, combined with
            // the ~1.0*tentLen along-strand travel giving a Euclidean worst
            // case of roughly 1.5*tentLen from the attach point; 1.7x is used
            // here for headroom against approximation error. This changes
            // zero pixels of visible output - a culled tentacle would only
            // ever have contributed a fully-masked-out (zero tentMask) result
            // at this fragment anyway, since it is provably outside the
            // fragment-to-attach-point distance from the closed-form checked
            // here.
            float tentMaxReach = tentLen * 1.7 + thickBase * 3.0;
            vec2  toAttach      = local - attachPt;
            if (dot(toAttach, toAttach) > tentMaxReach * tentMaxReach) continue;

            float baseAngle = -1.5707963 + bendBias; // -90 deg = straight down
            vec2  dirDown   = vec2(cos(baseAngle), sin(baseAngle));
            vec2  perp      = vec2(-dirDown.y, dirDown.x);

            float maxAmplitude = tentLen * maxAmpFrac;

            // Sample TENT_SAMPLES points along the strand's length. t=0 is
            // pinned exactly to attachPt (no bend/wave/drift/ripple term is
            // non-zero there), so the root is anchored by construction, not
            // by tuning. Every other term grows with t (via pow(t,ampPow)
            // for the wave, t itself for the static bend/drift/ripple), so
            // amplitude increases toward the tip for every motion source.
            //
            // Perf note (Entry 41 addendum 6): a single-pass rewrite that
            // dropped this array in favor of carrying only the previous
            // sample point forward was tried and measured - it was a real
            // fps *regression* (High profile: 68.7fps two-pass -> 63.1fps
            // merged, 5-run average both ways), not an improvement, most
            // likely because it broke the shader compiler's ability to
            // optimize/schedule the sample-generation and polyline-walk
            // work as two separable passes. Reverted; the two-pass
            // structure below is the measured-faster version, kept as is.
            vec2 pts[TENT_SAMPLES];
            for (int si = 0; si < TENT_SAMPLES; si++) {
                float st = float(si) / float(TENT_SAMPLES - 1);

                // Static resting curve - root fixed, bend grows toward the
                // tip (pow(st,1.3), not linear, so the bend visibly lags
                // near the root and compounds toward the tip).
                vec2 basePos = attachPt + dirDown * (tentLen * st)
                                        + perp * (bendAmt * tentLen * pow(st, 1.3));

                // Genuine traveling wave: multiple bends distributed along
                // the length (the `st * waveFreq` term) that travel down
                // the strand over time (the `- uTime * waveSpeed` term) -
                // this is what makes the shape itself undulate instead of
                // the whole strand swinging as one rigid unit.
                float ampT = maxAmplitude * pow(st, ampPow);
                float wave = ampT * sin(st * waveFreq - uTime * waveSpeed + tentPhase);

                // Current drift - also stronger at the tip than the root,
                // per the brief. Reuses uCurrentDrive/uCurrentTurbulence
                // exactly as every prior round did.
                float driftNow = st * tentLen * (uCurrentDrive * currentAmt
                                    + uCurrentTurbulence * currentAmt * 0.35 * sin(uTime * 0.7 + tentPhase * 1.3));

                // Pulse-recoil ripple - reuses this jellyfish's own
                // contraction/phase exactly as every prior round did, now
                // also weighted by st so it grows toward the tip like every
                // other motion source here.
                float rippleNow = st * tentLen * contraction * 0.10 * sin(-phase * 10.0 + tentPhase);

                pts[si] = basePos + perp * (wave + driftNow + rippleNow);
            }

            // Distance to the polyline: walk each segment and smooth-min
            // (not hard-min) the running distance, so the vertex where two
            // consecutive segments meet at an angle - inevitable at a wave
            // crest/trough - blends into a soft rounded bend rather than a
            // visible sharp corner. tParam (whole-strand parametric
            // position, for taper/fade below) is blended with the same
            // weight so it stays consistent with the blended distance.
            float distC   = 1e5;
            float tParam  = 0.0;
            float blendK  = max(thickBase * 0.6, 0.0018);
            for (int si = 0; si < TENT_SAMPLES - 1; si++) {
                float tA = float(si)     / float(TENT_SAMPLES - 1);
                float tB = float(si + 1) / float(TENT_SAMPLES - 1);
                float rA = mix(thickBase, thickTip, tA);
                float rB = mix(thickBase, thickTip, tB);
                vec2  dh   = sdCapsuleT(local, pts[si], pts[si + 1], rA, rB);
                float d    = dh.x;
                float segT = mix(tA, tB, dh.y);
                if (si == 0) {
                    distC  = d;
                    tParam = segT;
                } else {
                    float bh   = clamp(0.5 + 0.5 * (distC - d) / blendK, 0.0, 1.0);
                    float newD = mix(distC, d, bh) - blendK * bh * (1.0 - bh);
                    tParam = mix(tParam, segT, bh);
                    distC  = newD;
                }
            }

            float edgeAA     = max(thickTip * 0.6, 0.0012);
            float tentMask   = 1.0 - smoothstep(-edgeAA, edgeAA, distC);
            // Soft fade toward the tip only (t=1) - never a hard clipped
            // end, per the brief's taper/fade requirement.
            float lengthFade = pow(1.0 - tParam, 0.70);

            // Reuses the bell's own bioluminescent rim color/brightness
            // formula verbatim - only the shape/motion technique changed
            // this pass, not the color/audio-reactivity design.
            float tentBrightness = tentMask * lengthFade * occlusion * brightVar;
            tentBrightness *= (0.55 + 0.85 * uLightDrive + 0.60 * uBloom);

            color += rimColor * tentBrightness * 1.15;
        }
    }
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

    // --- Jellyfish (Phase 2) -------------------------------------------------
    // Inserted here, after background/haze but before the nearer marine-snow
    // particulate tier, matching physical depth order - jellyfish are
    // embedded in the water column behind the particulate matter drifting
    // in front of everything. 3 mid-distance forms, each with its own
    // C#-integrated pulse phase (see UnderwaterScene.cs).
    renderJelly(color, p, 0.0, uJellyPhase0, hazeCombined, hazeDensity);
    renderJelly(color, p, 1.0, uJellyPhase1, hazeCombined, hazeDensity);
    renderJelly(color, p, 2.0, uJellyPhase2, hazeCombined, hazeDensity);

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
