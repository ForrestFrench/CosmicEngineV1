#version 400 core
// Phase 1 Hybrid Proof composite shader - deliberately minimal per architect scope:
// one blend uniform, no blend modes, no effect stack.
in vec2 vUv;
out vec4 FragColor;

uniform sampler2D uVideoTex;
uniform sampler2D uChildTex;
uniform float uBlend;      // 0 = pure video, 1 = pure child (procedural) world
uniform int   uVideoReady; // 0 if the decoder failed to open (VisionBoard clip missing/ffmpeg absent)
uniform float uTime;

void main()
{
    vec3 childColor = texture(uChildTex, vUv).rgb;

    vec3 videoColor;
    if (uVideoReady == 1)
    {
        videoColor = texture(uVideoTex, vUv).rgb;
    }
    else
    {
        // Decoder-unavailable fallback: a dim, clearly-synthetic dark-teal field (not black, not
        // magenta-placeholder - just enough to visually communicate "no clip loaded" without being
        // mistaken for a real video frame) so the composite still demonstrates the child-world/
        // blend machinery even when ffmpeg or the clip is unavailable.
        videoColor = vec3(0.02, 0.05, 0.06) + 0.01 * sin(uTime);
    }

    FragColor = vec4(mix(videoColor, childColor, clamp(uBlend, 0.0, 1.0)), 1.0);
}
