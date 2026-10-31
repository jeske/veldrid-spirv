#version 450

// Atlas texture: RGBA unified tile atlas
layout(set = 3, binding = 0) uniform texture2D AtlasTexture;
layout(set = 3, binding = 1) uniform sampler AtlasSampler;

layout(location = 0) in vec2 fragBaseAtlasUV;
layout(location = 1) in vec2 fragOverlayAtlasUV;
layout(location = 2) in float fragHasOverlay;
// The FULL vertex interface must be declared even where unused: the cross-compiled (SPIRV -> HLSL) interstage link binds by
// declaration order, so a hole at 3..5 made location 6 receive fragTileUV (rainbow tiles, 2026-10-31). Mirrors ChunkInstanced_GBuffer.frag.
layout(location = 3) in vec2 fragTileUV;           // TileGridOverlay (GBuffer only)
layout(location = 4) in float fragTileRowParity;   // TileGridOverlay (GBuffer only)
layout(location = 5) flat in vec2 fragGridTile;    // TileGridOverlay (GBuffer only)
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