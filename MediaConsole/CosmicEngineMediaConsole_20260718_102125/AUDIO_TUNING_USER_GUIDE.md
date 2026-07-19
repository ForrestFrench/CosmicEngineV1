# Cosmic Engine Audio Tuning User Guide

## Purpose

The **Audio Tuning** tab is a discovery environment for answering one question: **Does playing guitar feel like controlling a visual instrument?** It reuses Cosmic Engine's existing capture, analyzer, calibration, smoothing, and modulation pipeline. It does not capture audio itself and it does not introduce a second analyzer.

Audio modulation remains separate from effect presets and never changes clip selection.

## Launch

1. Double-click `START_MEDIA_CONSOLE.command` (or `start_mediaConsole.command`). The launcher now checks and starts the existing Cosmic Engine audio core automatically before opening the Media Console.

2. Open the Media Console address printed by the launcher.
3. Select **Audio Tuning** in the top navigation.

The audio badge should read **AUDIO LIVE**. **CAPTURE IDLE** means the core is reachable but audio capture is not active. **CORE OFFLINE** means the Media Console is safely showing neutral audio values; video and base effects continue to work.

## Screen layout

The left side shows an approved video atom with the currently authored effect state plus temporary audio modulation. The right side contains the tuner.

### Live input monitor

Each guitar shows:

- Signal level: calibrated 0–1 input value.
- Raw RMS: existing analyzer energy before response shaping.
- Attack: transient emphasis.
- Sustain: held-note energy.
- State: attack, sustain, active, or inactive.
- Silence: whether that input is below its calibrated gate.
- Visual-control gain: multiplies the normalized control signal from 0× to 32× using the existing calibration layer. It does not change or repair the captured audio.

The combined area shows overall performance energy and both-guitar interaction. The **Live Effect Response** row shows the final modulated values reaching Liquid Warp, Edge Glow, Mirror blend, and Saturation.

### Effect mapping controls

Four mappings are available:

- Liquid Warp
- Edge Glow
- Mirror
- Color / Saturation

Each mapping has a selectable source:

- Guitar A
- Guitar B
- Combined energy
- Attack
- Sustain

Each mapping also provides:

- Enable/disable
- Sensitivity
- Response speed
- Smoothing
- Maximum contribution
- Decay time
- Dead zone / noise threshold

Changes save automatically. A mapping's live contribution appears at the right side of its heading.

## Recommended tuning workflow

1. Begin with audio modulation enabled and all maximum-contribution controls low.
2. Stop playing. Raise each dead zone until meters can move without causing visible effect movement.
3. Play Guitar A only. If the Clarett is healthy but the calibrated signal is too low, raise **Visual-control gain** until ordinary playing reaches a useful range. If the interface's own clip light appears, reduce the Clarett preamp first; software control gain cannot repair clipped audio.
4. Verify level, attack, sustain, and the **SHAPING VISUALS** indicator, then tune one mapping at a time.
5. Play Guitar B only and repeat.
6. Play both guitars. Adjust Combined Energy mappings until the visual language becomes richer without jumping.
7. Stop. Increase decay and smoothing until the image returns naturally to its authored base state.
8. Toggle **Enabled** off. The image should immediately use only the base effect state. Toggle it on to continue.
9. Save a named tuning profile when the response feels coherent.

The guided input test marks Guitar A, Guitar B, both-guitar interaction, and the final silent decay as they are observed.

## Profile ideas

- **Clean Guitar Profile:** modest Edge Glow, low color contribution, short attack decay.
- **Heavy Riff Profile:** stronger Combined Energy mapping with a conservative dead zone and controlled maximums.
- **Ambient Sustain Profile:** Sustain driving Liquid Warp with slower response, heavier smoothing, and a long decay.

Profiles are stored in `data/AUDIO_TUNING_PRESETS.json`, separate from `data/EFFECT_PRESETS.json`. Loading a tuning profile never overwrites an effect preset.

## Musical tuning guidance

- If the visual flickers, raise the dead zone and smoothing before increasing sensitivity.
- If it feels disconnected, shorten response speed slightly.
- If every note causes a hard visual hit, reduce maximum contribution and lengthen decay.
- If silence still moves the image, raise the dead zone for the responsible mapping.
- If meters move but effects do not, look at the mapping contribution values and **LIVE EFFECT RESPONSE** status. **BELOW MAPPING THRESHOLDS** means the calibrated signal has not cleared the selected mapping's dead zone. **SHAPING VISUALS** confirms a nonzero contribution is reaching the effects.
- Prefer slow sustain-driven evolution and restrained attack emphasis over note-for-note flashing.

## Boundaries and limitations

- Only Liquid Warp, Edge Glow, Mirror, and Color / Saturation are audio-modulated.
- The clip timeline remains independent; notes never select clips.
- The interface polls the existing read-only response endpoint at approximately 20 Hz.
- The current capture architecture exposes the existing stereo input path as Guitar A and Guitar B.
- On the Clarett, the current OpenAL capture path exposes **Input 1 (Left)** and **Input 2 (Right)** only. A guitar connected to physical input 3 or 4 will not appear unless Focusrite Control routes it into the host stereo 1/2 capture stream. Selecting Clarett as the macOS input device alone does not perform that routing.
- Real two-guitar playing is still required for artistic tuning. The built-in calibration test pulse is useful for technical validation but cannot reproduce real RMS, spectral detail, or playing feel.
- This remains a tuning environment, not permanent live-performance integration.

## Core-offline troubleshooting

A browser tab can remain open after the Cosmic Engine process on port 8080 has stopped. In that state, the old calibration dashboard may look frozen while its repeated `/calibration/status` requests fail with connection refused. A visible page is not proof that the audio process is alive.

The Media Console now detects this condition, clears stale audio values, and shows **CORE OFFLINE**. Its one-click launcher checks `http://localhost:8080/audio/reactivity` and starts the existing dashboard-only Cosmic Engine process when needed. If the core stops later, run `START_MEDIA_CONSOLE.command` again and consult `/private/tmp/cosmic_audio_core.log` if it cannot restart.

## Validation completed

- Media Console and Audio Tuning tab launched successfully.
- Existing audio core reported live capture through the Clarett 4Pre USB.
- Approved atom playback and visual effects continued in the tuning view.
- Live meter and effect-value bindings were present and updating.
- Effect-centered source selections and all requested controls rendered.
- A named tuning profile saved and survived dashboard reload.
- Audio disable returned the live effect output to the base effect values.
- Automated tests confirmed neutral behavior, noise-floor gating, effect scope, bounded modulation, persistence, and no mutation of base effect presets.
