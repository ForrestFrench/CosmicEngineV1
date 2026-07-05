#version 330 core
in vec2 vUV;
out vec4 fragColor;

// Diagnostic-only shader: proves the shader/uniform/quad pipeline can
// produce visible output, independent of any world's own shader.
// Deliberately has no uniforms and no dependency on world state.
void main()
{
    fragColor = vec4(vUV.x, vUV.y, 1.0 - vUV.x, 1.0);
}
