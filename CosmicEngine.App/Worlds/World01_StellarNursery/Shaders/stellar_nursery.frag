#version 330 core
in vec2 vUV;
out vec4 fragColor;

// STELLAR NURSERY - Volumetric Raymarcher
//
// True volumetric rendering: Beer-Lambert transmittance + emission.
// 3D value noise evaluated at each march step.
// Henyey-Greenstein phase function for forward scattering.
// Correct perspective rays from 3D camera basis vectors.
//
// Coordinate system: light-years (ly)
// Camera: ~200 ly from origin, looking forward through the nebula.
// Nebula: fills 3D space with multi-octave fractal density field.
//
// Guitar 1 (Creator): raises temperature, boosts emission intensity
// Guitar 2 (Sculptor): increases density / compression, shapes structure

// -------------------------------------------------------
// UNIFORMS
// -------------------------------------------------------

uniform float uTime;
uniform float uBass1;
uniform float uMid1;
uniform float uTreble1;
uniform float uLevel1;
uniform float uBass2;
uniform float uMid2;
uniform float uTreble2;
uniform float uLevel2;
uniform float uBassCombined;
uniform float uDimLevel;
uniform float uBassBrightness;
uniform float uSeed;

// Diagnostic-only (Visual Recovery Pass 1): 0 in all normal rendering. 1 shows
// raymarch opacity (1-transmittance) in isolation; 2 shows raw accumulated
// radiance before background/stars/audio-envelope/gamma. Lets --diagnostic
// visual prove density/color structure independently of final compositing.
uniform int uDebugMode;

// 3D camera (in light-years)
uniform vec3 uCamPos;
uniform vec3 uCamForward;
uniform vec3 uCamRight;
uniform vec3 uCamUp;

// Legacy (still sent by StellarNursery.cs, unused here)
uniform float uZoom;
uniform float uDriftX;
uniform float uDriftY;
uniform float uPerfTime;
uniform float uCumEnergy1;
uniform float uCumEnergy2;

// -------------------------------------------------------
// 3D HASH + NOISE
// Fast integer-like hash - no sin(), no trig.
// Uses float multiply tricks for statistical independence.
// -------------------------------------------------------

float hash3(vec3 p) {
    p = fract(p * vec3(443.897, 441.423, 437.195) + uSeed * 0.017);
    p += dot(p, p.yzx + 19.19);
    return fract((p.x + p.y) * p.z);
}

// 3D value noise: trilinear interpolation over 8 lattice corners.
// This is the fundamental primitive everything builds on.
float noise3D(vec3 p) {
    vec3 i = floor(p);
    vec3 f = fract(p);
    // Smooth cubic interpolation (removes grid artifacts)
    vec3 u = f * f * (3.0 - 2.0 * f);

    float n000 = hash3(i + vec3(0.0, 0.0, 0.0));
    float n100 = hash3(i + vec3(1.0, 0.0, 0.0));
    float n010 = hash3(i + vec3(0.0, 1.0, 0.0));
    float n110 = hash3(i + vec3(1.0, 1.0, 0.0));
    float n001 = hash3(i + vec3(0.0, 0.0, 1.0));
    float n101 = hash3(i + vec3(1.0, 0.0, 1.0));
    float n011 = hash3(i + vec3(0.0, 1.0, 1.0));
    float n111 = hash3(i + vec3(1.0, 1.0, 1.0));

    return mix(mix(mix(n000, n100, u.x),
                   mix(n010, n110, u.x), u.y),
               mix(mix(n001, n101, u.x),
                   mix(n011, n111, u.x), u.y), u.z);
}

// 3D FBM: 4 octaves with specified frequencies.
// Octave 0: 0.001 ly^-1 -> structure at ~1000 ly scale
// Octave 1: 0.003 ly^-1 -> structure at ~333 ly scale
// Octave 2: 0.009 ly^-1 -> structure at ~111 ly scale
// Octave 3: 0.027 ly^-1 -> structure at ~37 ly scale (Visual Detail Pass 1:
//   fine wisp/knot texture - see nebulaDensity, which also reuses this
//   octave's raw value as a dust-lane erosion mask instead of sampling a
//   separate noise field, to keep the extra cost to one octave).
// Returns value in approximately [0, 1]. `fineOctave` outputs the raw
// (un-normalized, [0,1]) octave-3 sample for reuse by the caller.
float fbm3D(vec3 p, float t, out float fineOctave) {
    float v    = 0.0;
    float freq = 0.001;
    float amp  = 1.000;

    for (int i = 0; i < 4; i++) {
        // Time evolution per octave (higher octaves evolve faster). Stellar
        // Nursery Showability Audit: the original 0.0004 coefficient produced a
        // per-octave noise-space offset of ~0.006-0.024 over a full 15s viewing
        // window - far below one noise lattice cell (~1.0), i.e. imperceptible
        // (measured: max per-pixel frame difference of 2-10/255 over 15s at the
        // fixed static camera). Raised 25x so the same 15s window produces a
        // ~0.15-0.6 noise-space shift - a visually apparent drift in the density
        // field - while keeping the per-frame delta small enough (~0.0007/frame
        // at 60fps) to read as smooth motion, not jitter. Fixed camera position,
        // color mapping, thresholds, and star code are all untouched.
        float tScale = float(i + 1) * 0.0004 * 25.0;
        float n = noise3D(p * freq + vec3(t * tScale, float(i) * 7.3, 0.0));
        v += amp * n;
        if (i == 3) fineOctave = n;
        freq *= 3.0; // 0.001 -> 0.003 -> 0.009 -> 0.027
        amp  *= 0.5; // 1.0   -> 0.5   -> 0.25  -> 0.125
    }
    // Normalize: sum of amps = 1 + 0.5 + 0.25 + 0.125 = 1.875
    return v / 1.875;
}

// -------------------------------------------------------
// NEBULA DENSITY FIELD
//
// Samples 3D FBM with Guitar 2 modulation.
// g2bass compresses density (denser peaks, emptier voids).
// g2mid shifts the density threshold slightly.
// Returns density in [0, 1].
// -------------------------------------------------------

float nebulaDensity(vec3 pos, float g2bass, float g2mid, out float fineDetail) {
    float raw = fbm3D(pos, uTime, fineDetail);

    // Dust lane erosion (Visual Detail Pass 1, softened in Revision 1): reuse
    // the fine (octave-3) sample as a mask that locally thins the density
    // where it's high, cutting dark lanes/gaps through the nebula instead of
    // leaving it a single soft blob. Revision 1: the original 0.62-0.82 mask
    // combined with a 65% erosion strength read as hard "punched out" holes
    // with visible edges rather than soft dust lanes (ChatGPT review
    // rejection). Broadened to 0.50-0.90 (softer transition in) and eased the
    // erosion strength to 30% (was 65%) so lanes darken gradually instead of
    // cutting a sharp-edged void.
    float dustMask = smoothstep(0.50, 0.90, fineDetail);
    raw *= mix(1.0, 0.70, dustMask);

    // Base threshold (Visual Recovery Pass 1: lowered from 0.42 to 0.38). The
    // camera is fixed and the dominant fbm octave varies on a ~1000 ly scale -
    // far larger than the 400 ly march range - so a given uSeed effectively
    // picks one large-scale sample for the whole visible frame. At 0.42, many
    // seeds landed below threshold almost everywhere (flat/near-black frame).
    // (0.30 was tried and overcorrected: combined with the old extinction
    // coefficient it made density cross threshold almost everywhere, which
    // saturated transmittance to zero within the first couple of march steps -
    // still visually flat, just a uniform fog-colored wall instead of a
    // uniform dark wash. See the extinction-coefficient comment below for the
    // other half of that fix.)
    // Guitar 2 bass: compression shifts distribution toward extremes
    float thresh = 0.38 - g2bass * 0.10 - g2mid * 0.04;
    float d = (raw - thresh) / (1.0 - thresh);
    return clamp(d, 0.0, 1.0);
}

// -------------------------------------------------------
// BLACKBODY COLOR (spec table: 3000K - 30000K)
// -------------------------------------------------------

vec3 blackbodyColor(float T) {
    if (T < 4000.0) {
        return mix(vec3(1.00, 0.44, 0.25), vec3(1.00, 0.64, 0.37),
                   smoothstep(3000.0, 4000.0, T));
    } else if (T < 6000.0) {
        return mix(vec3(1.00, 0.64, 0.37), vec3(1.00, 0.96, 0.88),
                   smoothstep(4000.0, 6000.0, T));
    } else if (T < 10000.0) {
        return mix(vec3(1.00, 0.96, 0.88), vec3(0.72, 0.85, 1.00),
                   smoothstep(6000.0, 10000.0, T));
    } else {
        return mix(vec3(0.72, 0.85, 1.00), vec3(0.40, 0.55, 1.00),
                   smoothstep(10000.0, 25000.0, T));
    }
}

// -------------------------------------------------------
// NEBULA EMISSION COLOR
//
// Cold/thin gas:  deep indigo / violet (unlit, absorptive)
// Medium density: teal / cyan  (OIII-like emission)
// Hot / Guitar 1: amber / gold / white (temperature-driven)
// Dust:           warm brown tint at mid density
// -------------------------------------------------------

vec3 nebulaEmission(float d, float T_K) {
    // Unlit cold gas: deep indigo to violet
    vec3 cold  = mix(vec3(0.04, 0.01, 0.12), vec3(0.14, 0.03, 0.30), d);
    // Ionized gas: teal-cyan (like real OIII emission)
    vec3 lit   = mix(vec3(0.04, 0.24, 0.38), vec3(0.18, 0.55, 0.65), d);
    // High temperature / Guitar 1: blackbody spectrum
    vec3 hotC  = blackbodyColor(T_K);

    vec3 col = mix(cold, lit, smoothstep(0.15, 0.60, d));
    // Temperature drives how much hot color bleeds in
    col = mix(col, hotC * 1.6, smoothstep(5000.0, 20000.0, T_K) * d * 0.80);

    // Warm dust tint at medium density (brown/orange-ish)
    float dust = smoothstep(0.18, 0.50, d) * smoothstep(0.80, 0.45, d);
    col = mix(col, col * vec3(1.28, 1.08, 0.72), dust * 0.38);

    return col;
}

// -------------------------------------------------------
// HENYEY-GREENSTEIN PHASE FUNCTION
//
// Describes how gas scatters light at angle cosTheta.
// g=0: isotropic. g=0.6: forward scattering (real gas is forward-biased).
// Used to add a "sun" scattering contribution.
// -------------------------------------------------------

float henyeyGreenstein(float cosTheta, float g) {
    float g2  = g * g;
    float denom = 1.0 + g2 - 2.0 * g * cosTheta;
    return (1.0 - g2) / (4.0 * 3.14159 * pow(max(denom, 0.0001), 1.5));
}

// -------------------------------------------------------
// POINT STARS
//
// A star is a small bounded radial dot in ray-direction space, not a
// filled hash cell: contribution falls to zero (smoothstep) outside
// `radius`, so at most a small circle near the cell center is ever lit,
// never the whole cell.
// -------------------------------------------------------

float pointStarLayer(vec3 rayDir, float cellFreq, float density, float radius, vec3 offset) {
    vec3 p     = rayDir * cellFreq + offset;
    vec3 cell  = floor(p);
    vec3 local = fract(p);

    float h = hash3(cell);
    if (h < 1.0 - density) {
        return 0.0;
    }

    // Keep the star center away from cell edges so it isn't clipped.
    vec3 jitter = vec3(
        hash3(cell + vec3(11.1, 23.7, 5.3)),
        hash3(cell + vec3(4.9, 31.2, 17.8)),
        hash3(cell + vec3(19.4, 8.2, 41.6))
    );
    vec3 center = mix(vec3(0.35), vec3(0.65), jitter);

    float d = length(local - center);

    float core = smoothstep(radius, 0.0, d);
    core *= core;

    return core * (0.5 + 0.5 * h);
}

// -------------------------------------------------------
// MAIN - VOLUMETRIC RAYMARCHER
//
// For each pixel:
//   1. Construct perspective ray from 3D camera basis
//   2. March from near (1 ly) to far (400 ly), 16 steps
//   3. At each step: sample density, accumulate color + transmittance
//      using Beer-Lambert: T *= exp(-sigma * dt)
//                          L += T * emission * (1 - exp(-sigma*dt))
//   4. Composite remaining transmittance over deep space background
// -------------------------------------------------------

void main() {
    vec2 uv = vUV;
    uv.y = 1.0 - uv.y;

    // NDC coordinates: [-1,1] x [-1,1], not yet aspect-corrected
    vec2 ndc = uv * 2.0 - 1.0;

    // Perspective ray construction.
    // FOV = 55 degrees. tan(27.5 deg) = 0.5206.
    // Aspect ratio 16:9 stretches X by 1.777.
    float tanHalfFov = 0.5206;
    float aspect     = 1.7778;

    vec3 rayDir = normalize(
        uCamForward
        + ndc.x * aspect * tanHalfFov * uCamRight
        + ndc.y * tanHalfFov * uCamUp
    );
    vec3 rayPos = uCamPos;

    // March parameters
    float tNear = 1.0;    // 1 ly minimum
    float tFar  = 400.0;  // 400 ly maximum
    int   STEPS = 16;
    float dt    = (tFar - tNear) / float(STEPS);

    // Accumulation state
    float transmittance = 1.0;
    vec3  radiance      = vec3(0.0);

    // Scattering: a single ambient "sun" at fixed direction
    // Guitar 1 treble increases this scattering contribution
    vec3  sunDir  = normalize(vec3(0.3, 0.7, 0.2));
    float sunPhase = henyeyGreenstein(dot(rayDir, sunDir), 0.60);
    vec3  sunColor = vec3(0.90, 0.82, 0.70) * (0.4 + uTreble1 * 0.8);

    // Audio parameters baked out of loop for performance
    float g1bass  = uBass1;
    float g1level = uLevel1;
    float g2bass  = uBass2;
    float g2mid   = uMid2;

    // Composition offset (Visual Detail Pass 1): shifts which part of the
    // seed-random density field the fixed camera samples, without touching
    // uCamPos/uCamForward/uCamRight/uCamUp (the accepted camera-uniform fix).
    // Stellar Nursery Showability Revision: ChatGPT/user review of the prior
    // audit's fix found structure sitting mostly at the frame edges/corners
    // with an empty center. Root cause: the old offset (-55,30,0) is tiny
    // relative to the dominant density octave's ~1000 ly scale, so it barely
    // relocated which part of the field the frustum samples - center-frame
    // structure was pure luck per seed. Empirically searched much larger
    // offsets (hundreds of ly, comparable to the octave-0 scale) against the
    // full seed pool; (0, -400, 0) combined with a re-curated seed pool (see
    // StellarNursery.cs KnownGoodSeeds) reliably fills the central 60% of the
    // frame, confirmed both visually and via center-region luminance metrics
    // (see AUDIT.md). This is a coordinate shift only - no threshold, color,
    // or star-code change.
    vec3 compositionOffset = vec3(0.0, -400.0, 0.0);

    for (int i = 0; i < STEPS; i++) {
        if (transmittance < 0.005) { break; }

        // Step center position in world space (ly)
        float t   = tNear + (float(i) + 0.5) * dt;
        vec3  pos = rayPos + rayDir * t + compositionOffset;

        // Sample density field at this world position
        float fineDetail;
        float d = nebulaDensity(pos, g2bass, g2mid, fineDetail);

        if (d > 0.002) {
            // Beer-Lambert extinction coefficient (Visual Recovery Pass 1: base
            // lowered from 0.80 to 0.35). At 0.80, a single 24.9 ly march step
            // through moderate density was already ~90%+ opaque, so transmittance
            // collapsed to ~0 within the first 1-2 steps for most rays - every
            // pixel then showed the same near-camera density (rays haven't
            // diverged much that close in), reading as a flat wall of color
            // instead of a nebula. 0.35 lets opacity build up gradually across
            // more of the 16-step march, so the ray survives long enough to
            // reach depths where per-pixel rays have diverged (revealing the
            // higher-frequency octaves as visible screen-space structure) and
            // gaps still let stars/background show through in places.
            // Guitar 2 bass increases extinction (denser gas absorbs more)
            float sigma = d * (0.35 + g2bass * 0.40);

            // Opacity of this slab: 1 - exp(-sigma * dt)
            float slabAlpha = 1.0 - exp(-sigma * dt);

            // Temperature: Guitar 1 injects heat
            float baseTemp = mix(3500.0, 9000.0, d);
            float heatG1   = (g1bass * 0.6 + g1level * 0.4) * 18000.0;
            float T_K      = baseTemp + heatG1;

            // Emission color at this point
            vec3 emitCol = nebulaEmission(d, T_K);

            // Warm emission pockets (Visual Detail Pass 1, softened in
            // Revision 1): one extra cheap noise sample (not a full fbm) at a
            // distinct frequency/phase from the density field, so warm
            // patches don't coincide with the dust lanes/density peaks.
            // Frequency 0.018 (not the original 0.006): at 0.006 the noise's
            // spatial period (~166 ly) was wide enough that some seeds' fixed
            // camera frustum landed entirely inside one high lobe, flooding
            // most of the frame warm instead of leaving isolated pockets -
            // checked across seeds 100/400/777 during tuning. 0.018 (~55 ly
            // period) keeps pockets patch-sized relative to the frame for any
            // seed.
            //
            // Revision 1: the original `step(0.05, d)` hard-gated the glow
            // fully on/off at a density contour, which is exactly what read
            // as a flat opaque "mask" with a hard edge in ChatGPT's review
            // (the density field itself is smooth - see DensityDebug - the
            // hard edge was purely a color-mapping artifact of this gate).
            // Replaced with a smoothstep density gate (0.02-0.10) so the glow
            // fades in/out continuously with local density instead of
            // switching on at a fixed strength - it now reads as a soft rim-
            // light along cloud edges, embedded in the volume, rather than a
            // flat pocket laid on top. (An earlier attempt in this revision
            // also multiplied by raw `d` on top of the smoothstep gate,
            // which double-attenuated and made the glow nearly invisible -
            // removed; the smoothstep alone is sufficient softening.)
            // Audio-independent - visible under silence without uBass1/uLevel1.
            float warmNoise    = noise3D(pos * 0.018 + vec3(91.3, -47.8, 22.1));
            float warmPocket   = smoothstep(0.55, 0.85, warmNoise);
            float densityGate  = smoothstep(0.02, 0.10, d);
            float warmPocketSoft = warmPocket * densityGate;
            emitCol += vec3(0.55, 0.24, 0.14) * warmPocketSoft * 1.4;

            // Depth cue (Visual Detail Pass 1): free (reuses t, no extra noise
            // sampling) - near material reads slightly warmer/brighter,
            // far material slightly cooler/dimmer (aerial-perspective-style
            // depth separation) so the nebula reads as layered, not a flat wash.
            float depthT = t / tFar;
            vec3  depthTint = mix(vec3(1.10, 1.03, 0.94), vec3(0.86, 0.92, 1.06), depthT);
            emitCol *= depthTint;

            // Scattering contribution from sun
            emitCol += sunColor * sunPhase * d * 0.25;

            // Accumulate: color gets attenuated current transmittance
            radiance += transmittance * slabAlpha * emitCol;

            // Attenuate transmittance through this slab
            transmittance *= (1.0 - slabAlpha);
        }
    }

    // Diagnostic debug views (Visual Recovery Pass 1): bypass background/star/
    // audio-envelope compositing entirely so density and raw emission structure
    // can be inspected in isolation. Both still apply gamma so they're legible
    // on screen (raw linear radiance is too dark to read otherwise). uDebugMode
    // is 0 in all normal rendering (see StellarNursery.Render()).
    if (uDebugMode == 1) {
        // Density/opacity debug: how much of the ray was absorbed/emitted into,
        // independent of color - reveals whether density structure exists at all.
        float opacity = 1.0 - transmittance;
        fragColor = vec4(vec3(pow(opacity, 1.0 / 2.2)), 1.0);
        return;
    }
    if (uDebugMode == 2) {
        // Radiance debug: raw accumulated nebula emission color, before the
        // deep-space background, stars, or brightness envelope are applied.
        vec3 dbgColor = pow(clamp(radiance, 0.0, 1.0), vec3(1.0 / 2.2));
        fragColor = vec4(dbgColor, 1.0);
        return;
    }

    // Background: deep space visible through remaining transmittance
    // Stars: rare points from a 3D hash
    vec3 bgColor = vec3(0.012, 0.005, 0.025);

    // Rare embedded stars visible through gaps: bounded point stars, not
    // filled hash cells (see pointStarLayer above - this replaces the old
    // whole-cell block that caused square/rectangular/triangular artifacts).
    // Stellar Nursery Showability Audit: original density (0.025/0.012) produced
    // only 1-2 visible stars across the whole frame even with a 3.5x brightness
    // boost applied to a screenshot - read as "no obvious stars" to a casual
    // viewer. Density roughly doubled and radius modestly increased (still a
    // small bounded smoothstep falloff per pointStarLayer, not a filled cell -
    // no risk of reintroducing the old square-cell artifact) for a noticeably
    // more populated star field.
    // Stellar Nursery Showability Revision: the Revision's much denser default
    // nebula (needed to fill the frame center) also raised the background
    // brightness stars compete against, and since stars are composited as part
    // of `bgColor` (correctly attenuated by remaining `transmittance` behind
    // nebula, same as real starlight through gas), a brighter nebula alone made
    // stars read as dimmer by comparison even though their own brightness was
    // unchanged. Star color multiplier raised (0.35 -> 0.9) and radius nudged
    // up again (still a small bounded smoothstep falloff, same shape) so stars
    // stay clearly visible against the now-richer background.
    {
        float s1 = pointStarLayer(rayDir, 45.0, 0.05,  0.13, uCamPos * 0.001);
        float s2 = pointStarLayer(rayDir, 90.0, 0.025, 0.10, uCamPos * 0.002);
        float star = s1 + s2;

        vec3 starColor = mix(vec3(0.65, 0.75, 1.0), vec3(1.0, 0.82, 0.58), hash3(rayDir * 13.7));
        bgColor += starColor * star * 1.6;
    }

    // Final composite
    vec3 color = radiance + bgColor * transmittance;

    // Performance memory: subtle enrichment over time
    color *= 1.0 + smoothstep(0.0, 180.0, uPerfTime) * 0.15;

    // Audio brightness envelope
    color *= uDimLevel + uBassCombined * uBassBrightness;

    // Gamma 2.2 (spec requirement)
    color = pow(max(color, vec3(0.0)), vec3(1.0 / 2.2));

    fragColor = vec4(clamp(color, 0.0, 1.0), 1.0);
}
