#version 330 core
in vec2 vUV;
out vec4 fragColor;

// WIND TURBINE / FIRE - World 03 (Phase 2.1: fire/ember design correction pass)
//
// Shader-only, fullscreen-quad, analytic 2D scene - same architecture family as
// Lava Lamp (World02): no raymarch, no textures, no mesh pipeline. A dark, hazy
// dusk-to-night industrial tableau - wind-turbine silhouettes turning against
// layered smoke, with an ember-glow horizon that slowly builds with sustained
// musical energy. Deliberately cold/desaturated at rest (blue-grey sky, charcoal
// smoke, near-black land/turbines) so that fire reads as heat-against-cold
// contrast, not orange-on-orange.
//
// Guitar 1 (Creator) -> uFireDrive: fire intensity / glow brightness / ember
// density. Guitar 2 (Sculptor) -> uWindDrive: wind speed / rotor speed / smoke
// turbulence. uSceneHeat is a single continuous 0-1 accumulator (integrated in
// C#, not here) representing sustained-energy "heat" - it is NOT a state
// machine; "Calm/Ignition/Burn" in comments below just names tuning ranges
// within that one float.
//
// Turbine rotation and smoke drift each have a time-only baseline so the scene
// is alive under total silence - audio only ever lifts those values above that
// floor, never gates them to black/frozen. Embers are a deliberate exception
// (Phase 1.2): they have no baseline and are gated hard by fireIntensity, so
// they are absent at rest and only appear once the fire signal visibly lights
// up - see the ember block below for the emberGate mechanics.
//
// Phase 2 additions (all driven by the existing fireIntensity/uSceneHeat -
// no new uniforms, no C# changes): shaped flame tongues rising in the same
// background horizon region as the glow band/embers (gated by flameGate,
// reusing fireIntensity), a second tighter-radius "hot rim" smoke-
// underlighting term on top of the original broad ambient tint, and a small,
// masked, fireIntensity-scaled heat-distortion UV displacement (pWarped)
// applied only to the sky/background-turbine/smoke sampling that sits behind
// the fire - never to the fire's own light sources or the solid ground/
// foreground silhouettes. See each block below for detail.
//
// Refinement Pass 2 (this pass, on top of the accepted Phase 2.1 corrective
// pass below): the user is happy with the current design and asked for three
// small, controlled refinements only, not a redesign: (1) heat-wave
// distortion made distant turbines wobble cartoonishly at high intensity -
// background turbines now sample a separate, gentler, lower-frequency,
// height-tapered warp instead of the fuller sky/smoke one, and the overall
// distortion drive is soft-clamped at high intensity; (2) ember size/
// placement/clustering are explicitly preserved untouched, but density (how
// many of uEmberCount are actually active) now climbs progressively with
// fireIntensity via a per-ember soft activation threshold; (3) the fire
// front's height cap now keeps growing (not flat) past where flameGate
// itself saturates, and a separate soft, noise-modulated high-altitude haze
// layer (not more flame geometry) extends the glow further into the sky at
// high intensity. See each block below for detail.
//
// Phase 2.1 corrective pass: user feedback on Phase 2's screenshot
// flagged two problems - embers reading as big, sparse, close-camera
// foreground particles instead of small distant sparks belonging to the fire
// line, and flame tongues reading as a row of individually repeated cones
// rather than one continuous distant fire front. Fixes: (1) the 5 discrete
// flame tongues are replaced by a single continuous scrolling height-field
// fire front, fused into the existing horizon glow band rather than composited
// as a separate layer, with distance contrast (hot at the base, losing
// contrast into haze toward the tip) instead of Phase 2's tip-whitening;
// (2) embers are smaller (squared-hash size skew), more numerous, clustered
// near a few fire-front locations instead of spread uniformly across the full
// width, biased toward the base with a per-ember brightness power curve so
// most are dim, and cool/shrink as they rise; (3) both the horizon band and a
// new near-fire smoke veil are broken up with FBM so the fire blends into the
// smoke rather than sitting as a crisp cutout. Ember hard-gating (zero
// baseline, absent at rest), heat distortion, background-turbine embedding/
// opacity, and composite order are all unchanged from Phase 1.2/Phase 2.
//
// Phase 3 (turbine/geometry de-stiffening, this pass): fire/smoke/embers/
// distortion above are untouched - this pass is scoped entirely to
// turbineSDF() (renamed turbineMask(), see its own comment block) and its
// four call sites. Goal: make the turbines read as less mechanically rigid
// without disturbing anything already accepted. (1) Blade motion blur - a
// cheap single-pass analytic stand-in (this architecture has no render-to-
// texture history buffer for true accumulation): the blade mask is sampled
// at 3 angles spread around the current rotor angle and averaged, with the
// spread driven by an estimated angular speed (derived from uWindDrive,
// already an existing uniform - no new uniform added). (2) Tower flex - the
// tower is now a 3-segment tapered-capsule chain instead of one straight
// segment; at high uWindDrive the upper segments pick up a small, height-
// increasing horizontal offset plus a slow per-turbine sway, returning fully
// upright as wind drops. (3) Nacelle detail - tried in this pass (a secondary
// tail-stub capsule plus a top-lit/underside-shaded tint on the two
// foreground nacelles) but removed same-day per user feedback ("looks
// cheap" / wanted plain all-black turbines) - see AUDIT.md Entry 39 addendum.
// The nacelle is back to a single plain capsule, flat black like the rest of
// the turbine, same as pre-Phase-3. (4)/(5) Parallax + haze grading - the farther
// background turbine (BG2) was nudged smaller/higher/hazier relative to the
// nearer one (BG1) so the two-band depth separation reads more clearly; no
// third turbine/band was added (stays shader-only, no new rotor-angle
// uniform). Every one of these is verified mathematically neutral at rest/
// low wind (see turbineMask()'s own comments) - zero behavior change to the
// already-accepted look at idle.

uniform float uTime;
uniform float uSceneHeat;        // 0 (cold/calm) .. 1 (fully hot), continuous
uniform float uFireDrive;        // Guitar 1 / Creator, 0-1
uniform float uWindDrive;        // Guitar 2 / Sculptor, 0-1
uniform float uSmokeTurbulence;  // 0-1ish, baseline never 0 (set in C#)
uniform int   uEmberCount;       // P1-aware: Safe uses fewer, High more

uniform float uRotorAngleFG1;
uniform float uRotorAngleFG2;
uniform float uRotorAngleBG1;
uniform float uRotorAngleBG2;

const int MAX_EMBERS = 40;

// --- cheap analytic noise (no textures) ------------------------------------

float hash1(float n) { return fract(sin(n) * 43758.5453123); }

float hash2(vec2 p) { return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453123); }

// Named vnoise2 (not noise2) - GLSL has a built-in noise2(vec2) returning
// vec2, and a same-named float-returning function collides with it.
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

// 3-octave FBM (loop is capped at 3, callers can request fewer) - cheap enough
// for a fullscreen pass on integrated GPUs, matches the "2-3 octave" art brief.
float fbm2(vec2 p, int octaves) {
    float sum  = 0.0;
    float amp  = 0.5;
    float freq = 1.0;
    for (int i = 0; i < 3; i++) {
        if (i >= octaves) break;
        sum  += amp * vnoise2(p * freq);
        freq *= 2.02; // deliberately off-power-of-two, avoids repeating tiling
        amp  *= 0.5;
    }
    return sum;
}

// --- 2D SDF primitives -------------------------------------------------------

float sdCapsule(vec2 p, vec2 a, vec2 b, float r) {
    vec2  pa = p - a, ba = b - a;
    float h  = clamp(dot(pa, ba) / dot(ba, ba), 0.0, 1.0);
    return length(pa - ba * h) - r;
}

// Approximate tapered capsule (linearly-varying radius along the segment) -
// cheap, visually sufficient for a silhouette tower/blade, not a true
// mathematically-exact rounded-cone SDF.
float sdTaperedCapsule(vec2 p, vec2 a, vec2 b, float ra, float rb) {
    vec2  pa = p - a, ba = b - a;
    float h  = clamp(dot(pa, ba) / dot(ba, ba), 0.0, 1.0);
    float r  = mix(ra, rb, h);
    return length(pa - ba * h) - r;
}

// One full turbine: tapered tower + nacelle blob + 3 tapered-capsule blades at
// 120-degree spacing, rotated by rotorAngle. Returns a signed distance (min of
// all parts) so the caller can threshold/smoothstep it into a silhouette mask.
const float TOWER_HEIGHT = 0.50;

// Phase 3: rough angular-speed estimate for blade-blur only, mirroring
// WindTurbineFireScene.cs's BaseRotorSpeed/RotorWindGain constants - the
// shader has no access to the C# integrator's actual smoothed speed, so this
// is a deliberate, documented approximation (same shape, not frame-exact)
// used purely to decide how much blur to draw, not to move the blades
// themselves (uRotorAngle* uniforms still own the real angle).
const float ROTOR_BASE_SPEED_EST = 0.35; // mirrors BaseRotorSpeed
const float ROTOR_WIND_GAIN_EST  = 1.10; // mirrors RotorWindGain

// Phase 1.1 embedding fix: the ground/ridge silhouette (see main()'s ridgeN/
// ridgeY) is a noisy 1D FBM heightline whose top edge sits, at every x
// position, strictly below GROUND_Y - as little as ~0.009 and as much as
// ~0.05 for a tower rooted at GROUND_Y itself, or up to ~0.065 for a
// background tower rooted slightly above GROUND_Y (see turbineMask() call
// sites below). A tower's tapered base capsule previously ended exactly at
// its own baseY, which is always above that ridge line by that same margin -
// so the capsule's rounded cap was always visible in the gap, reading as
// "not embedded"/floating. EMBED_DEPTH is a single absolute (not
// scale-multiplied) constant, comfortably larger than the worst-case gap
// across every turbine's baseY plus its base radius plus margin, used below
// to bury a separate, constant-radius capsule straight down from each
// tower's existing base point - this only ever extends geometry below
// baseY, so the visible tapered tower from baseY up to the hub (and
// everything else about the turbine) is completely unchanged.
const float EMBED_DEPTH = 0.15;

// Phase 3: returns a thresholded mask (float), not a raw signed distance -
// renamed from turbineSDF() to turbineMask() because blade motion-blur needs
// to average *thresholded* masks from 3 angle samples, not a raw min-distance
// across samples (a distance union would just widen the solid blade shape,
// not soften/thin it toward the tips the way real motion blur does) - so
// this function does its own smoothstep-and-composite internally using the
// `edge` the caller used to apply itself. (Phase 3 briefly added a second
// return component, a nacelle rim-light tint - removed same-day per user
// feedback ("looks cheap"); see AUDIT.md Entry 39 addendum. Back to a plain
// float, matching the pre-Phase-3 shape of this function.)
//
// Verified mathematically neutral at rest: smoothstep(edge,0,x) is monotonic
// decreasing in x, so max(smoothstep(edge,0,a), smoothstep(edge,0,b)) ==
// smoothstep(edge,0,min(a,b)) whenever both use the same edge - true here
// whenever blurHalf is 0 (low/no wind), since bladeEdge then equals edge and
// all 3 angle samples coincide. Tower flex is similarly 0 at low wind, and
// the 3-segment tower chain is geometrically identical to the old single
// tapered capsule when unflexed (shared endpoints, same linear taper). So at
// rest this produces byte-for-byte the same mask as the pre-Phase-3 shader.
float turbineMask(vec2 p, float hubX, float baseY, float scale, float rotorAngle, float edge, float speedMul) {
    // --- Tower flex (Phase 3) --------------------------------------------
    // Small, height-increasing horizontal offset that grows with uWindDrive
    // and returns to 0 (fully upright) below the flexT threshold. A slow,
    // per-turbine sway (hashed phase so the 4 turbines don't move in
    // lockstep) keeps a windy tower from looking like a static bent pose.
    float swayPhase = hash1(hubX * 17.13 + baseY * 3.7 + 1.0) * 6.2831;
    float flexT      = smoothstep(0.30, 1.0, uWindDrive);
    float swayWobble = 0.85 + 0.15 * sin(uTime * 0.55 + swayPhase);
    float flexAmt     = 0.050 * scale * flexT * swayWobble;

    vec2 towerBase = vec2(hubX, baseY);
    vec2 hub       = vec2(hubX, baseY + TOWER_HEIGHT * scale);
    // Control points at 0/0.42/0.75/1.0 height fraction, offset increasing
    // with height (cantilever-style curve, most bend near the hub).
    vec2 flexP1    = mix(towerBase, hub, 0.42) + vec2(flexAmt * 0.15, 0.0);
    vec2 flexP2    = mix(towerBase, hub, 0.75) + vec2(flexAmt * 0.55, 0.0);
    vec2 hubFlexed = hub + vec2(flexAmt, 0.0);

    float r0 = 0.028 * scale, r1 = 0.009 * scale;
    float rMid1 = mix(r0, r1, 0.42), rMid2 = mix(r0, r1, 0.75);

    float dTower = sdTaperedCapsule(p, towerBase, flexP1, r0, rMid1);
    dTower = min(dTower, sdTaperedCapsule(p, flexP1, flexP2, rMid1, rMid2));
    dTower = min(dTower, sdTaperedCapsule(p, flexP2, hubFlexed, rMid2, r1));

    // Buried extension - anchored at the unflexed ground point, straight
    // down, exactly as before (Phase 1.1 embedding fix); flex only ever
    // offsets geometry above ground, so embedding is completely unaffected.
    vec2  towerBuriedEnd = vec2(hubX, baseY - EMBED_DEPTH);
    float dBuried  = sdCapsule(p, towerBase, towerBuriedEnd, 0.028 * scale);
    dTower = min(dTower, dBuried);

    // --- Nacelle -------------------------------------------------------------
    // Single plain capsule, flat black like the rest of the turbine - a
    // secondary tail-stub bump and a top-lit/underside-shaded tint were tried
    // in Phase 3 and removed same-day per user feedback ("looks cheap"); see
    // AUDIT.md Entry 39 addendum.
    vec2  nacelleA = hubFlexed - vec2(0.045 * scale, 0.0);
    vec2  nacelleB = hubFlexed + vec2(0.020 * scale, 0.0);
    float dNacelle = sdCapsule(p, nacelleA, nacelleB, 0.020 * scale);

    // --- Blades, motion-blurred at speed (Phase 3) ------------------------
    // angSpeed is a rough estimate only (see ROTOR_*_EST comment above);
    // blurHalf ramps in above a wind-drive threshold so idle/low-wind
    // turbines stay perfectly crisp (blurHalf clamps to ~0 there).
    float angSpeed = (ROTOR_BASE_SPEED_EST + uWindDrive * ROTOR_WIND_GAIN_EST) * speedMul;
    float blurHalf = clamp(angSpeed * 0.10, 0.0, 0.22) * smoothstep(0.15, 0.60, uWindDrive);

    float dBladeC = 1.0e5, dBladeM = 1.0e5, dBladeP = 1.0e5; // center / minus / plus angle samples
    for (int i = 0; i < 3; i++) {
        float baseA = float(i) * 2.0943951; // 120 degrees
        vec2  dirC  = vec2(cos(rotorAngle + baseA),            sin(rotorAngle + baseA));
        vec2  dirM  = vec2(cos(rotorAngle - blurHalf + baseA), sin(rotorAngle - blurHalf + baseA));
        vec2  dirP  = vec2(cos(rotorAngle + blurHalf + baseA), sin(rotorAngle + blurHalf + baseA));

        dBladeC = min(dBladeC, sdTaperedCapsule(p, hubFlexed, hubFlexed + dirC * (0.34 * scale), 0.016 * scale, 0.003 * scale));
        dBladeM = min(dBladeM, sdTaperedCapsule(p, hubFlexed, hubFlexed + dirM * (0.34 * scale), 0.016 * scale, 0.003 * scale));
        dBladeP = min(dBladeP, sdTaperedCapsule(p, hubFlexed, hubFlexed + dirP * (0.34 * scale), 0.016 * scale, 0.003 * scale));
    }

    // A touch of extra edge softness scaled by blur amount, on top of the
    // 3-sample averaging itself, so the blurred region reads as soft rather
    // than 3 overlapping crisp copies.
    float bladeEdge = edge + blurHalf * 0.02;
    float mBladeC = smoothstep(bladeEdge, 0.0, dBladeC);
    float mBladeM = smoothstep(bladeEdge, 0.0, dBladeM);
    float mBladeP = smoothstep(bladeEdge, 0.0, dBladeP);
    float mBlade  = (mBladeC + mBladeM + mBladeP) / 3.0;

    float mRigid = smoothstep(edge, 0.0, min(dTower, dNacelle));
    float mFinal = max(mRigid, mBlade);

    return mFinal;
}

void main() {
    vec2 uv = vUV;
    uv.y = 1.0 - uv.y;

    // Aspect-corrected centered coordinates (matches Lava Lamp / Stellar
    // Nursery's shared 16:9 assumption).
    float aspect = 1.7778;
    vec2  p = (uv - 0.5);
    p.x *= aspect;
    // After the uv.y flip above, p.y is negative at the top of the screen and
    // positive at the bottom (matches FullscreenQuad's NDC -> vUV mapping).
    // Every constant below (GROUND_Y, TOWER_HEIGHT, sky/ember Y ranges) assumes
    // the opposite - positive p.y = up/sky, negative p.y = down/ground - so
    // negate once here rather than rederive every constant.
    p.y = -p.y;

    const float GROUND_Y = -0.22;

    // --- Fire drive / heat composite -----------------------------------------
    // Low-frequency flicker: 3 summed incommensurate sines, not white noise -
    // reads as a slow, organic ember-bed pulse rather than static or strobing.
    float flicker = 0.78
                   + 0.14 * sin(uTime * 0.71 + 0.0)
                   + 0.09 * sin(uTime * 1.33 + 1.7)
                   + 0.05 * sin(uTime * 0.29 + 4.1);

    float fireIntensity = clamp(uSceneHeat * 0.70 + uFireDrive * 0.45, 0.0, 1.0) * flicker;

    // Deep-red -> orange -> white-orange hue ramp with intensity, plus a gentle
    // magenta nudge at heat peaks (nudge, never a hue flip - see art brief).
    vec3 glowDeepRed      = vec3(0.32, 0.03, 0.02);
    vec3 glowOrange       = vec3(0.95, 0.35, 0.05);
    vec3 glowWhiteOrange  = vec3(1.00, 0.78, 0.48);
    vec3 glowColor = mix(glowDeepRed, glowOrange, clamp(fireIntensity * 1.4, 0.0, 1.0));
    glowColor = mix(glowColor, glowWhiteOrange, clamp((fireIntensity - 0.6) * 2.5, 0.0, 1.0));
    glowColor = mix(glowColor, vec3(0.85, 0.22, 0.55), smoothstep(0.80, 1.0, uSceneHeat) * 0.16);

    // --- Heat distortion (Phase 2, reworked Refinement Pass 2) -----------------
    // A small, capped screen-space UV displacement applied only to the layers
    // behind/near the fire (sky, background turbines, smoke sampling below) -
    // masked to a band near the horizon and scaled by fireIntensity, so it is
    // exactly zero at rest and reads as a subtle heat-shimmer at high heat,
    // never a whole-frame wobble. The horizon glow band, fire front, embers,
    // ground, and foreground turbines are deliberately left undistorted
    // (evaluated against the original `p`) so the fire itself and the
    // solid/near silhouettes stay crisp.
    //
    // Refinement Pass 2: user feedback was that at high intensity distant
    // turbines got "too wobbly - cartoon-like." Root cause (confirmed by
    // reasoning about the sampling, not guessed): the same high-frequency
    // sin/cos offset was being sampled at every point across a turbine's own
    // thin silhouette, so different parts of the *same* turbine (tip vs.
    // base) picked up different phases of the wave and appeared to bend
    // independently rather than shimmer as one rigid shape - a diffuse field
    // (sky/smoke) doesn't show this because it has no hard edges to bend.
    // Fix: two separate warps from here on - the original (sky/smoke) keeps
    // shimmering at close to its prior strength since a shifted noise/gradient
    // sample there just reads as haze, while a second, much gentler and lower-
    // frequency warp is used only for background-turbine SDF sampling below,
    // plus a small extra height taper so blade tips (furthest from the
    // ground-hugging heat) get the least distortion of all. Overall driving
    // intensity is also soft-clamped so distortion stops growing well before
    // the very top of the heat range, instead of scaling linearly to 1.0.
    float distortBandDist = abs(p.y - (GROUND_Y + 0.06));
    float distortMask      = exp(-distortBandDist * distortBandDist * 9.0);
    float distortDrive      = min(fireIntensity, 0.80); // soft-clamp at high intensity
    float distortAmp       = 0.0028 * distortDrive * distortMask; // was 0.0035, no clamp
    vec2  distortOffset    = vec2(
        sin(p.y * 22.0 + uTime * 3.1),
        cos(p.x * 17.0 + uTime * 2.4)
    ) * distortAmp;
    vec2  pWarped = p + distortOffset;

    // Background-turbine-only warp: lower spatial frequency (less phase
    // variance across one turbine's footprint = less independent bending),
    // ~35% of the sky/smoke amplitude, and an extra taper that fades out with
    // height above the ground so tall blade tips stay closest to undistorted
    // while the tower base can still pick up a faint shimmer.
    float turbineHeightTaper = exp(-max(0.0, p.y - (GROUND_Y + 0.02)) * 5.0);
    vec2  distortOffsetTurbine = vec2(
        sin(p.y * 7.0 + uTime * 3.1),
        cos(p.x * 6.0 + uTime * 2.4)
    ) * (distortAmp * 0.35 * turbineHeightTaper);
    vec2  pWarpedTurbine = p + distortOffsetTurbine;

    // --- Sky: cold blue-grey near-black gradient ------------------------------
    // skyT: 0 at horizon, 1 at the top of frame. Top stays near-black; horizon
    // gets a very slight warm nudge with heat, never flipping the sky itself
    // orange (that job belongs entirely to the horizon glow band below).
    // Uses pWarped (heat distortion) rather than p, per the comment above.
    float skyT = clamp((pWarped.y - GROUND_Y) / (0.5 - GROUND_Y), 0.0, 1.0);
    vec3  skyHorizon = mix(vec3(0.075, 0.095, 0.150), vec3(0.16, 0.09, 0.09), uSceneHeat * 0.30);
    vec3  skyTop      = vec3(0.018, 0.022, 0.040);
    vec3  color = mix(skyHorizon, skyTop, skyT);

    // Horizon glow band: gaussian-ish falloff centered just above the ground
    // line, shaped so it reads as light coming from behind the ridge.
    float bandDist = abs(p.y - (GROUND_Y + 0.02));
    float band     = exp(-bandDist * bandDist * 70.0);

    // Phase 2.1 corrective S1: break the flat glow band up with slow FBM so it
    // doesn't read as one uniform gradient - shares noise coordinates with the
    // flame front below so the two visually belong to each other.
    band *= 0.7 + 0.5 * fbm2(vec2(p.x * 3.1 - uTime * 0.04, 1.3), 2);

    // --- Continuous fire front (Phase 2.1 corrective, replaces Phase 2's 5
    // discrete flame tongues) --------------------------------------------------
    // User feedback on Phase 2: 5 evenly-spaced procedural tongues read as "a
    // row of individually repeated cones," not a single distant fire line.
    // Fix: one continuous scrolling height-field band evaluated across the
    // full width in a single pass (no per-tongue loop, so there is nothing
    // left to repeat) - a warped-x FBM height function masked by a soft,
    // wide-topped smoothstep rather than a per-tongue hard triangular taper,
    // so the top edge reads as one broken, licking fire line. Gated by the
    // exact same fireIntensity variable that already drives the glow band/
    // embers (flameGate, unchanged threshold/range from Phase 2).
    float flameGate = smoothstep(0.35, 0.78, fireIntensity);
    float fm = 0.0;
    vec3  frontColor = glowColor;
    if (flameGate > 0.001) {
        float drift = uTime * 0.05;
        // maxH capped well under the background-turbine hub height
        // (~GROUND_Y+0.18) so the fire front never overtops the silhouettes
        // behind it - see Preserve list / risk flags. Refinement Pass 2: the
        // fixed 0.11 cap is replaced with a continued growth curve
        // (heightDrive) that keeps climbing past where flameGate itself
        // saturates (0.78), so the fire stays low early, rises modestly
        // through warm/mid, and reaches noticeably higher at hot/intense -
        // still capped at 0.15, comfortably under the turbine-hub ceiling.
        float heightDrive = smoothstep(0.35, 1.0, fireIntensity);
        float maxH  = mix(0.075, 0.15, heightDrive) * flameGate;

        // Warp the x sampling coordinate before evaluating the height field -
        // this is what kills the residual per-column regularity a plain
        // fbm2(vec2(p.x, ...)) would still show as an evenly-spaced cadence.
        float xw = p.x + 0.06 * fbm2(vec2(p.x * 1.4, uTime * 0.12), 2);

        float H = max(maxH * (0.35 + 0.65 * fbm2(vec2(xw * 2.6 + drift, 3.7), 2)), 0.0001);

        float h = p.y - (GROUND_Y + 0.005);
        // Soft, wide upper edge (not a crisp per-tongue taper) - hot/solid
        // near the base, fading out well before H.
        fm = smoothstep(H, H * 0.35, h);

        // Interior texture: scrolling domain-warped FBM breaks the top edge
        // into transient licks so it never reads as a static silhouette.
        float licks = fbm2(vec2(xw * 9.0, h * 5.0 - uTime * 1.8), 2);
        fm *= clamp(0.35 + 0.8 * licks, 0.0, 1.0);
        fm *= flameGate;

        // Distance contrast: this is a *distant* fire line, so bias hot near
        // the base (h ~= 0) and lose contrast into the sky/haze color toward
        // the tip - the inverse of Phase 2's tip-whitening, which read as
        // foreground-fire logic.
        float tipT = clamp(h / H, 0.0, 1.0);
        frontColor = mix(glowColor, skyHorizon, 0.25 * tipT);
    }

    // Fused into the existing band term (not stacked as an independent flame
    // layer) - one additive composite so the fire front reads as part of the
    // same horizon glow, not a layer floating on top of it.
    color += (glowColor * band + frontColor * fm * 0.6) * fireIntensity * 1.15;

    // --- High-altitude smoke-lit haze (Refinement Pass 2) -----------------------
    // User feedback: fire should stay low on the horizon early, then radiate
    // higher into the smoke/sky as intensity grows. Rather than just letting
    // the fire front's own crisp mask (fm above) keep growing indefinitely -
    // which would eventually start reading as flame geometry reaching too
    // high - this is a separate, soft, noise-modulated tint applied well
    // above the fire front's own visible top: "smoke-lit orange haze"
    // (atmosphere catching the fire's light), not more flame. Zero below
    // mid-high intensity, irregular via FBM (never a flat wash), mixed only
    // from the existing sky/glow palette (no new hue). Evaluated against `p`
    // (undistorted), matching the rest of the fire per the heat-distortion
    // Preserve list.
    float hazeDrive = smoothstep(0.55, 1.0, fireIntensity);
    if (hazeDrive > 0.001) {
        float hy = p.y - (GROUND_Y + 0.02);
        float hazeNoise = fbm2(vec2(p.x * 1.8 - uTime * 0.03, hy * 2.4 + 5.5), 2);
        // Noise-perturbed reach (not a hard cutoff) so the top edge is soft
        // and uneven rather than a flat orange wash.
        float hazeTop = mix(0.12, 0.30, hazeDrive) * (0.75 + 0.5 * hazeNoise);
        float hazeFalloff = smoothstep(0.0, 0.04, hy)
                           * (1.0 - smoothstep(hazeTop * 0.35, hazeTop, hy));
        float hazeMask = hazeFalloff * clamp(0.3 + 0.6 * hazeNoise, 0.0, 1.0) * hazeDrive;
        vec3  hazeColor = mix(skyHorizon, glowColor, 0.55);
        color += hazeColor * hazeMask * 0.28;
    }

    // --- Embers: fixed-loop analytic particles, grouped with the background
    // horizon glow (Phase 1.2) -------------------------------------------------
    // User feedback: embers previously spanned the full foreground frame
    // height with an always-on baseline, reading as a separate foreground
    // effect rather than part of the distant fire. Now confined to a narrow
    // band hugging the horizon glow (the same screen region as the background
    // turbines and glow band above) and drawn here - before the background
    // turbines, smoke, ground, and foreground turbines are composited - so
    // every closer/co-depth element properly occludes embers that drift
    // "behind" them (including the background turbines themselves, so an
    // ember never appears to shine in front of a background tower's own
    // silhouette), reinforcing "far off in the distance." Gated hard by
    // fireIntensity (the exact same driver as the glow band's own color/
    // brightness) via emberGate below, so embers and the glow visibly light
    // up together - near-invisible at rest, not a separate always-on layer
    // (no baseline term anymore, a deliberate, explicit change from Phase 1's
    // "always some ember glow" behavior). Turbulent (hash-seeded, multi-sine)
    // drift paths are unchanged from Phase 1 - only position/scale/gating/
    // draw-order changed here.
    // Phase 2.1 corrective: user feedback on Phase 2 was that embers still
    // read as big, sparse, close-camera foreground particles rather than
    // small distant sparks belonging to the fire line. Fixes below (numbered
    // to match the design-review spec): E1 more/smaller embers (count raised
    // in C#, size cut + squared-hash skew toward small), E2 a per-ember
    // brightness power curve so most embers are dim and only a few are
    // near-full brightness, E3 a smaller/lighter halo, E4 clustering near a
    // few fire-front locations instead of uniform full-width spawn, E5 a
    // base-weighted vertical bias with a tighter top fade, E6 a
    // distance/cooling cue (shrink + cool toward deep red as an ember rises),
    // E7 a ~20% slower drift. Hard gating (emberGate, zero baseline, absent
    // at rest) and composite position are unchanged from Phase 1.2.
    //
    // Refinement Pass 2: ember DENSITY (how many of uEmberCount are actually
    // active) now climbs with fireIntensity - low/rest close to none, warm
    // sparse, hot modestly denser, intense noticeably more active - while
    // every existing size/placement/brightness-curve/clustering rule above
    // is completely untouched, per explicit user request to preserve the
    // current look. Implemented as a per-ember soft activation threshold
    // (densityGate below) rather than shrinking/growing the loop's `i <
    // uEmberCount` bound directly, so embers switch on progressively with a
    // soft edge instead of a hard population cutoff.
    float emberGate = smoothstep(0.05, 0.40, fireIntensity);
    float densityDrive = smoothstep(0.10, 0.95, fireIntensity);
    vec3  emberAccum = vec3(0.0);
    for (int i = 0; i < MAX_EMBERS; i++) {
        if (i >= uEmberCount) break;

        float fi   = float(i);
        float seed = fi * 13.17 + 4.0;

        // E7: ~20% slower than Phase 1.2's 0.045+0.05*hash1(seed) for a
        // lazier drift befitting small distant sparks.
        float lifeSpeed = 0.036 + 0.04 * hash1(seed);
        float t         = uTime * lifeSpeed + hash1(seed + 1.0) * 10.0;
        float cyc       = fract(t);

        // E2: per-ember brightness power curve, applied after emberGate (not
        // by touching the gate threshold, per risk flags) - pow(.,2.8) skews
        // heavily toward dim (~80% land 0.05-0.35) with only a few embers
        // near 1.0, instead of Phase 2's uniform per-ember brightness. New
        // hash offset (seed+5.0) so this doesn't correlate with the existing
        // seed+0..4 uses above.
        float bMax = pow(hash1(seed + 5.0), 2.8);

        // Refinement Pass 2: per-ember activation level - a stable,
        // per-ember random threshold (new offset seed+8.0, unrelated to the
        // seed+0..7 uses already in play) compared against densityDrive
        // below, so a growing subset of embers "switches on" as the fire
        // intensifies, low-threshold embers first.
        float actLevel = hash1(seed + 8.0) * 0.9;
        float densityGate = smoothstep(actLevel - 0.12, actLevel + 0.12, densityDrive);

        // E5: base-weighted vertical bias (pow(cyc,0.75) skews dwell time
        // toward the bottom of the band) and only high-bMax embers reach the
        // top of the band at all - dim embers stay low, near the fire base.
        float topY = mix(GROUND_Y + 0.08, GROUND_Y + 0.20, bMax);
        float y    = mix(GROUND_Y - 0.02, topY, pow(cyc, 0.75));

        // E4: cluster spawn near a handful of fire-front locations instead of
        // uniform full-width placement, so embers visually belong to the
        // flames rather than scattering evenly across the whole horizon.
        // New hash offsets (seed+6.0/7.0), both >= 5.0 per risk flags.
        const int EMBER_CLUSTERS = 4;
        float clusterId     = floor(hash1(seed + 6.0) * float(EMBER_CLUSTERS));
        float clusterCenter = mix(-0.65, 0.65, (clusterId + 0.5) / float(EMBER_CLUSTERS));
        float clusterSpread = 0.10 + 0.05 * hash1(seed + 7.0); // +/- 0.10-0.15
        float baseX         = clusterCenter + (hash1(seed + 2.0) * 2.0 - 1.0) * clusterSpread;

        float windDrift = (0.10 + 0.28 * uWindDrive) * cyc;

        // 3 incommensurate sine perturbations - deliberately non-matching
        // frequencies/phases so no two embers (or one ember over its own
        // life) trace the same curve.
        float turb = 0.04  * sin(t * 3.10 + seed)
                   + 0.025 * sin(t * 7.30 - seed * 1.7)
                   + 0.015 * sin(t * 1.70 + seed * 2.3);

        vec2 emberPos = vec2(baseX + windDrift + turb * (0.6 + uWindDrive), y);

        float dist = length(p - emberPos);

        // E1: smaller base size than Phase 2, hash squared so the
        // distribution skews toward small (the squared term pulls most
        // samples toward 0 while still allowing a few larger outliers).
        float hSize = hash1(seed + 3.0);
        hSize *= hSize;
        float size = 0.0010 + 0.0016 * hSize;

        // E6: distance/cooling cue - embers shrink and cool toward deep red
        // as they rise (cyc -> 1), reinforcing "receding into distant haze"
        // rather than a flat, unchanging spark.
        size *= (1.0 - 0.4 * cyc);

        // E5 (top fade): tightened from Phase 2's (0.72,1.0) to (0.55,0.95)
        // so embers fade out well before the top of the band.
        float fade = smoothstep(0.0, 0.15, cyc) * (1.0 - smoothstep(0.55, 0.95, cyc));

        float brightness = fade * emberGate * bMax * densityGate;

        vec3 emberColor = mix(vec3(0.55, 0.14, 0.05), vec3(1.0, 0.55, 0.16), hash1(seed + 4.0));
        emberColor = mix(emberColor, glowDeepRed, clamp(cyc * 0.6, 0.0, 1.0));

        // E3: smaller, lighter halo than Phase 2's size*4.0 @ 0.22 - that was
        // the main "big foreground dot" contributor; brightness already
        // carries the bMax factor so the halo is implicitly gated by it too.
        emberAccum += emberColor * brightness * smoothstep(size, 0.0, dist) * 1.1;
        emberAccum += emberColor * brightness * 0.10 * smoothstep(size * 2.5, 0.0, dist);
    }
    color += emberAccum;

    // --- Background/distant turbines (hazed, atmospheric perspective) --------
    // Drawn after embers (so their silhouettes occlude any ember behind them)
    // but before smoke (so smoke can still drift in front of them); tinted
    // toward the local sky color rather than pure black, simulating
    // haze/distance.
    //
    // Phase 1.2 fix: the blend-back-toward-`color` factor here was previously
    // 0.60/0.68 - since `color` already includes the horizon glow band (and
    // now embers) by this point, that meant the turbine's own fill was mostly
    // the glow's own color showing straight through the silhouette (a real
    // transparency bug, not just haze) - most visible as "you can see through
    // the towers" when the glow behind them is bright. Cut to 0.18/0.22 so
    // the silhouette reads as solid even at high heat, while keeping a small
    // blend for atmospheric-perspective/haze feel (fully flat black read too
    // harsh by comparison to the rest of this layer's treatment).
    //
    // Phase 2: sampled against a heat-distortion warp, not p, so the
    // background turbines' own outline visibly shimmers near the fire at
    // high heat - explicitly called for by the art brief ("distant turbines"
    // are one of the named heat-distortion targets), zero effect at rest
    // since distortAmp is 0 there. Refinement Pass 2: uses pWarpedTurbine
    // (the gentler, lower-frequency, height-tapered warp defined above), not
    // the fuller sky/smoke pWarped, so the shimmer stays visible without the
    // turbines bending/wobbling independently part-by-part.
    //
    // Phase 3 parallax/haze grading: BG2 (the farther of the two background
    // turbines - smaller scale, sits higher) was previously only weakly
    // separated from BG1 (scale 0.34->0.24, haze blend 0.18->0.22, a narrow
    // gap). Widened on both axes so the two-band depth separation actually
    // reads: BG2 nudged smaller (0.24->0.19) and higher (+0.015->+0.026), and
    // its haze blend raised further (0.22->0.32, still well under the 0.60/
    // 0.68 that Phase 1.2 fixed as a transparency bug) while BG1's own blend
    // was nudged down slightly (0.18->0.16) for more contrast between the
    // two. Speed multipliers (0.82/0.94) passed through unchanged, matching
    // WindTurbineFireScene.cs's BG1SpeedMul/BG2SpeedMul, for blade-blur only.
    {
        float m1 = turbineMask(pWarpedTurbine, -0.10, GROUND_Y + 0.01, 0.34, uRotorAngleBG1, 0.007, 0.82);
        vec3  c1 = mix(vec3(0.035, 0.038, 0.055), color, 0.16);
        color = mix(color, c1, m1);

        float m2 = turbineMask(pWarpedTurbine, 0.62, GROUND_Y + 0.026, 0.19, uRotorAngleBG2, 0.006, 0.94);
        vec3  c2 = mix(vec3(0.030, 0.033, 0.050), color, 0.32);
        color = mix(color, c2, m2);
    }

    // --- Smoke: 2-3 octave FBM, slow scroll + turbulent warp ------------------
    // Domain warp (not just scroll) keeps motion from reading as mechanical
    // straight-line drift, satisfying "slightly-too-slow smoke, never
    // straight-line" from the art brief. Phase 2: the noise-sampling
    // coordinate is built from pWarped (heat distortion), not p, so smoke
    // texture itself visibly shimmers near the fire at high heat; the
    // vertical extent gate (smokeBand below) intentionally still uses the
    // original p.y so the smoke's overall silhouette/coverage doesn't shift,
    // only its internal texture ripples.
    vec2 smokeWarp = vec2(
        sin(pWarped.y * 4.0 + uTime * 0.15) * 0.06,
        cos(pWarped.x * 3.0 + uTime * 0.12) * 0.04
    ) * uSmokeTurbulence;

    vec2 smokeUV = vec2(
        pWarped.x * 1.30 - uTime * (0.015 + 0.05 * uWindDrive),
        pWarped.y * 2.20 + uTime * 0.01
    ) + smokeWarp;

    float smokeN    = fbm2(smokeUV, 3);
    float smokeBand = smoothstep(GROUND_Y - 0.04, GROUND_Y + 0.30, p.y) *
                       (1.0 - smoothstep(0.40, 0.50, p.y));
    float smokeMask = smoothstep(0.34, 0.74, smokeN) * smokeBand;

    // Heavily desaturated charcoal grey base, underlit/tinted by the horizon
    // glow where it's close enough - this is the "sells fire without real flame
    // shapes" trick called out in the art brief. Phase 2: two falloff terms
    // instead of one - glowFalloffSoft is the original broad ambient tint
    // (light generally cast upward into the smoke layer), glowFalloffHot is a
    // much tighter-radius term added on top, reading as a distinct hot rim
    // right where smoke meets the fire below, rather than one flat gradient.
    vec3  smokeBase       = vec3(0.15, 0.155, 0.17);
    float glowFalloffSoft = exp(-max(p.y - GROUND_Y, 0.0) * 5.0);
    float glowFalloffHot  = exp(-max(p.y - GROUND_Y, 0.0) * 15.0);
    vec3  smokeColor      = mix(smokeBase, glowColor, glowFalloffSoft * fireIntensity * 0.55);
    smokeColor += glowColor * glowFalloffHot * fireIntensity * 0.40;

    color = mix(color, smokeColor, smokeMask * 0.60);

    // S2 (Phase 2.1 corrective): a second, near-horizon smoke veil - a
    // separate FBM band, offset/drifting differently from the main smoke
    // pass above, masked to a narrow strip right at the fire line so it
    // partially occludes the fire front's top edge. This is what actually
    // "blends fire into background" rather than the fire sitting as a crisp
    // cutout in front of flat smoke - fire-underlit (reuses smokeColor, so it
    // stays tied to the same hue/heat as the rest of the smoke layer).
    vec2 veilUV = vec2(
        pWarped.x * 1.05 + uTime * 0.021,
        pWarped.y * 2.60 - uTime * 0.017
    );
    float veilN    = fbm2(veilUV, 2);
    float veilBand = smoothstep(GROUND_Y - 0.02, GROUND_Y + 0.02, p.y) *
                      (1.0 - smoothstep(GROUND_Y + 0.10, GROUND_Y + 0.14, p.y));
    float veilMask = smoothstep(0.30, 0.70, veilN) * veilBand;
    color = mix(color, smokeColor, veilMask * 0.30);

    // --- Ground / ridge silhouette (1D FBM heightline) ------------------------
    float ridgeN  = fbm2(vec2(p.x * 2.3 + 41.0, 7.0), 2);
    float ridgeY  = GROUND_Y - 0.05 + ridgeN * 0.055;
    float groundMask = smoothstep(ridgeY + 0.006, ridgeY - 0.006, p.y);
    vec3  groundColor = vec3(0.012, 0.012, 0.016);
    color = mix(color, groundColor, groundMask);

    // --- Foreground turbines (near-black silhouettes) -------------------------
    // Flat black, no shading variation - a nacelle highlight/tint was tried in
    // Phase 3 and removed same-day per user feedback ("looks cheap"); see
    // AUDIT.md Entry 39 addendum. Speed multipliers (1.00/1.18) match
    // WindTurbineFireScene.cs's FG1SpeedMul/FG2SpeedMul, for blade-blur only.
    vec3 fgColor = vec3(0.014, 0.015, 0.020);

    float mFG1 = turbineMask(p, -0.34, GROUND_Y, 1.00, uRotorAngleFG1, 0.005, 1.00);
    color = mix(color, fgColor, mFG1);

    float mFG2 = turbineMask(p, 0.30, GROUND_Y, 0.86, uRotorAngleFG2, 0.005, 1.18);
    color = mix(color, fgColor, mFG2);

    // Embers moved earlier (Phase 1.2, see the block right after the
    // background turbines above) so foreground ground/turbine silhouettes
    // here correctly occlude them, reinforcing "far off in the distance."

    // --- Final grade / vignette / readability guard ---------------------------
    // Vignette darkens the extreme corners slightly (elongated by the
    // aspect-corrected p, consistent with the rest of this pass).
    float vig = smoothstep(0.95, 0.30, length(p));
    color *= mix(0.72, 1.0, vig);

    // Readability guard: exposure only ever nudges up modestly with heat, so
    // even a fully "hot" frame stays dominated by dark sky/ground/silhouettes
    // rather than washing the whole frame warm - the horizon band + smoke tint
    // + embers above are the only elements that actually brighten with heat.
    float exposure = 0.92 + 0.10 * uSceneHeat;
    color *= exposure;

    fragColor = vec4(clamp(color, 0.0, 1.0), 1.0);
}
