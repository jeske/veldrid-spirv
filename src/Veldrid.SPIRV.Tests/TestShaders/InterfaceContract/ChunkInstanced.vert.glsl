#version 450

layout(set = 0, binding = 0) uniform ProjectionBuffer {
    mat4 Projection;
};

layout(set = 1, binding = 0) uniform ChunkOriginBuffer {
    float ChunkOriginWorldPixelX;
    float ChunkOriginWorldPixelY;
    float ChunkSizeInTiles;
    float AtlasWidthPixels;
    float AtlasHeightPixels;
    float PixelsPerTile;            // atlas layout (spec 10 D3) - see TilePalette
    float AtlasSlotSizePixels;
    float AtlasStampPaddingPixels;
    float AtlasStampsPerRow;
    float _padding1;
    float _padding2;
    float _padding3;
};

// Chunk stamp textures: R16_UNorm format, 64x64, each texel = TileStampId (normalized 0-1)
layout(set = 2, binding = 0) uniform texture2D BaseLayerStampTexture;
layout(set = 2, binding = 1) uniform texture2D OverlayLayerStampTexture;
layout(set = 2, binding = 2) uniform sampler StampSampler;

// Screen-space light texture and parameters (set 4)
layout(set = 4, binding = 0) uniform TileLightingParams {
    float AmbientLightLevel;
    float _lightPad1;
    float LightBufferOriginWorldTileX;
    float LightBufferOriginWorldTileY;
    float LightBufferWidthInTiles;
    float LightBufferHeightInTiles;
    float _lightPad2;
    float _lightPad3;
};
layout(set = 4, binding = 1) uniform texture2D LightTexture;
layout(set = 4, binding = 2) uniform sampler LightSampler;

layout(location = 0) in vec2 CornerOffset;   // unit fraction of the tile (0|1, 0|1) - scaled by PixelsPerTile below
layout(location = 1) in vec4 Color;

layout(location = 0) out vec2 fragBaseAtlasUV;
layout(location = 1) out vec2 fragOverlayAtlasUV;
layout(location = 2) out float fragHasOverlay;
layout(location = 3) out vec2 fragTileUV;          // TileGridOverlay: 0..1 across this tile
layout(location = 4) out float fragTileRowParity;  // TileGridOverlay: 0 = even world tile row, 1 = odd
layout(location = 5) flat out vec2 fragGridTile;   // integer world tile coordinate for presentation overrides
layout(location = 6) out vec3 fragVertexLightColor; // forward lighting only; unused by GBuffer, kept last

// Compute atlas UV from a stampId using the deterministic atlas layout. The four layout numbers come from
// TilePalette through ChunkOriginBuffer (spec 10 D3) - this function must stay a mirror of TilePalette.AtlasStampPixelOrigin.
vec2 stampIdToAtlasUV(uint sid, float cornerU, float cornerV) {
    int stampsPerRow = int(AtlasStampsPerRow);
    int gx = int(sid) % stampsPerRow;
    int gy = int(sid) / stampsPerRow;
    float px = float(gx) * AtlasSlotSizePixels + AtlasStampPaddingPixels;
    float py = float(gy) * AtlasSlotSizePixels + AtlasStampPaddingPixels;

    return vec2(
        (px + cornerU * PixelsPerTile) / AtlasWidthPixels,
        (py + cornerV * PixelsPerTile) / AtlasHeightPixels
    );
}

// Sample the light texture at a world tile corner, averaging a 2x2 area (Terraria-style smooth lighting)
vec3 SampleLightForVertex(vec2 worldTileCorner) {
    vec3 lightSum = vec3(0.0);
    for (int dy = 0; dy <= 1; dy++) {
        for (int dx = 0; dx <= 1; dx++) {
            vec2 sampleTile = worldTileCorner + vec2(float(dx), float(dy));
            vec2 bufferLocalPos = sampleTile - vec2(LightBufferOriginWorldTileX, LightBufferOriginWorldTileY);
            vec2 lightUV = (bufferLocalPos + vec2(0.5)) / vec2(LightBufferWidthInTiles, LightBufferHeightInTiles);
            lightUV = clamp(lightUV, vec2(0.0), vec2(1.0));
            lightSum += texture(sampler2D(LightTexture, LightSampler), lightUV).rgb;
        }
    }
    return lightSum * 0.25;
}

void main() {
    // Compute tile position from instance index
    int chunkSize = int(ChunkSizeInTiles);
    int localTileX = int(gl_InstanceIndex) % chunkSize;
    int localTileY = int(gl_InstanceIndex) / chunkSize;

    // Sample stamp texture to get tile stamp ID
    vec2 stampUV = (vec2(float(localTileX), float(localTileY)) + vec2(0.5)) / vec2(ChunkSizeInTiles);
    float stampIdFloat = texture(sampler2D(BaseLayerStampTexture, StampSampler), stampUV).r;
    uint stampId = uint(stampIdFloat * 65535.0 + 0.5);

    // Air tiles (stampId == 0) become degenerate quads
    if (stampId == 0u) {
        gl_Position = vec4(0.0);
        fragBaseAtlasUV = vec2(0.0);
        fragOverlayAtlasUV = vec2(0.0);
        fragHasOverlay = 0.0;
        fragVertexLightColor = vec3(0.0);
        fragTileUV = vec2(0.0);
        fragGridTile = vec2(0.0);
        fragTileRowParity = 0.0;
        return;
    }

    // Corner fraction: the vertex buffer holds unit fractions (0,0), (1,0), (1,1), (0,1)
    float cornerU = CornerOffset.x;  // 0 or 1
    float cornerV = CornerOffset.y;  // 0 or 1

    // Base layer atlas UV
    fragBaseAtlasUV = stampIdToAtlasUV(stampId, cornerU, cornerV);

    // Overlay layer: sample overlay stamp texture
    float overlayStampIdFloat = texture(sampler2D(OverlayLayerStampTexture, StampSampler), stampUV).r;
    uint overlayStampId = uint(overlayStampIdFloat * 65535.0 + 0.5);

    if (overlayStampId != 0u) {
        fragOverlayAtlasUV = stampIdToAtlasUV(overlayStampId, cornerU, cornerV);
        fragHasOverlay = 1.0;
    } else {
        fragOverlayAtlasUV = vec2(0.0);
        fragHasOverlay = 0.0;
    }

    // Compute world tile position for this vertex corner
    // ChunkOriginWorldPixel is in pixels, divide by PixelsPerTile to get tile coords
    float worldTileX = ChunkOriginWorldPixelX / PixelsPerTile + float(localTileX);
    float worldTileY = ChunkOriginWorldPixelY / PixelsPerTile + float(localTileY);

    // Each corner samples a different 2x2 area for smooth lighting
    // cornerU/cornerV: 0=top-left, 1=bottom-right
    // Offset: TL corner → (-1,-1), TR → (0,-1), BR → (0,0), BL → (-1,0)
    vec2 lightCornerOffset = vec2(cornerU - 1.0, cornerV - 1.0);
    vec2 cornerWorldTilePos = vec2(worldTileX, worldTileY) + lightCornerOffset;
    fragVertexLightColor = SampleLightForVertex(cornerWorldTilePos);

    // World position = chunk origin + tile offset + corner offset
    float worldX = ChunkOriginWorldPixelX + (float(localTileX) + cornerU) * PixelsPerTile;
    float worldY = ChunkOriginWorldPixelY + (float(localTileY) + cornerV) * PixelsPerTile;
    // TileGridOverlay (GBuffer stage): the tile's own UV (0..1 across the quad) and the parity of its WORLD tile row (0 even / 1 odd),
    // constant per quad - the lattice is drawn in TILE-LOCAL space (user, 2026-10-30), never from world coordinates.
    fragTileUV = vec2(cornerU, cornerV);
    fragGridTile = vec2(worldTileX, worldTileY);
    fragTileRowParity = mod(floor(worldTileY + 0.5), 2.0);

    gl_Position = Projection * vec4(worldX, worldY, 0.0, 1.0);
}