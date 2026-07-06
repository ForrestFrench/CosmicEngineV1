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

// 3D FBM: 3 octaves with specified frequencies.
// Octave 0: 0.001 ly^-1 -> structure at ~1000 ly scale
// Octave 1: 0.003 ly^-1 -> structure at ~333 ly scale
// Octave 2: 0.009 ly^-1 -> structure at ~111 ly scale
// Returns value in approximately [0, 1].
float fbm3D(vec3 p, float t) {
    float v    = 0.0;
    float freq = 0.001;
    float amp  = 1.000;

    for (int i = 0; i < 3; i++) {
        // Slow time evolution per octave (higher octaves evolve faster)
        float tScale = float(i + 1) * 0.0004;
        v += amp * noise3D(p * freq + vec3(t * tScale, float(i) * 7.3, 0.0));
        freq *= 3.0; // 0.001 -> 0.003 -> 0.009
        amp  *= 0.5; // 1.0   -> 0.5   -> 0.25
    }
    // Normalize: sum of amps = 1 + 0.5 + 0.25 = 1.75
    return v / 1.75;
}

// -------------------------------------------------------
// NEBULA DENSITY FIELD
//
// Samples 3D FBM with Guitar 2 modulation.
// g2bass compresses density (denser peaks, emptier voids).
// g2mid shifts the density threshold slightly.
// Returns density in [0, 1].
// -------------------------------------------------------

float nebulaDensity(vec3 pos, float g2bass, float g2mid) {
    float raw = fbm3D(pos, uTime);

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

    for (int i = 0; i < STEPS; i++) {
        if (transmittance < 0.005) { break; }

        // Step center position in world space (ly)
        float t   = tNear + (float(i) + 0.5) * dt;
        vec3  pos = rayPos + rayDir * t;

        // Sample density field at this world position
        float d = nebulaDensity(pos, g2bass, g2mid);

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
    {
        float s1 = pointStarLayer(rayDir, 45.0, 0.025, 0.06,  uCamPos * 0.001);
        float s2 = pointStarLayer(rayDir, 90.0, 0.012, 0.045, uCamPos * 0.002);
        float star = s1 + s2;

        vec3 starColor = mix(vec3(0.65, 0.75, 1.0), vec3(1.0, 0.82, 0.58), hash3(rayDir * 13.7));
        bgColor += starColor * star * 0.35;
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
