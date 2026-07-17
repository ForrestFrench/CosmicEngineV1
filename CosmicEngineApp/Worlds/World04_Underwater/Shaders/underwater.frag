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
// Abyssal Bloom Phase 3 "Distant Event" (post "Living Water"/"Bloom
// refinement"): adds one new environmental layer - a vast, distant field of
// shifting abyssal glow low in the water column, composited into the water
// gradient itself (see "Abyssal Glow Field" section, just before main()) so
// every other layer naturally sits in front of it and partially obscures it.
// Addresses ChatGPT's review that the scene still mostly read as "jellyfish
// under light rays, with plankton as a supporting layer" - this is
// deliberately NOT another jellyfish/creature element (explicit hard
// constraint from the brief: abstract/atmospheric only, no cartoon sea
// creature) - it is pure domain-warped noise, gated to emerge gradually
// across the same uBloom arc every other gated layer in this file already
// uses. Jellyfish/tentacle/plankton code is untouched by this pass.
//
// Abyssal Bloom Phase 4 "Presence / Color / Depth Population" (direct user
// review feedback, superseding ChatGPT's originally-recommended "Phase 4:
// Song Feel/Audio Tuning" for this pass): the user found the scene still too
// sparse/monochromatic for a full song. Four additions, all confined to this
// file (plus a profile-scaled count knob in UnderwaterScene.cs/CosmicEngine.
// cs, no C#-integrated state needed): (1) "Distant Alien Presence" - a huge,
// slow, deliberately abstract shadow/darkening mass low-frequency-drifting
// through the water column, gated by its own independent "appears every few
// seconds" time cycle layered on top of the existing uBloom arc (see
// "Distant Alien Presence" section) - explicitly a DARKENING effect, not a
// drawn/glowing silhouette, specifically to avoid a "pasted silhouette
// sticker" or "cartoon sea creature" read; (2) several small,
// cheap, reduced-detail background jellyfish (see "Background Jellyfish"
// section) adding depth population without running the frozen, six-round-
// refined foreground tentacle pipeline; (3) modest, tasteful color-variation
// nudges (violet/indigo haze shadows, green/blue caustic drift, a pulsing
// magenta/indigo glow-field accent, a rare warm plankton spark, a cool-hue
// ray drift) layered onto existing brightness terms, never a new color
// mechanism or a full-saturation wash; (4) every addition measured against
// the existing ~65fps High-profile floor and, where a spatial early-out
// wasn't practical (the presence layer's own necessarily large on-screen
// footprint - see that section's perf note), kept to the cheapest noise
// construction that still reads as intended. Foreground jellyfish/tentacles
// and the abyssal glow field's own shape/technique are both explicitly
// frozen/untouched by this pass - see AUDIT.md for the full brief.
//
// Abyssal Bloom Phase 4 Addendum 1 "Monster/Presence Visibility Fix" (direct
// user-review follow-up: "I didn't see the monster presence at all"): the
// Distant Alien Presence darkening above was originally composited
// immediately after the bare water gradient, before the abyssal glow field/
// rays/caustics/haze added their own light on top of it - full-composite
// screenshots (not isolated renders) showed this left the effect barely
// perceptible even at forced-peak visibility. Fixed by moving that mix to
// run AFTER those atmosphere layers (still before the background/hero
// jellyfish and particle/plankton layers, so near-field elements still
// obscure it) and widening presenceArc's rise so meaningful visibility
// starts earlier in the bloom arc - see "Distant Alien Presence" section's
// own comments and AUDIT.md Entry 47 Addendum 1 for full root-cause detail
// and full-composite screenshot evidence. No change to the shape/placement
// logic (monsterShape()/monsterCenter()) or the darkening-not-brightening
// technique itself.
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

// Phase 1 "Living Water" (fresh architect plan, post Phase 3): C#-integrated
// jellyfish drift position/depth (UnderwaterScene.cs's JellyDriftState) -
// position now comes from here instead of being derived purely from hash
// constants inside renderJelly(), for the identical reason phase already is
// (the drift rate is current-driven and therefore time-varying). Paired
// floats, combined into vec2 in renderJelly()'s caller below, rather than a
// vec2 uniform - ShaderProgram.cs has no SetVector2 and adding one would be
// an infra change riding inside this shader-art pass (governance rule 12).
uniform float uJellyPos0X, uJellyPos0Y;
uniform float uJellyPos1X, uJellyPos1Y;
uniform float uJellyPos2X, uJellyPos2Y;
uniform float uJellyDepth0; // 0 (far) .. 1 (near)
uniform float uJellyDepth1;
uniform float uJellyDepth2;

// Phase 1 "Living Water": Engine/Camera.cs's already-running Offset,
// forwarded here (paired floats, same reason as uJellyPos* above) and
// applied as a per-layer parallax shift in main() - see the parallax
// comment there for the per-layer multipliers.
uniform float uCameraOffsetX;
uniform float uCameraOffsetY;

// Phase 1 "Living Water": profile-scaled plankton bloom-field count - see
// main()'s plankton section.
uniform int uPlanktonCount;

// Phase 4 "Presence / Color / Depth Population": profile-scaled background-
// jellyfish count - see "Background Jellyfish" section below. Deliberately
// no C#-integrated per-instance state (unlike the 3 foreground jellies') -
// these are cheap, shader-hash-placed, and don't need audio-reactive lap
// timing, so a new UnderwaterScene struct/uniform set would be unnecessary
// plumbing for what's meant to be a cheap background layer.
uniform int uBgJellyCount;

const int MAX_PARTICLES = 64;
const int MAX_PLANKTON  = 140;
const int MAX_BG_JELLY  = 8;
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
//
// Phase 3 ray/caustic interaction polish (AUDIT.md Entry 43): two refinements
// to the existing band+attenuation shape, tuning rather than rebuilding it -
// (1) edge softening: the core Gaussian band is a fairly crisp cutout on its
// own, so a second, wider/dimmer "halo" Gaussian is added underneath it
// (screen-blended, not just summed, so it can never push a ray brighter than
// its own core) - this feathers the visible edge into the surrounding water
// instead of reading as a hard-edged cutout, while leaving the bright core
// unchanged. (2) depth-attenuation: a soft smoothstep ease-in near the ray's
// own origin (fadeIn) replaces the old "full brightness from along=0"
// discontinuity with a gentle ramp, and the exponential falloff exponent was
// eased slightly (3.0 -> 2.6) for a marginally longer, more graceful death
// into darkness - still comfortably reaching near-zero (exp(-2.6*1.3) =~
// 0.034) well before the bottom third of frame, matching the original
// design intent, just with a softer curve at both ends instead of one hard
// start and one abrupt-feeling tail.
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

        float bandCore  = exp(-(perp * perp) / (width * width));
        float haloWidth = width * 2.4;
        float bandHalo  = exp(-(perp * perp) / (haloWidth * haloWidth)) * 0.30;
        float band      = bandCore + bandHalo * (1.0 - bandCore);

        float fadeIn = smoothstep(0.0, 0.12, along);
        float atten  = exp(-along * 2.6) * fadeIn;

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
// `idx` picks this jellyfish's hash-derived bell/tentacle *character* (bell
// proportions, tentacle count/bend/etc. - unchanged, still hash-seeded, no
// C# state needed for that). `jellyPos`/`depthNorm` are Phase 1 "Living
// Water" additions - C#-integrated drift position and depth (see
// UnderwaterScene.cs's JellyDriftState), replacing the old purely-hash-
// derived-per-frame position below. `hazeCombined`/`hazeDensity` are the
// Phase 1 (atmosphere) haze values already computed at this pixel in main() -
// read (not recomputed) so a jellyfish behind heavier haze visibly recedes,
// per the plan's "reads as embedded in the water column" requirement.
//
// Translation invariance (Phase 1 "Living Water" regression guard): every
// subsequent line in this function derives its shape/motion only from
// `local = p - jellyPos`, `seed`, `phase`, `depthScale` (size), and the haze
// params above - never from `jellyPos` itself again after this point. Moving
// `jellyPos` therefore only ever translates the rendered result; it cannot
// change the tentacle/bell's own shape or quality. This is what makes
// swapping a hash-derived static position for a moving C#-integrated one
// safe without touching any of the six rounds of tentacle-rescue work below.
void renderJelly(inout vec3 color, vec2 p, float idx, float phase,
                  float hazeCombined, float hazeDensity,
                  vec2 jellyPos, float depthNorm) {
    float seed = idx * 41.7 + 5.0;

    float depthScale = mix(0.50, 1.15, clamp(depthNorm, 0.0, 1.0));

    vec2 jellySize = vec2(0.116, 0.080) * depthScale;

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
    // farther jellyfish read as receding into the water column. distFactor's
    // range was recalibrated (Phase 1 "Living Water") to match depthScale's
    // new 0.50-1.15 range (was 0.78-1.16, tied to the old static per-jelly
    // hash) - same 0 (near, full clarity) .. 1 (far, most occluded) meaning.
    float distFactor = clamp((1.15 - depthScale) / 0.65, 0.0, 1.0);
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

// --- Background Jellyfish (Phase 4 "Presence / Color / Depth Population") --
//
// User feedback: "three jellyfish floating around is not interesting enough
// for a full song" / "add a few more jellyfish farther in the background" /
// "the current scene feels too sparse." This adds several (profile-scaled,
// uBgJellyCount, 4-8) small, faint, reduced-detail organisms distinct from
// the 3 hero jellyfish above - explicitly NOT running renderJelly()'s
// six-round-refined SDF bell + traveling-wave-tentacle pipeline (frozen this
// pass; running it 4-8x more would also be a real, unnecessary perf risk per
// this pass's own brief). Deliberately cheap: a soft radial glow "bell" plus
// a faint ring-edge cue - no dome/skirt SDF construction, no tentacle field,
// no fbm2/vnoise2 calls at all (hash1/sin/exp only).
//
// Placement/motion is pure shader-side hash + uTime (no C#-integrated drift
// struct, unlike the 3 hero jellies) - these don't need audio-reactive lap
// timing, so a simple continuous hashed drift is sufficient and considerably
// cheaper than adding new per-instance C#/uniform plumbing, per this pass's
// own brief ("consider whether pure shader-side hash-based placement is
// sufficient/cheaper for background elements that don't need the same drift
// complexity as foreground jellyfish"). The off-frame wrap bound (1.15)
// mirrors the hero jellies' own JellyEdgeX=1.05 trick (UnderwaterScene.cs) -
// both ends of a lap sit outside the visible ~0.89 half-width, so the wrap
// itself is never seen.
void renderBackgroundJelly(inout vec3 color, vec2 pBg, float idx,
                            float hazeCombined, float hazeDensity) {
    // Distinct hash domain from every other per-instance seed in this file
    // (hero jellies idx*41.7+5, plankton fi*23.71+11, marine snow
    // fi*19.61+7) so no two systems' hashes ever accidentally correlate.
    float seed = idx * 29.3 + 71.0;

    float depthNorm = hash1(seed + 1.0); // 0 (far) .. 1 (near-ish) - still a background tier, never as large/clear as the 3 hero jellies
    float sizeScale = mix(0.32, 0.58, depthNorm); // notably smaller than the hero bell (jellySize = (0.116,0.080)*[0.50,1.15])
    vec2  bgSize = vec2(0.044, 0.031) * sizeScale;

    // Perf: prefix hash count halved (5 -> 3 unconditional hash1() calls,
    // paid by every fragment regardless of the reach check below, so this is
    // the part worth trimming - same "single hash, multiple sub-values"
    // technique already established on this file's own plankton loop, Entry
    // 45 addendum). hMotion feeds both speed and dirSign; hPos feeds both
    // the lap-phase offset and yCenter. This introduces a mild, disclosed
    // correlation between each pair (e.g. a jelly's drift speed and its
    // left/right direction) - both are minor timing/placement variance, not
    // a structural/color identity attribute, so judged low visual risk, the
    // same standard Entry 45's addendum applied to its own analogous
    // lifeSpeed/phase consolidation.
    float hMotion = hash1(seed + 2.0);
    float speed   = mix(0.0035, 0.0095, hMotion); // very slow - a full crossing takes several minutes, background pacing, not a foreground drift
    float dirSign = fract(hMotion * 53.73) > 0.5 ? 1.0 : -1.0; // not all drifting the same direction
    float hPos    = hash1(seed + 4.0);
    float cyc     = fract(uTime * speed + hPos);
    float posX    = dirSign * mix(-1.15, 1.15, cyc);
    float yCenter = mix(-0.38, 0.32, fract(hPos * 91.71)); // spread across mid/lower water column, not clustered, not evenly spaced

    // Perf (self-caught before reporting - same "coarse position, defer the
    // rest" prefix-cost pattern already proven on this file's own plankton
    // loop, AUDIT.md Entry 45 addendum): yWander's own hash1/sin calls are
    // deferred until AFTER the reach check below, using yCenter alone (plus
    // a fixed conservative pad covering yWander's own max 0.035 amplitude,
    // added to `reach` so the check stays exact/conservative, not
    // approximate) for the coarse position instead - saves 2 hash1 + 1 sin
    // for every fragment the check culls, which given this layer's own
    // necessarily-large early-out footprint (a background jelly's glow, not
    // a tiny point like a plankton mote) still culls the large majority of
    // the frame per instance.
    vec2  coarsePos = vec2(posX, yCenter);

    // Cheap early-out before any further math - most fragments, most
    // instances, most frames are nowhere near a given background jelly.
    vec2  toBgCoarse = pBg - coarsePos;
    float reach = bgSize.x * 3.2 + 0.035;
    if (dot(toBgCoarse, toBgCoarse) > reach * reach) return;

    float yWander = 0.035 * sin(uTime * mix(0.025, 0.07, hash1(seed + 6.0)) + hash1(seed + 7.0) * 6.2831853);
    vec2  bgPos   = vec2(posX, yCenter + yWander);
    vec2  toBg    = pBg - bgPos;

    vec2  rel    = toBg / max(bgSize, vec2(0.001));
    float distSq = rel.x * rel.x + rel.y * rel.y * 1.5; // mild vertical elongation - a bell-like read without an SDF

    // Slow, cheap pulse (brightness/size only, no shape reconstruction) - a
    // hint of life without paying for the hero jellies' full contract/relax
    // SDF deformation.
    float pulseT   = 0.5 + 0.5 * sin(uTime * mix(0.12, 0.26, hash1(seed + 8.0)) + hash1(seed + 9.0) * 6.2831853);
    float bodyGlow = exp(-distSq * 1.15) * mix(0.85, 1.15, pulseT);
    float dist     = sqrt(distSq);
    float rim      = exp(-abs(dist - 1.0) * 5.0) * 0.6; // the only "bell edge" suggestion - no tentacles at all, per this pass's reduced-detail requirement

    float occlusion = (1.0 - hazeCombined * hazeDensity * 0.80) * mix(0.30, 0.85, depthNorm);

    // Color variation (Phase 4 goal): background jellies lean more toward
    // blue-violet than the hero jellies' teal-forward palette, adding hue
    // variety to the scene without touching the frozen foreground rendering.
    // A rare (tier-gated), bloom-gated magenta accent keeps this "trippy but
    // controlled" per the brief - never a rainbow spread.
    vec3  bgColorA    = vec3(0.30, 0.62, 0.70); // cyan-teal
    vec3  bgColorB    = vec3(0.42, 0.40, 0.82); // blue-violet
    vec3  bgColor     = mix(bgColorA, bgColorB, hash1(seed + 10.0));
    float magentaTier = hash1(seed + 11.0);
    bgColor = mix(bgColor, vec3(0.62, 0.32, 0.68), smoothstep(0.86, 1.0, magentaTier) * smoothstep(0.55, 1.0, uBloom) * 0.5);

    float brightness = (0.10 + 0.30 * uLightDrive + 0.28 * uBloom) * occlusion;
    color += bgColor * (bodyGlow + rim) * brightness;
}

// --- Distant Alien Presence (Phase 4 "Presence / Color / Depth Population") -
//
// Compositing position (Phase 4 Addendum 1): this field's own shape/gate
// logic below is unchanged since Phase 4, but WHERE the resulting darkening
// mix is applied in main() moved - see the "Distant Alien Presence" call
// site there (now after the water gradient/glow field/rays/caustics/haze,
// before the jellyfish/particle layers) for why the original "immediately
// after the water gradient" position left the effect invisible in the full
// composite.
//
// User feedback (verbatim, via the orchestrating session): "add the shadow/
// presence of some alien monster appearing in the distance every few
// seconds"; "the monster should start as a faint outline and get more clear
// throughout the song"; "it should always remain a shadow in the background,
// never a literal foreground creature." This is named the single highest-
// risk element in this pass's own brief - the explicit target is a *sensed*
// presence, not a drawn creature: huge, distant, partially obscured, slow,
// mysterious, possibly imagined. It must NOT read as cartoonish, a literal
// sea-monster drawing, a foreground character, a pasted silhouette sticker,
// or a face.
//
// Design choice made specifically to avoid the "pasted silhouette" failure
// mode: this is a DARKENING effect (mixes the already-composited water color
// toward a near-black shadow tint in main()), not a brightening/glow effect
// like every other layer in this scene (rays/caustics/jellyfish/glow-field/
// plankton). A silhouette built as an additive glow shape reads as "an
// object placed in front of the water"; a soft, irregular darkening diffused
// INTO the water reads as an absence/shadow, which is the actual target. No
// rim light, no edge highlight, no outline of any kind is added anywhere in
// this section - an edge highlight is exactly what would turn this into the
// rejected "silhouette sticker" look.
//
// Shape: one large, anisotropic (wide/low, never circular - "never an orb")
// irregular mass. Design-correction history (self-caught before reporting,
// not a review failure - see AUDIT.md): the first implementation built the
// shape as a smooth exp() Gaussian envelope with the SAMPLING POSITION
// domain-warped by noise sampled in full-frame/world coordinates. An
// isolated-render check (same technique Entry 46 used to catch its own two
// design corrections) showed this read as a single smooth, coherent
// "eel/leaf/pill" shape with a gentle S-bend - exactly the "a thing" failure
// this pass's own brief warns against - because the warp noise's frequency
// was far too low relative to the mass's own footprint (MONSTER_SCALE_X/Y):
// under one noise cycle spanned the entire shape, so the warp only bent the
// whole envelope coherently instead of perturbing different parts of the
// boundary independently. Root cause diagnosed (not guessed) via that same
// isolated-render capture, at high magnification, boosted brightness.
//
// Fix: rebuilt around the same "soft gate bounding an irregular patchy noise
// field" architecture abyssalGlowShape() already uses successfully (macro +
// detail fbm2 layers, a core threshold for patchy internal contrast) -
// sharing that proven principle is explicitly allowed by the brief - but
// built as its own distinct construction, not a reuse/rename: (1) the field
// noise here is sampled in SHAPE-LOCAL coordinates (`rel`, already
// normalized by the mass's own scale) at a frequency tuned to that local
// scale, not world/frame coordinates - this is the specific fix, ensuring
// several independent lobes/gaps appear across the mass's own extent instead
// of one smooth bend; (2) the outer gate is an anisotropic Gaussian bound
// tied to a moving `monsterCenter`, not a static screen-space mask, so the
// whole irregular field travels and reshapes as one drifting presence rather
// than sitting fixed in world space; (3) composited as a darkening mix in
// main(), never additive brightening - see that call site's own comment for
// why. No explicit fin/tendril shape is ever drawn - the irregular field's
// own lobes/gaps are what imply them, per the brief's "implied, not drawn"
// instruction.
//
// Perf note (measured, not assumed - see AUDIT.md): unlike the glow field
// (gated to only the bottom ~35-40% of frame via glowVMask) or the tentacle/
// plankton loops (whose per-element reach is tiny relative to the screen),
// this layer's own design intent - "huge" - means its on-screen footprint at
// full visibility is comparable to the visible frame itself, so a spatial
// early-out here provides little real pruning (see main()'s own comment at
// the call site). Kept to two 1-octave fbm2 calls total (half the abyssal
// glow field's own 4-vnoise2 budget) to stay within this pass's own
// performance budget despite the necessarily large footprint.
vec2 monsterCenter(float t) {
    // Very slow, independent drift - NOT derived from uBloom or any audio
    // signal, a pure internal clock (per the brief's explicit "should have
    // its own rhythm layered on top of, not derived from, the bloom arc"
    // instruction). Two incommensurate low-frequency sine terms per axis so
    // the crossing never reads as a mechanical back-and-forth sweep; a full
    // lateral crossing of the visible frame takes on the order of several
    // minutes - "slow and massive", not a moving cinematic element.
    float cx = 0.55 * sin(t * 0.0065 + 1.1) + 0.22 * sin(t * 0.0021 - 0.4);
    float cy = -0.06 + 0.11 * sin(t * 0.0043 + 2.7);
    return vec2(cx, cy);
}

const float MONSTER_SCALE_X = 0.44;
const float MONSTER_SCALE_Y = 0.17;

float monsterShape(vec2 pos, vec2 center, float t) {
    vec2 rel = (pos - center) / vec2(MONSTER_SCALE_X, MONSTER_SCALE_Y);

    // Soft anisotropic gate - bounds where the irregular field below is
    // allowed to contribute at all (never alone sufficient to read as "a
    // shape" on its own, same role abyssalGlowShape's own vertical mask
    // plays for its noise field). Wide/low, never circular - "never an orb".
    float gate = exp(-(rel.x * rel.x * 0.55 + rel.y * rel.y * 1.9));
    if (gate < 0.004) return 0.0;

    // Irregular patchy field, sampled in SHAPE-LOCAL coordinates (rel, not
    // pos/world-space) - this is the actual fix (see the section header
    // above): at this frequency, several independent lobes/gaps appear
    // across the mass's own footprint instead of one smooth bend. Two
    // independently-drifting layers (macro shape + a smaller-scale detail
    // churn warped by the macro layer) - the same two-layer principle
    // abyssalGlowShape() uses, applied at a different, shape-local scale.
    vec2  fieldUV = rel * 1.55 + vec2(t * 0.050, -t * 0.036);
    float macro   = fbm2(fieldUV, 1) * 2.0; // renormalize fbm2(.,1)'s [0,0.5] range to [0,1]

    vec2  detailUV = rel * 3.3 + vec2(macro * 0.7, -macro * 0.4) + vec2(-t * 0.041, t * 0.029);
    float detail    = fbm2(detailUV, 1) * 2.0;

    float field = clamp(macro * 0.55 + detail * 0.45, 0.0, 1.0);
    // Patchy core threshold - numerous, irregular, continuously reshaping
    // with the field underneath, never a fixed/countable shape (same
    // principle abyssalGlowShape's own `core` term establishes).
    float core = smoothstep(0.30, 0.62, field);

    return gate * clamp(field * 0.30 + core * 0.85, 0.0, 1.0);
}

// --- Plankton flow/pulse helpers (Phase 2 "Bloom refinement") --------------
// Added to give the Phase 1 "Living Water" plankton bloom field real
// structure - flow-coherent streaming and traveling brightness pulse
// trains - instead of independent per-mote random drift/twinkle. Kept as
// small standalone functions (rather than inlined into the loop below) for
// the same readability reason rayEnvelope()/rayOrigin()/rayAngle() are split
// out above; both are cheap (hash1/sin only, no fbm2/vnoise2 - a full 2D
// noise-field evaluation per plankton per pixel was considered and rejected
// on cost grounds given the loop already runs up to MAX_PLANKTON times per
// pixel) and reuse only infrastructure this file already has (hash1, plus
// the same lane-blending pattern already proven on this file's own jellyfish
// tentacles - AUDIT.md Entry 41 addendum 1 - here applied to plankton
// "current channels" instead of tentacle strands).

// Number of coarse horizontal current channels plankton are grouped into.
// Small on purpose - the point is a few visually-distinguishable streams,
// not per-particle individuality (that's what the residual turb wobble in
// the main loop is for).
const float PLANKTON_CHANNEL_COUNT      = 7.0;
const int   PLANKTON_CHANNEL_BOUNDARIES = 8; // PLANKTON_CHANNEL_COUNT + 1 endpoints

// Perf correction (self-caught before reporting this pass done - see
// AUDIT.md for the measured before/after): the 8 channel-boundary angles
// below depend only on uTime and a small integer index (0-7), never on any
// per-plankton value. The first implementation recomputed all of this
// (2 hash1 + 2 sin) from scratch for every one of up to 90 plankton per
// pixel, which cost real, measured fps (High profile at worst-case forced
// bloom dropped from an already-existing Phase 1 baseline of ~52fps to
// ~44fps, a violation of the 60fps floor). Hoisting the 8 boundary angles
// into a once-per-pixel precomputation (this function; called once, before
// the plankton loop, only when bloomNorm already gates the loop open) and
// having planktonChannelAngle() below do a cheap array lookup + blend per
// plankton instead removes that redundancy entirely - identical visual
// result (verified - see AUDIT.md), far fewer transcendental calls.
void computePlanktonChannelBoundaryAngles(out float chAngle[PLANKTON_CHANNEL_BOUNDARIES]) {
    for (int c = 0; c < PLANKTON_CHANNEL_BOUNDARIES; c++) {
        float cf    = float(c);
        float ang   = hash1(cf * 12.9 + 3.0) * 6.2831853;
        // Slow per-channel angle drift - channels reshape gradually over
        // minutes, never frozen, never fast enough to look like jitter.
        float drift = sin(uTime * 0.008 + cf * 5.3) * 0.6;
        chAngle[c]  = ang + drift;
    }
}

// Looks up this plankton's shared current-channel flow angle from the
// precomputed boundary array above - a plankton's baseX places it between
// two adjacent channels, smoothly blended (smoothstep, not a hard switch)
// so channel boundaries are never a visible seam, mirroring the tentacle
// lane technique's own "two adjacent hashed lanes blended by fract"
// structure (AUDIT.md Entry 41 addendum 1).
float planktonChannelAngle(float baseX, float chAngle[PLANKTON_CHANNEL_BOUNDARIES]) {
    float chCoord = (baseX * 0.5 + 0.5) * PLANKTON_CHANNEL_COUNT; // 0..count across the field width
    int   chLo    = clamp(int(floor(chCoord)), 0, PLANKTON_CHANNEL_BOUNDARIES - 2);
    float chBlend = smoothstep(0.0, 1.0, fract(chCoord));
    return mix(chAngle[chLo], chAngle[chLo + 1], chBlend);
}

// Traveling brightness pulse-train: a literal function of position and
// time (not a per-plankton independent phase), so as uTime advances the
// bright "wavefront" sweeps continuously through the field - this is what
// makes the bloom read as "something is happening" rather than "particles
// twinkling randomly". Direction is fixed, roughly matching the ambient
// current's own rightward/downward bias so waves visually travel with the
// water rather than across it. freq/speed both rise with bloomNorm so
// pulse trains are slow/sparse in Bioluminescent Awakening and quick/tight
// by Bloom Event - a qualitative escalation, not just a brightness/density
// scale-up.
float planktonPulseWave(vec2 pos, float bloomNorm) {
    vec2  waveDir = normalize(vec2(0.6, 1.0));
    float freq    = mix(2.1, 4.2, bloomNorm);
    float speed   = mix(0.55, 1.55, bloomNorm);
    float phase   = dot(pos, waveDir) * freq - uTime * speed;
    return 0.5 + 0.5 * sin(phase);
}

// --- Abyssal Glow Field (Phase 3 "Distant Event") ---------------------------
// Fable-authored roadmap item, directly addressing ChatGPT's review of the
// "Living Water"/"Bloom refinement" passes: "the next visual gain should
// come from stronger environmental transformation, not more jellyfish
// anatomy... the scene still mostly reads as jellyfish under light rays,
// with plankton as a supporting layer." This adds a vast, distant field of
// shifting abyssal glow low in the water column, well behind everything
// else in the scene.
//
// Explicit constraint (hard, not a preference): must read as abstract/
// atmospheric, not a creature or character - no symmetric shape, no orb, no
// silhouette with a recognizable body ("do not make a cartoon sea
// creature"). Built the same way this file already avoids single-shape
// reads elsewhere (rayField's fanned bands, the plankton field's channel-
// based streams, the haze layer's own FBM murk): one continuous, irregular,
// domain-warped noise field with a soft directional (vertical, not radial)
// mask - never a radially-symmetric shape, never a single center. Two
// independently-drifting FBM layers compose into the field:
//   - a low-frequency "macro" shape (less than one full noise cycle across
//     the visible frame width) defining a few broad, uneven lobes - this is
//     what reads as one continuous vast presence rather than many small
//     glints;
//   - a higher-frequency "detail" layer, itself domain-warped by a third,
//     independently-drifting noise term (its own warp, distinct phase/rate
//     from the macro layer, so the two components never lock into a single
//     moving pattern) - this is the internal churn that reads as "something
//     down there is alive," not a static painted backdrop.
// The two layers' drift rates/directions are deliberately incommensurate
// (different axes, different speeds) so no part of this field ever repeats
// or oscillates predictably - there is nothing here for the eye to latch
// onto as a body, limb, or face.
// Perf (measured, not assumed - see AUDIT.md for the before/after fps): the
// first implementation used 2 octaves on all three fbm2 calls here (6
// vnoise2 calls per affected pixel) plus a vertical mask reaching nearly to
// mid-frame, and forced-Bloom-Event High measured 61.0-61.4fps avg - above
// the 60fps floor but with too little margin against this project's own
// documented session-level fps variance. The macro shape and its warp term
// are both meant to be broad/low-frequency by design (that is the whole
// point of "macro") and lost negligible visual quality dropping to 1 octave
// each (confirmed by screenshot comparison) - only the detail layer (the
// "internal churn" component) benefits from the extra octave, so it alone
// keeps 2. This halves the vnoise2 count on 2 of the 3 calls (6 -> 4 total)
// at zero visible cost, combined with the tightened glowVMask footprint
// below.
// Design correction (self-caught before reporting, not a review failure -
// see AUDIT.md): a first pass combined macro/detail directly assuming
// fbm2(...) already spans roughly [0,1], but fbm2()'s own amplitude series
// (0.5 + 0.25 + ...) means an N-octave call actually tops out at 1-0.5^N -
// 0.5 for 1 octave, 0.75 for 2 - so the un-normalized combination topped out
// around 0.6 with a mean near 0.3, and the whole field was consequently far
// too dim to read at any bloom level (confirmed via an isolated-render
// capture showing peak pixel values of ~2-6/255 even at forced full Bloom
// Event). Each fbm2 term is now explicitly renormalized to its own analytic
// max before combining, so `shape` genuinely spans close to [0,1] as the
// rest of this function's threshold/weight constants assume.
float abyssalGlowShape(vec2 pos, float drift) {
    // Design correction #2 (self-caught, isolated-render check at high
    // magnification - see AUDIT.md): with macro at 0.55x frequency, less
    // than one full noise cycle fits across the visible frame width, so the
    // field's horizontal variation was far weaker than the vertical mask's
    // own gradient - the composite read as "one smooth wavy band" rather
    // than an irregular field with multiple distinguishable lobes. Raised to
    // 0.85x (roughly 1.5 cycles across the frame) so at least 2-3 lobes are
    // visible at once, still broad/vast relative to any foreground element
    // but no longer reading as a single coherent wave.
    vec2  macroUV = pos * 0.85 + vec2(uTime * 0.006, uTime * 0.004 * drift);
    float macro   = fbm2(macroUV, 1) * 2.0;        // fbm2(.,1) maxes at 0.5 - renormalize to [0,1]

    vec2  warpUV   = pos * 1.3 + vec2(-uTime * 0.010 * drift, uTime * 0.007);
    float warp     = fbm2(warpUV, 1) - 0.25;        // centered around 0 (fbm2(.,1) mean ~0.25)
    vec2  detailUV = pos * 2.6 + vec2(warp * 0.6, warp * 0.4)
                                + vec2(uTime * 0.009 * drift, -uTime * 0.005);
    float detail   = fbm2(detailUV, 2) * (1.0 / 0.75); // fbm2(.,2) maxes at 0.75 - renormalize to [0,1]

    return clamp(macro * 0.60 + detail * 0.40, 0.0, 1.0);
}

void main() {
    vec2 uv = vUV;
    uv.y = 1.0 - uv.y;

    float aspect = 1.7778;
    vec2  p = (uv - 0.5);
    p.x *= aspect;
    // Positive p.y = up for every constant below (matches wind_turbine_fire.frag's convention).
    p.y = -p.y;

    // --- Camera parallax (Phase 1 "Living Water") -----------------------------
    // Engine/Camera.cs's Offset - a slow, layered sine/cosine drift, already
    // updated every frame by the engine loop regardless of which world reads
    // it - was previously unused by this scene (see UnderwaterScene.cs's
    // constructor). Applied here purely as a per-layer coordinate-space
    // shift (no rotation, no zoom - this should read as slow current drift,
    // not a moving cinematic camera) so depth planes visibly drift at
    // different rates, the standard parallax cue that background layers sit
    // physically farther away than foreground ones. Multipliers, farthest to
    // nearest:
    //   water gradient                 0.2x  - the most distant backdrop
    //   rays/caustics                   0.5x
    //   haze/murk                        0.7x
    //   particles/plankton/jellyfish  1.0x (unscaled) - the "near" reference
    //     layer every other multiplier above is judged relative to.
    //
    // Perf note (honest, measured - not assumed): the un-refracted per-layer
    // parallax bases are intentionally not kept as separate named locals -
    // only their already-refracted forms below are sampled, so this avoids
    // 3 unused-past-this-point vec2s. Tried this specifically as a candidate
    // fix for a synthetic worst-case measurement (a temporary, since-removed
    // debug override forcing all 3 jellyfish to maximum depth/size and
    // clustered on-screen simultaneously - not a realistic case, since
    // independent per-lap hashing rarely puts all 3 there at once) that
    // showed High profile at 60.1-62.6fps avg across several 3-run sets
    // under that specific extreme - within margin of the mandatory 60fps
    // floor. Measured before/after:
    // this collapse made no measurable difference (confirmed further by
    // forcing uCameraOffset to a hardcoded zero under the same worst case,
    // which also made no measurable difference) - the GLSL compiler was
    // already eliminating the dead intermediates, unlike the tentacle loop's
    // own genuine two-pass-vs-merged regression (AUDIT.md Entry 41 addendum
    // 6's "optimization 3"). Kept anyway as a harmless readability
    // simplification, not a performance claim. The real driver of that
    // worst-case number was not isolated further this pass - see the
    // mandatory performance section of this pass's evidence for the actual
    // measured numbers (both the realistic, unforced smoke test, which is
    // the pass/fail criterion, and this documented synthetic-extreme
    // boundary case).
    vec2 uCameraOffset = vec2(uCameraOffsetX, uCameraOffsetY);
    vec2 pNear = p - uCameraOffset;

    // --- Foreground refraction warp (Phase 3, AUDIT.md Entry 43) -------------
    // A gentle, always-on screen-space UV displacement suggesting looking
    // through moving water, applied ONLY to the diffuse/background layers
    // below (water gradient, god rays, caustics, haze) - the two-tier lesson
    // this project already learned on Wind Turbine Fire's heat-distortion fix
    // (AUDIT.md Entry 34) and flagged again in Entry 41's own risk notes: a
    // uniform full-strength warp applied to a thin curved structural
    // silhouette reads as a wobble, not a shimmer. This pass takes the
    // conservative end of that lesson rather than a tapered partial warp -
    // jellyfish/tentacles/particles/plankton below are rendered against
    // pNear (parallax-shifted but never refraction-warped), i.e. full
    // exclusion from refraction specifically, not a smaller-amplitude
    // version - per this phase's own explicit guidance to prefer excluding
    // them outright over risking any regression to the six-round tentacle
    // rescue (Entry 41 addenda 1-6). Amplitude derives from the
    // already-existing uCurrentDrive/uCurrentTurbulence uniforms (no new
    // uniform needed) so refraction visibly intensifies with water motion; a
    // small baseline keeps it never fully zero, matching this file's
    // existing "nothing goes fully static at rest" convention. Phase 1
    // "Living Water": each layer's parallax multiplier and the shared
    // refraction offset are combined in one step (see the perf note above)
    // so parallax and refraction still compose exactly as before, just
    // without a separate un-refracted intermediate per layer.
    float refractAmp = 0.0030 + 0.0026 * uCurrentTurbulence + 0.0012 * uCurrentDrive;
    vec2  refractOffset = vec2(
        sin(p.y * 16.0 + uTime * 1.35),
        cos(p.x * 13.0 - uTime * 1.05)
    ) * refractAmp;
    vec2  pRefractWater        = p - uCameraOffset * 0.2 + refractOffset;
    vec2  pRefractRaysCaustics = p - uCameraOffset * 0.5 + refractOffset;
    vec2  pRefractHaze         = p - uCameraOffset * 0.7 + refractOffset;

    // depthT: 0 at the very top of frame (light entry), 1 at the very
    // bottom (abyss). Perturbed with low-frequency horizontal noise so the
    // three-zone gradient is never a flat ramp. Sampled against pRefractWater
    // (Phase 3 refraction + Phase 1 parallax, water gradient's own 0.2x
    // layer) so the water-column gradient carries both the refraction
    // shimmer and its own (subtle) parallax drift.
    float depthT = clamp(0.5 - pRefractWater.y, 0.0, 1.0);
    float depthNoise = (fbm2(vec2(pRefractWater.x * 1.1 + 3.0, uTime * 0.015), 2) - 0.5) * 0.12;
    float depthTN = clamp(depthT + depthNoise, 0.0, 1.0);

    // --- Three-zone vertical water gradient ------------------------------
    vec3 zoneTop    = vec3(0.045, 0.130, 0.120); // dim green-teal, light entry
    vec3 zoneMid    = vec3(0.032, 0.052, 0.098); // desaturated slate-blue
    vec3 zoneBottom = vec3(0.006, 0.009, 0.022); // near-black blue-violet abyss

    vec3 color = mix(zoneTop, zoneMid, smoothstep(0.0, 0.45, depthTN));
    color = mix(color, zoneBottom, smoothstep(0.45, 1.0, depthTN));

    // --- Abyssal Glow Field (Phase 3 "Distant Event") -----------------------
    // Composited immediately after the water-gradient "canvas" and before
    // every other layer (rays/caustics/haze/jellyfish/plankton) - per this
    // pass's own design, everything below naturally draws on top of and
    // partially obscures this field, which is exactly what reinforces its
    // distance rather than competing with the foreground elements. Sampled
    // against pRefractWater - the same most-distant 0.2x-parallax +
    // refraction coordinate the water gradient's own depthT/depthNoise
    // already use - so this reads as part of the same distant backdrop, not
    // a separate nearer layer; no new parallax multiplier introduced.
    //
    // Gated on two independent, cheap checks before any of the (relatively)
    // expensive FBM work runs, mirroring this file's own established "gate
    // cost, not just visible output" pattern (the plankton field's
    // bloomNorm > 0.001 gate; the tentacle/plankton loops' spatial early-
    // outs):
    //   1. glowArc - a uniform-only value (identical for every pixel this
    //      frame, so this check costs nothing per-pixel), near-zero through
    //      Deep Calm and most of Bioluminescent Awakening, rising through
    //      Current Build, fullest at Bloom Event - this is the "emerge
    //      gradually through the bloom arc" requirement, and it skips the
    //      whole block below during Deep Calm exactly like the plankton
    //      field does, not just fading the output to invisible while still
    //      paying for it.
    //   2. glowVMask - a per-pixel vertical gate, since this event lives low
    //      in the water column near/below the visible bottom edge. Tightened
    //      (measured, not assumed - see AUDIT.md) from an initial (-0.55,
    //      0.10) band, which left the FBM work running across roughly the
    //      bottom 60% of frame, to (-0.62, -0.14) - now only the bottom
    //      ~35-40% of frame pays the FBM cost at all, which is also a
    //      tighter match to "deep background, near the bottom edge" than
    //      the original wider band was. This is the spatial-masking check
    //      this pass's own brief calls for before adding real per-fragment
    //      cost across the full frame; a soft smoothstep gate (never a hard
    //      mask) so there is no visible
    //      seam where the field "turns on".
    float glowArc = pow(clamp(uBloom, 0.0, 1.0), 2.3);
    if (glowArc > 0.0004) {
        // Design correction (self-caught, isolated-render check - see
        // AUDIT.md): a first pass gated purely on pRefractWater.y, an
        // iso-line with zero horizontal variation - at high magnification
        // this read as flat, uniformly-curved horizontal strata (closer to
        // sedimentary layers than an irregular living field). Perturbing the
        // boundary itself with two cheap, incommensurate sine terms (no new
        // fbm2/transcendental-heavy cost - just 2 sin() calls, same
        // "irregular, not a perfect oscillation" technique already used by
        // rayAngle()'s sway) breaks the iso-line into an undulating,
        // never-repeating boundary before the noise field even begins.
        float vMaskWobble = 0.055 * sin(pRefractWater.x * 2.3 + uTime * 0.021)
                           + 0.032 * sin(pRefractWater.x * 5.1 - uTime * 0.014 + 1.7);
        float glowVMask = 1.0 - smoothstep(-0.62, -0.14, pRefractWater.y + vMaskWobble);
        if (glowVMask > 0.003) {
            // Internal churn very subtly quickens through the arc (never
            // enough to read as urgency, only enough to feel like "more is
            // happening") - part of what makes the escalation toward Bloom
            // Event a qualitative change (slow, barely-perceptible drift ->
            // a visibly living field), not just a brightness increase.
            float drift = 1.0 + 0.35 * glowArc;
            float shape = abyssalGlowShape(pRefractWater, drift);

            // Two soft bands rather than a hard threshold - a broad, dim
            // "ambient" presence plus irregular brighter cores within it, so
            // the field has internal contrast (reads as structured, alive)
            // without any single core reading as a body: the cores are
            // numerous, irregular, and continuously reshaping with the
            // noise field underneath them, never a fixed or countable
            // shape.
            //
            // Design correction #3 (self-caught, isolated-render check - see
            // AUDIT.md): the ambient term's own weight was strong enough
            // relative to the core term that the field's overall brightness
            // trend still tracked the (now-perturbed but still smooth)
            // vertical mask more than the noise field's own patchiness.
            // Lowered the core threshold (more of the field crosses into
            // "core" territory) and rebalanced the weights so the patchy,
            // irregular core term is what visually dominates - the ambient
            // term is now only a faint backdrop, not competing with it.
            float core      = smoothstep(0.30, 0.62, shape);
            float glowField = shape * 0.16 + core * 1.15;

            // Color stays on this scene's established deep-blue/cyan ramp,
            // with the same small violet nudge already used for jellyfish
            // rim/plankton at high bloom (never a hue flip) - this event
            // does not introduce a new color family to the scene. Magnitudes
            // retuned alongside the shape-renormalization fix above so the
            // brightest cores read as a real, if still dark-dominant,
            // presence at Bloom Event rather than an imperceptible tint.
            vec3 glowDeep      = vec3(0.055, 0.100, 0.190);
            vec3 glowBloom     = vec3(0.190, 0.320, 0.560);
            vec3 abyssalColor  = mix(glowDeep, glowBloom, core);
            // Phase 4 color variation: the violet nudge itself now gently
            // pulses (one extra, cheap sin call) rather than being a flat
            // ramp - "subtle magenta/indigo in bloom pulses" per the brief,
            // layered onto this layer's own pre-existing high-bloom
            // violet-nudge mechanism rather than a new one.
            float glowPulseVar = 0.85 + 0.15 * sin(uTime * 0.21 + 3.1);
            abyssalColor = mix(abyssalColor, vec3(0.38, 0.20, 0.62), smoothstep(0.80, 1.0, uBloom) * 0.30 * glowPulseVar);

            color += abyssalColor * glowField * glowArc * glowVMask;
        }
    }

    // --- God rays ----------------------------------------------------------
    // Brightness = time-only baseline (alive under silence) + uLightDrive
    // lift + uBloom lift, so rays never disappear entirely at rest. Sampled
    // against pRefractRaysCaustics (Phase 3 refraction + Phase 1 parallax,
    // rays/caustics' own 0.5x layer) - see refraction-warp/parallax comments
    // above.
    float rf = rayField(pRefractRaysCaustics);
    float rayBrightnessMul = 0.55 + 0.65 * uLightDrive + 0.45 * uBloom;
    vec3  rayColorDeep  = vec3(0.16, 0.48, 0.50);
    vec3  rayColorBloom = vec3(0.48, 0.90, 0.62);
    vec3  rayColor = mix(rayColorDeep, rayColorBloom, clamp(uBloom, 0.0, 1.0) * 0.55);
    // Phase 4 color variation: a very low-frequency horizontal hue drift
    // toward a cooler cyan-blue variant, layered onto the existing teal-green
    // ramp - "occasional green/blue caustic variation" extended lightly to
    // the rays themselves for cross-layer consistency. Cheap (one extra sin,
    // no new noise call).
    vec3  rayColorCool = vec3(0.20, 0.42, 0.62);
    float rayHueVar = 0.5 + 0.5 * sin(pRefractRaysCaustics.x * 1.3 + uTime * 0.05);
    rayColor = mix(rayColor, rayColorCool, rayHueVar * 0.25);
    color += rayColor * rf * rayBrightnessMul;

    // --- Caustic shimmer ----------------------------------------------------
    // Two independently scrolling ridged-noise layers, multiplied together,
    // masked to the upper third of frame AND modulated by the local
    // (cheap-envelope) ray intensity so it never reads as a full-frame
    // static-looking overlay. Phase 3 (AUDIT.md Entry 43): causticLocalRay's
    // exponent sharpens (was a linear clamp) so caustic energy concentrates
    // specifically inside ray interiors and fades faster in the water
    // between rays, rather than scaling uniformly with a flat upper-water
    // band mask; the clamp ceiling (1.6 -> 1.35) is tightened to compensate
    // so peak brightness at full ray strength stays close to the original
    // (1.35^1.6 =~ 1.62 vs the old flat 1.6), i.e. this redistributes where
    // the energy concentrates rather than adding new energy to the frame.
    // Noise UVs and the ray-strength sample both use pRefractRaysCaustics
    // (Phase 3 refraction + Phase 1 parallax) so caustics and the rays they
    // pool inside stay visually locked together under the same warp/
    // parallax layer rather than drifting apart.
    float causticTopMask = 1.0 - smoothstep(0.0, 0.34, depthTN);
    vec2  cUV1 = pRefractRaysCaustics * 6.0 + vec2(uTime * 0.06, -uTime * 0.03);
    vec2  cUV2 = pRefractRaysCaustics * 7.4 + vec2(-uTime * 0.045, uTime * 0.07);
    float cn1 = vnoise2(cUV1);
    float cn2 = vnoise2(cUV2);
    float ridge1 = pow(1.0 - abs(2.0 * cn1 - 1.0), 2.2);
    float ridge2 = pow(1.0 - abs(2.0 * cn2 - 1.0), 2.2);
    float caustic = ridge1 * ridge2;
    float rayStrength = rayEnvelope(pRefractRaysCaustics);
    float causticLocalRay = pow(clamp(rayStrength * 1.3, 0.0, 1.35), 1.6);
    float causticMask = causticTopMask * causticLocalRay;
    // Phase 4 color variation: subtle green/blue caustic hue drift, reusing
    // cn1 (already computed above for the ridge pattern - zero extra noise
    // cost) - "occasional green/blue caustic variation" per the brief,
    // tasteful rather than chaotic since it's the same noise already driving
    // the caustic shape itself, not an independent random hue.
    vec3  causticColorA = vec3(0.30, 0.68, 0.56); // green-teal (original)
    vec3  causticColorB = vec3(0.22, 0.56, 0.74); // cyan-blue
    vec3  causticColor  = mix(causticColorA, causticColorB, smoothstep(0.35, 0.65, cn1));
    color += causticColor * caustic * causticMask * (0.45 + 0.55 * uLightDrive + 0.40 * uBloom) * 0.85;

    // --- Bloom-arc bands (Phase 1 "Living Water") -----------------------------
    // uBloom (0-1, already established by Phase 1/2/3 as a single continuous
    // accumulator - see UnderwaterScene.cs) now drives named, documented
    // behavior bands across every layer in this scene coherently - the same
    // "continuous scalar with named tuning ranges" pattern already
    // established by Wind Turbine Fire's Calm/Ignition/Burn, not a separate
    // state machine or code path per band:
    //   Deep Calm                 (0.00-0.20): current low, plankton
    //     essentially absent, rays/caustics faint, jellyfish drift only.
    //   Bioluminescent Awakening  (0.20-0.45): plankton begins appearing
    //     sparse, glow intensity rising.
    //   Current Build             (0.45-0.70): current/drift rate rises
    //     noticeably, plankton density increases, ray/caustic energy rises.
    //   Bloom Event                (0.70-1.00): plankton at full density
    //     with visible streaming/pulsing character, peak caustic shimmer,
    //     richest overall energy - but still dark-dominant per this scene's
    //     established readability guard (final exposure/vignette below is
    //     unchanged by this pass - still only a modest 0.92-1.00 nudge).
    // Ray/caustic brightness already scale continuously with uBloom via the
    // rayBrightnessMul/causticColor multipliers above (Phase 1/3) - that
    // already satisfies "ray/caustic energy rises" through Current
    // Build/Bloom Event with no new code needed. currentDriveArc below is
    // this pass's one new piece: a small additive current-drive lift, layered
    // on top of (never replacing) uCurrentDrive's own audio-driven value,
    // that builds through Current Build and peaks at Bloom Event - used by
    // haze/marine-snow/plankton drift so "current visibly picks up" is a
    // real, shared, cross-layer effect rather than a single layer's trick.
    float bloomCurrentLift = smoothstep(0.45, 0.70, uBloom) * 0.18 + smoothstep(0.70, 1.0, uBloom) * 0.12;
    float currentDriveArc  = clamp(uCurrentDrive + bloomCurrentLift, 0.0, 1.4);

    // --- Haze/murk: FBM layers with domain-warped lateral current drift ----
    // Baseline drift never zero even at rest; denser/darker toward the
    // bottom. Built from pRefractHaze (Phase 3 refraction + Phase 1
    // parallax, haze's own 0.7x layer), composing with this layer's own
    // pre-existing hazeWarp domain-warp exactly as before - two independent
    // warps stacking harmlessly since both are gentle/low-amplitude.
    // driftBase uses currentDriveArc (Phase 1 "Living Water" bloom-arc
    // remapping, defined below with the other bloom-band terms just before
    // this layer needs it) rather than raw uCurrentDrive, so haze drift is
    // part of the same "current visibly picks up through Current Build"
    // mechanic as the plankton/marine-snow drift.
    vec2  hazeWarp = vec2(fbm2(pRefractHaze * 0.6 + vec2(0.0, uTime * 0.015), 2) * 0.35, 0.0);
    float driftBase = 0.015 + 0.09 * currentDriveArc;
    vec2  hazeUV1 = (pRefractHaze + hazeWarp) * 1.4 + vec2(uTime * driftBase, uTime * 0.006);
    vec2  hazeUV2 = (pRefractHaze + hazeWarp) * 2.3 + vec2(-uTime * driftBase * 0.7, -uTime * 0.009);
    float haze1 = fbm2(hazeUV1, 3);
    float haze2 = fbm2(hazeUV2, 2);
    float hazeCombined = clamp(haze1 * 0.6 + haze2 * 0.4, 0.0, 1.0);
    float hazeDensity = mix(0.10, 0.42, depthTN);
    vec3  hazeTint = vec3(0.010, 0.018, 0.032);
    // Phase 4 color variation: a small violet/indigo nudge in the haze
    // shadows at higher bloom - "hints of violet/purple in shadows" per the
    // brief, reusing this file's own established nudge rule (small
    // uBloom-gated mix, never a hue flip) rather than a new mechanism.
    hazeTint = mix(hazeTint, vec3(0.020, 0.014, 0.040), smoothstep(0.55, 1.0, uBloom) * 0.5);
    color = mix(color, hazeTint, hazeCombined * hazeDensity * 0.55);

    // --- Distant Alien Presence (Phase 4, repositioned by Phase 4 Addendum 1) --
    // User-review feedback: "I didn't see the monster presence at all" when
    // actually watching the running scene, directly contradicting Phase 4's
    // own isolated-render luminance verification (see AUDIT.md Entry 47 and
    // this file's earlier revision history). Root-caused via full-composite
    // (never isolated) screenshots at forced bloom/pulse states, including
    // the literal maximum-visibility state (uBloom=1 AND presencePulse=1
    // simultaneously, which barely ever coincides during real play since
    // presencePulse is deliberately independent of uBloom): the darkening
    // mix was composited immediately after the bare water-gradient canvas,
    // BEFORE the abyssal glow field / god rays / caustic shimmer / haze all
    // added their own (purely additive, `color +=` / a second `mix`) light
    // on top. Since a mix-toward-near-black only darkens whatever `color`
    // already holds at that point, darkening a bare, still-dim gradient and
    // then piling most of the scene's actual visible brightness on top of it
    // afterward left almost nothing of the darkening in the final pixel -
    // confirmed by direct full-composite screenshot inspection (not just
    // "the isolated numbers moved"), not merely theorized.
    //
    // Fix: this block is now composited HERE - after the water gradient, the
    // abyssal glow field, god rays, caustics, and haze have all contributed
    // their light, i.e. against the water column's actual near-final
    // backdrop brightness - so the same mix-toward-shadowTint technique now
    // visibly dims what a viewer actually sees instead of dimming a canvas
    // that was about to be redrawn over. Still composited BEFORE the
    // background/hero jellyfish and marine-snow/plankton particle layers
    // below, preserving the original "huge distant thing everything nearer
    // still naturally layers on top of and partially obscures" principle for
    // the near-field/creature/particulate layers specifically - only the
    // ambient atmosphere layers (which don't represent anything "in front of"
    // the presence, just the water column's own lit density) moved ahead of
    // it. No change to monsterCenter()/monsterShape() (the shape/placement
    // logic - already verified non-cartoonish via isolated-render crops in
    // Entry 47) or to the darkening-not-brightening technique itself - this
    // is a compositing-order fix, not a new visual mechanism. Sampled against
    // pRefractWater exactly as before (same most-distant 0.2x-parallax
    // coordinate).
    //
    // Two independent gates, per this file's established "gate cost, not
    // just visible output" pattern:
    //   1. presenceArc - rises with uBloom (its own curve, independently
    //      tuned from glowArc so the two layers don't necessarily peak
    //      together) - "starts extremely faint early... becomes more
    //      readable later."
    //   2. presencePulse - a slow, own-clock "appears every few seconds"
    //      breathing cycle, entirely independent of uBloom/audio, per the
    //      brief's explicit instruction that this should NOT just track
    //      bloom 1:1. Because presenceArc depends on accumulated bloom state
    //      and presencePulse depends only on raw uTime, the monster's own
    //      peak-visibility MOMENT (their product, maximized) does not in
    //      general coincide with uBloom's own peak - verified explicitly in
    //      this pass's performance/screenshot evidence (see AUDIT.md).
    // Phase 4 Addendum 1: arc widened from (0.05, 0.85) to (0.05, 0.60) - the
    // compositing-order fix above made the peak state genuinely visible, but
    // full-composite screenshots at bloom=0.3 (Bioluminescent Awakening/
    // early Current Build - meant to be "occasionally sensed... starting
    // fairly early" per the original brief) still showed it as imperceptible,
    // since the old curve didn't reach a meaningful envelope contribution
    // until well past the midpoint of the bloom arc. Reaching full arc
    // contribution by bloom=0.60 instead of 0.85 (still gated by presenceArc
    // near-zero at very low bloom, and still gated by the independent
    // presencePulse window on top) moves "occasionally sensed" earlier into
    // the song without changing peak behavior at Bloom Event.
    float presenceArc      = smoothstep(0.05, 0.60, uBloom);
    float presencePulseRaw = 0.5 + 0.5 * (0.62 * sin(uTime * 0.46 + 1.7) + 0.38 * sin(uTime * 0.19 - 0.6));
    // Narrow "appearance window" rather than a constant slow breathing glow -
    // most of each cycle presencePulse sits near zero; it rises to visible
    // only for a portion of the cycle, then recedes - this IS the "appears
    // every few seconds" read, not a mask hiding something otherwise
    // permanently on-screen.
    float presencePulse    = smoothstep(0.40, 0.84, presencePulseRaw);
    float presenceEnvelope = presenceArc * presencePulse;

    if (presenceEnvelope > 0.004) {
        vec2 mCenter  = monsterCenter(uTime);
        vec2 toCenter = pRefractWater - mCenter;
        // Cheap AABB early-out before the expensive fbm2 work below. Per the
        // perf note in the "Distant Alien Presence" section above, this
        // layer's own necessarily large footprint means this bound provides
        // real but modest pruning (unlike the glow field's tight bottom-band
        // mask) - kept anyway since it costs nothing and is never harmful.
        if (abs(toCenter.x) < MONSTER_SCALE_X * 2.5 && abs(toCenter.y) < MONSTER_SCALE_Y * 2.8) {
            float presenceMask = monsterShape(pRefractWater, mCenter, uTime);

            // Pure darkening, no brightening - see the section header above
            // for why. Tint leans a shade bluer/violet than the darkest
            // water zone (zoneBottom) itself - an "alien" cue without ever
            // becoming a saturated color.
            vec3 shadowTint = vec3(0.003, 0.006, 0.017);
            color = mix(color, shadowTint, presenceMask * presenceEnvelope * 0.88);
        }
    }

    // --- Background jellyfish (Phase 4 "Presence / Color / Depth Population") -
    // Several (profile-scaled) small, faint, reduced-detail organisms behind
    // the 3 hero jellyfish - see renderBackgroundJelly() above for why this
    // is a deliberately separate, much cheaper technique. Own parallax depth
    // tier (0.82x - between haze's 0.7x and the near/hero-jellyfish layer's
    // 1.0x, per the brief's "parallax should differ by depth" requirement),
    // never refraction-warped (the same exclusion rule Phase 3 established
    // for the hero jellyfish - background organisms follow the identical
    // convention). Composited here, before the hero jellyfish below, so they
    // sit visually farther back - matching physical depth order.
    vec2 pBg = p - uCameraOffset * 0.82;
    for (int bi = 0; bi < MAX_BG_JELLY; bi++) {
        if (bi >= uBgJellyCount) break;
        renderBackgroundJelly(color, pBg, float(bi), hazeCombined, hazeDensity);
    }

    // --- Jellyfish (Phase 2, drift path added Phase 1 "Living Water") --------
    // Inserted here, after background/haze but before the nearer marine-snow
    // particulate tier, matching physical depth order - jellyfish are
    // embedded in the water column behind the particulate matter drifting
    // in front of everything. 3 drifting forms, each with its own
    // C#-integrated pulse phase AND (new) drift position/depth (see
    // UnderwaterScene.cs's JellyDriftState). Rendered against pNear - full
    // camera parallax (the "near" reference layer, per the parallax comment
    // above) but never refraction-warped, per Phase 3's own exclusion rule.
    renderJelly(color, pNear, 0.0, uJellyPhase0, hazeCombined, hazeDensity, vec2(uJellyPos0X, uJellyPos0Y), uJellyDepth0);
    renderJelly(color, pNear, 1.0, uJellyPhase1, hazeCombined, hazeDensity, vec2(uJellyPos1X, uJellyPos1Y), uJellyDepth1);
    renderJelly(color, pNear, 2.0, uJellyPhase2, hazeCombined, hazeDensity, vec2(uJellyPos2X, uJellyPos2Y), uJellyDepth2);

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
        // driftX uses currentDriveArc (Phase 1 "Living Water" bloom-arc
        // remapping, defined above) rather than raw uCurrentDrive, so marine
        // snow drift is part of the same "current visibly picks up through
        // Current Build" cross-layer mechanic as haze/plankton.
        float driftX = (0.05 + 0.35 * currentDriveArc) * cyc; // baseline drift never zero

        // 3 incommensurate sine perturbations - never a straight line.
        float turb = 0.03  * sin(t * 2.7 + seed)
                   + 0.018 * sin(t * 6.1 - seed * 1.3)
                   + 0.010 * sin(t * 1.3 + seed * 2.9);

        vec2 particlePos = vec2(baseX + driftX + turb * (0.5 + uCurrentTurbulence), y);
        // Phase 1 "Living Water": marine snow is a "near" reference-depth
        // layer (full 1.0x camera parallax, see the parallax comment above)
        // - distance measured against pNear, not raw p.
        float dist = length(pNear - particlePos);

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

    // --- Bioluminescent plankton bloom (Phase 1 "Living Water") --------------
    // A small, cheap point/glow field, distinct from the marine-snow
    // particle system above (smaller, dimmer, more numerous - bioluminescent
    // plankton, not suspended debris) whose visible density/brightness IS the
    // scene's evolution mechanic named by the bloom-arc bands above, not
    // decoration layered on top of an already-evolving scene. Supersedes
    // Phase 1 (original atmosphere pass)'s "Bioluminescent motes" stub, which
    // was explicitly documented as foreshadowing-only placeholder with "no
    // creature shapes" - this is that placeholder's real implementation,
    // built to the bloom-arc spec instead of a single flat on/off gate.
    //
    // Applies every Entry-33 lesson already proven on this file's own
    // marine-snow layer: squared-hash size skew toward small, pow(hash,~2.6)
    // brightness skew so most are dim, current-coupled drift (currentDriveArc
    // - the same bloom-arc-boosted current vector as haze/marine-snow, for
    // motion coherence across layers), two implicit depth tiers via one
    // correlated hash driving size/speed/brightness together.
    //
    // Per-plankton appearance is staggered across the whole bloom arc (not a
    // single global on/off) via apThresh - each plankton "switches on" at
    // its own hashed point along bloomNorm, so the field visibly *builds*
    // through Awakening/Current Build rather than snapping on as a block;
    // brightness itself also keeps rising with bloomNorm (mix(0.35,1.0,...))
    // so Bloom Event reads as richer, not just more numerous.
    //
    // Entirely wrapped in an early bloomNorm gate so the loop's *cost*, not
    // just its visible output, is skipped during Deep Calm - this is what
    // keeps this layer "essentially absent" in that band cheap as well as
    // dark, and keeps the added per-frame cost budget-conscious overall
    // (point/glow math only, no SDF curve evaluation like the tentacles).
    //
    // Phase 2 "Bloom refinement" (this pass): the field above already had
    // density/brightness/staggered-appearance structure but read as
    // independent per-mote wander and per-mote blinking - "particles
    // twinkling randomly" rather than a current-borne bloom. This pass adds
    // two real structural mechanics, both using planktonChannelAngle()/
    // planktonPulseWave() defined just above main():
    //   1. Flow-field alignment - each plankton's baseX places it in one of
    //      a small number of shared "current channels" (smoothly blended,
    //      never a hard seam), each channel drifting in its own slowly-
    //      evolving direction, so plankton with nearby baseX visibly move
    //      together rather than independently - see flowBendX below.
    //   2. Pulse trains - a traveling brightness wave sampled at each
    //      plankton's own position (planktonPulseWave), so waves of
    //      brightness sweep continuously through the whole field over time
    //      instead of each mote blinking on its own random schedule.
    // Both mechanics' strength/frequency rise with bloomNorm - Awakening
    // shows only a faint, tentative channel bend and a slow, sparse pulse
    // train; Bloom Event shows strongly aligned streaming and a quick,
    // tight pulse train - a qualitative escalation across the arc, not just
    // more/brighter dots. Brightness variance (brightVarMul below) also widens
    // through the arc for the same reason - Awakening's population reads as
    // uniformly dim, Bloom Event's reads as a real mix of dim and bright.
    float bloomNorm = clamp((uBloom - 0.20) / 0.80, 0.0, 1.0); // 0 at Deep Calm's end, 1 at full Bloom Event
    if (bloomNorm > 0.001) {
        // Computed once per pixel, not once per plankton - see the perf
        // note on computePlanktonChannelBoundaryAngles() above.
        float chAngle[PLANKTON_CHANNEL_BOUNDARIES];
        computePlanktonChannelBoundaryAngles(chAngle);

        // Perf fix (plankton spatial early-out, AUDIT.md Entry 45 addendum -
        // see that entry for the isolation evidence): disabling this entire
        // loop at forced full Bloom Event recovered ~75fps on High, matching
        // Deep Calm rest-state almost exactly - proving this loop, not
        // anything else in the scene, is responsible for the ~50fps
        // worst-case shortfall below the 60fps floor. The loop had no
        // spatial early-out at all: every fragment ran the full per-plankton
        // cost (flow-channel lookup, wobble, pulse-train, color mix) for
        // every one of uPlanktonCount plankton regardless of proximity, even
        // though the actual visible radius (sizePx below) is only
        // ~0.001-0.003 p-units - the overwhelming majority of (fragment,
        // plankton) pairs contribute exactly zero, and that work was 100%
        // wasted. Same class of fix already proven on this file's own
        // tentacle loop (Entry 41 addendum 6: 48.6fps -> 74.9fps from an
        // analogous per-element reach check).
        //
        // flowWeight/turbMul below are the same closed-form expressions the
        // per-plankton math already used, but neither actually depends on
        // anything per-plankton (both are pure functions of this frame's
        // uniforms - bloomNorm, currentDriveArc, uCurrentTurbulence) so they
        // were being silently recomputed identically for every plankton;
        // hoisting them here removes that redundancy AND doubles as the
        // exact (not guessed) bound needed for the reach check just below:
        //   flowBendX = flowDirX * flowWeight * cyc, with |flowDirX| <= 1
        //     (cos) and cyc in [0,1] (fract) - so |flowBendX| <= flowWeight
        //     exactly, for every plankton, every frame.
        //   turb = 0.014*sin(..) + 0.008*sin(..), so |turb| <= 0.022 exactly
        //     (sum of the two amplitudes) - multiplied by turbMul.
        // A fragment farther than sizePx + planktonReachPad from a
        // plankton's cheap "coarse" position (baseX+driftX, no
        // transcendental calls) can therefore never receive a nonzero
        // contribution from it, so skipping straight to `continue` there
        // changes zero pixels of visible output - it is a tight analytic
        // bound derived from the existing motion formulas, not a headroom
        // guess.
        float flowWeight        = mix(0.35, 1.0, bloomNorm) * (0.05 + 0.22 * currentDriveArc);
        float turbMul           = 0.5 + uCurrentTurbulence;
        float planktonReachPad  = flowWeight + 0.022 * turbMul;
        // sizePx's own max possible value (mix(0.0010,0.0030,x<=1)*mix(0.7,1.2,x<=1),
        // both factors capped at their own upper bound) - a fixed compile-time
        // bound, used only for the coarse reach check below so the real
        // per-plankton sizeHash hash1() call (see below) can be deferred past
        // that check instead of paid by every plankton unconditionally; still
        // exact/conservative, not a guess.
        const float PLANKTON_SIZEPX_MAX = 0.0030 * 1.2;
        float planktonReachMax  = PLANKTON_SIZEPX_MAX + planktonReachPad;

        for (int i = 0; i < MAX_PLANKTON; i++) {
            if (i >= uPlanktonCount) break;

            float fi   = float(i);
            float seed = fi * 23.71 + 11.0;

            float apThresh = hash1(seed + 15.0) * 0.85; // spreads turn-on across most of the arc, a few always-early ones
            float apGate   = smoothstep(apThresh, apThresh + 0.12, bloomNorm);
            if (apGate < 0.003) continue;

            float tier = hash1(seed + 9.0);

            // Perf (prefix cost reduction, AUDIT.md Entry 45 addendum): after
            // the spatial early-out above was added, this loop's remaining
            // cost is dominated by the handful of hash1() calls every
            // plankton pays unconditionally just to know its own coarse
            // position (needed for the reach check itself) - unlike the
            // flow/pulse/color tail, this prefix can't be skipped by a
            // position check since it's what PRODUCES the position. Two
            // small, disclosed simplifications here reduce that prefix from
            // 6 hash1() calls to 4:
            //  1. sizeHash (used only for sizePx, not position) is deferred
            //     until after the reach check below, using the fixed
            //     PLANKTON_SIZEPX_MAX bound in the check itself instead -
            //     saves one hash1() call for every culled plankton.
            //  2. lifeSpeed's small jitter and t's phase offset - both minor
            //     lifecycle-timing variance terms, not position/color/size
            //     identity - are now derived from a single hash1() call
            //     instead of two independent ones (second sub-value via
            //     fract(h*K), a standard single-hash multi-output trick).
            //     This introduces a mild deterministic correlation between a
            //     plankton's fall-speed jitter and its cycle phase offset
            //     that did not exist before; judged visually negligible
            //     (verified below) since both are minor per-plankton timing
            //     variance, not a structural/positional/color attribute
            //     (the kind of correlation Entry 33 warns against).
            float hLife       = hash1(seed + 2.0);
            float lifeSpeed   = mix(0.010, 0.030, tier) * (0.9 + 0.2 * hLife);
            float tPhaseFrac  = fract(hLife * 71.317); // derived second sub-value, decorrelated in practice
            float t   = uTime * lifeSpeed + tPhaseFrac * 20.0;
            float cyc = fract(t);

            float topY = 0.55, bottomY = -0.55;
            float y = mix(topY, bottomY, cyc);

            float baseX = mix(-0.9, 0.9, hash1(seed + 4.0));
            // Streaming character intensifies specifically through Bloom
            // Event, per the named band above.
            float streamBoost = 1.0 + 1.4 * smoothstep(0.70, 1.0, uBloom);
            float driftX = (0.04 + 0.30 * currentDriveArc) * cyc * streamBoost;

            // Spatial early-out - see planktonReachPad's derivation above.
            // coarsePos uses only terms already computed (no new
            // transcendental calls beyond the hash1s paid for above) and is
            // guaranteed within planktonReachMax (using the fixed sizePx
            // upper bound - the real per-plankton value is computed only
            // below, for survivors) of this plankton's true final position.
            // Skips sizeHash, the flow-channel lookup, wobble, pulse-train,
            // and color math entirely for fragments provably out of reach -
            // this is the fix's whole effect.
            vec2  coarsePos  = vec2(baseX + driftX, y);
            vec2  coarseDiff = pNear - coarsePos;
            if (dot(coarseDiff, coarseDiff) > planktonReachMax * planktonReachMax) continue;

            float sizeHash = hash1(seed + 1.0);
            sizeHash *= sizeHash; // squared skew toward small
            float sizePx = mix(0.0010, 0.0030, sizeHash) * mix(0.7, 1.2, tier);

            // Phase 2 "Bloom refinement": flow-field alignment. Sample this
            // plankton's shared current-channel direction (from baseX, see
            // planktonChannelAngle() above) and bend its path toward it,
            // growing progressively over its fall (cyc, the same lifecycle
            // parameter driftX already uses) so the bend reads as a
            // continuous curved streamline rather than an instant offset.
            // flowWeight (hoisted above) rises with bloomNorm and with the
            // ambient current (currentDriveArc) - negligible/tentative in
            // Bioluminescent Awakening, strongly aligned by Bloom Event.
            // Only cos(flowAngle) (the x-bend) is actually used below - the
            // y-component was computed in an earlier draft and dropped
            // (this pass keeps the fall cadence untouched, x-only bend),
            // so sin(flowAngle) is never evaluated - one fewer transcendental
            // call per plankton.
            float flowAngle  = planktonChannelAngle(baseX, chAngle);
            float flowDirX   = cos(flowAngle);
            float flowBendX  = flowDirX * flowWeight * cyc;

            // Residual independent wobble - reduced from Phase 1's 3-term/
            // full amplitude (was the dominant motion term; now a minor
            // organic touch layered on top of the channel-aligned
            // streamline above, not the primary driver of lateral motion).
            // Perf: also trimmed from 3 sine terms to 2 (one fewer
            // transcendental call per plankton) - at this reduced
            // amplitude the third term's contribution was not visually
            // distinguishable from the other two.
            float turb = 0.014 * sin(t * 3.1 + seed)
                       + 0.008 * sin(t * 6.7 - seed * 1.3);

            vec2 planktonPos = vec2(baseX + driftX + flowBendX + turb * turbMul, y);
            // Same "near" reference-depth layer as marine snow/jellyfish
            // (full 1.0x camera parallax, see the parallax comment above).
            float dist = length(pNear - planktonPos);

            // Perf: unchanged constant-exponent pow from Phase 1 (was
            // briefly made variable-exponent - mix(3.2,2.2,bloomNorm) - to
            // widen brightness variance through the arc, but that measured
            // as added cost for a purely cosmetic effect; reuses the
            // already-computed `tier` hash below instead, at zero extra
            // hash1/pow cost, for the same "richer mix, not just more of
            // the same" widening.
            float bMax = pow(hash1(seed + 5.0), 2.6);
            // Reuses `tier` (already computed above, no new hash1 call) to
            // widen the bright/dim spread as bloomNorm rises - Awakening's
            // population reads as uniformly dim, Bloom Event's as a real
            // mix of dim and bright, without any added per-plankton cost.
            float brightVarMul = mix(1.0, mix(0.45, 1.85, tier), bloomNorm);

            // Phase 2 "Bloom refinement": traveling pulse-train (see
            // planktonPulseWave() above) replaces the old per-plankton
            // independent blink - present at low weight from Awakening
            // onward (slow, sparse) and dominant by Bloom Event (quick,
            // tight), so "waves of brightness travel through the field"
            // rather than motes twinkling on independent random schedules.
            float pulseTrainAmt = mix(0.10, 0.65, bloomNorm);
            float pulseWave     = planktonPulseWave(planktonPos, bloomNorm);
            float pulse         = mix(1.0, pulseWave, pulseTrainAmt);

            float brightness = bMax * mix(0.5, 1.0, tier) * apGate * pulse * brightVarMul
                              * mix(0.35, 1.0, bloomNorm); // glow intensity itself rises through the arc, not just count/density

            vec3 planktonColor = mix(vec3(0.40, 0.90, 0.72), vec3(0.68, 0.95, 0.80), tier);
            // Violet nudge only at very high bloom - reuses the same rule
            // established for rim glow/marine-snow-adjacent layers - a
            // nudge, never a hue flip.
            planktonColor = mix(planktonColor, vec3(0.55, 0.35, 0.75), smoothstep(0.85, 1.0, uBloom) * 0.12);
            // Phase 4 color variation: a rare, subtle warm bioluminescent
            // spark variant (reuses hLife, already computed above for
            // lifecycle timing - no new hash1() call, zero added cost) -
            // "warmer bioluminescent sparks only if subtle" per the brief;
            // gated narrow (>0.93) so it stays a rare accent, not a
            // competing color family.
            planktonColor = mix(planktonColor, vec3(0.85, 0.62, 0.35), smoothstep(0.93, 0.99, hLife) * 0.35);

            color += planktonColor * brightness * smoothstep(sizePx, 0.0, dist) * 1.6;
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
