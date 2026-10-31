#version 450

// Atlas texture: RGBA unified tile atlas
layout(set = 3, binding = 0) uniform texture2D AtlasTexture;
layout(set = 3, binding = 1) uniform sampler AtlasSampler;

layout(location = 0) in vec2 fragBaseAtlasUV;
layout(location = 1) in vec2 fragOverlayAtlasUV;
layout(location = 2) in float fragHasOverlay;
layout(location = 6) in vec3 fragVertexLightColor;
layout(location = 0) out vec4 outColor;

void main() {
    // Sample base tile from atlas
    vec4 baseColor = texture(sampler2D(AtlasTexture, AtlasSampler), fragBaseAtlasUV);

    // Composite overlay on top of base (alpha blend)
    if (fragHasOverlay > 0.5) {
        vec4 overlayColor = texture(sampler2D(AtlasTexture, AtlasSampler), fragOverlayAtlasUV);
        baseColor = mix(baseColor, overlayColor, overlayColor.a);
    }

    // Apply smooth vertex lighting (interpolated from vertex shader)
    outColor = baseColor * vec4(fragVertexLightColor, 1.0);
}
