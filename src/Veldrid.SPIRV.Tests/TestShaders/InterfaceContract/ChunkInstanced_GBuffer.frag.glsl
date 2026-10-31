#version 450

// TileMapKit_ShadingMode.GBufferMRT fragment stage (spec 30 D6 step 3): the tile plane written into an AN.RenderKit GBuffer so
// DeferredLightingPass lights it (sun / PointLightGpu / AccumulateLightTexture) and multiplies by albedo once in Compose.
// Pairs with ChunkInstanced.vert.glsl (the shared vertex shader). fragVertexLightColor is an input because the vertex
// shader always emits it; this stage ignores it (set 4 holds the renderer's constant white texture).
//
// GBuffer layout (RenderKit Targets/GBuffer.cs):
//   RT0 rgb = albedo, a = roughness          RT1 rgb = worldNormal * 0.5 + 0.5, a = metalness
//   RT2 rgb = emissive (0: tiles are lit, never glow - spec 30 Q4), a = shadow-exempt mask (0 = receives sun shadows)

// Atlas texture: RGBA unified tile atlas
layout(set = 3, binding = 0) uniform texture2D AtlasTexture;
layout(set = 3, binding = 1) uniform sampler AtlasSampler;

// GBufferMRT only: the tile plane's world normal toward the camera (ITileWorldProjection.PlaneNormal, spec 30 Q1)
layout(set = 5, binding = 0) uniform TilePlaneParams {
    vec4 PlaneNormal;         // xyz = unit normal, w unused (std140 = 48 bytes total; mirrors TilePlaneParamsUniform)
    vec4 GridOverlayColour;   // rgba of the lattice outline (TileGridOverlay)
    vec4 GridOverlayParams;   // x = TileGridOverlay_Mode (0 none, 1 square tiles, 2 hex rows), y = line width in tiles
};
layout(set = 5, binding = 1) uniform texture2D GridSquareOverrides;
layout(set = 5, binding = 2) uniform sampler GridOverrideSampler;

layout(location = 0) in vec2 fragBaseAtlasUV;
layout(location = 1) in vec2 fragOverlayAtlasUV;
layout(location = 2) in float fragHasOverlay;
layout(location = 3) in vec2 fragTileUV;           // TileGridOverlay: 0..1 across THIS tile
layout(location = 4) in float fragTileRowParity;   // TileGridOverlay: 0 = even world tile row, 1 = odd (constant per tile)
layout(location = 5) flat in vec2 fragGridTile;    // world tile index, constant across the quad
layout(location = 6) in vec3 fragVertexLightColor; // unused in this mode; kept last to match the shared vertex interface

layout(location = 0) out vec4 out_AlbedoRoughness;
layout(location = 1) out vec4 out_NormalMetalness;
layout(location = 2) out vec4 out_Emissive;

// ---- TileGridOverlay (ArtificialNecessity.TileMapKit.Rendering.TileGridOverlay) ----
// Local UV edge distances only. No world-coordinate search or Voronoi candidates.
// Convert a UV displacement into screen pixels using the local derivative Jacobian.
vec2 uvToPixels(vec2 v, vec2 dx, vec2 dy) {
    float det = dx.x * dy.y - dy.x * dx.y;
    return vec2(dy.y * v.x - dy.x * v.y, -dx.y * v.x + dx.x * v.y) / det;
}

float segmentPixels(vec2 p, vec2 a, vec2 b, vec2 dx, vec2 dy) {
    vec2 pa = uvToPixels(p - a, dx, dy);
    vec2 ba = uvToPixels(b - a, dx, dy);
    float t = clamp(dot(pa, ba) / max(dot(ba, ba), 1e-8), 0.0, 1.0);
    return length(pa - ba * t);
}

float gridOverlayMask(vec2 p, float rowParity, float mode, float linePx) {
    // Derivatives BEFORE wrapping/folding: avoid artificial spikes at cell seams.
    vec2 dx = dFdx(p), dy = dFdy(p);
    float edgePx;
    if (mode < 1.5) {
        vec2 edgeUV = min(p, vec2(1.0) - p);
        vec2 uvPerPixel = max(vec2(length(vec2(dx.x, dy.x)), length(vec2(dx.y, dy.y))), vec2(1e-6));
        edgePx = min(edgeUV.x / uvPerPixel.x, edgeUV.y / uvPerPixel.y);
    } else {
        // Tile-aligned pointy hexes: width 1, height 4/3, row stride 1.
        // Even rows centre at u=.5; odd rows centre at u=0 and u=1.
        vec2 q = vec2(fract(p.x - 0.5 * rowParity) - 0.5, p.y - 0.5);
        vec2 s = vec2(q.x < 0.0 ? -1.0 : 1.0, q.y < 0.0 ? -1.0 : 1.0);
        q = abs(q); dx *= s; dy *= s;
        // Symmetry folds six edges into two segments: vertical side and sloping shoulder.
        edgePx = min(segmentPixels(q, vec2(0.5, 0.0), vec2(0.5, 1.0/3.0), dx, dy),
                     segmentPixels(q, vec2(0.5, 1.0/3.0), vec2(0.0, 2.0/3.0), dx, dy));
    }
    float halfWidth = max(linePx, 0.0) * 0.5;
    return 1.0 - smoothstep(max(halfWidth - 0.5, 0.0), halfWidth + 0.5, edgePx);
}

void main() {
    // Sample base tile from atlas
    vec4 baseColor = texture(sampler2D(AtlasTexture, AtlasSampler), fragBaseAtlasUV);

    // Composite overlay on top of base (alpha blend)
    if (fragHasOverlay > 0.5) {
        vec4 overlayColor = texture(sampler2D(AtlasTexture, AtlasSampler), fragOverlayAtlasUV);
        baseColor = mix(baseColor, overlayColor, overlayColor.a);
    }

    // Alpha cutout, same rule as RenderKit's GBuffer.frag: a GBuffer has no blending, a transparent texel writes NOTHING
    // (albedo, normal, emissive AND depth all stay as they were).
    if (baseColor.a < 0.5) discard;

    // Lighten the existing albedo along grid edges; no emission, so darkness and lighting still apply.
    if (GridOverlayParams.x > 0.5) {
        float mode = GridOverlayParams.x;
        ivec2 tile = ivec2(fragGridTile);
        ivec2 mapSize = textureSize(sampler2D(GridSquareOverrides, GridOverrideSampler), 0);
        bool hidden = false;
        if (all(greaterThanEqual(tile, ivec2(0))) && all(lessThan(tile, mapSize))) {
            int tileOverride = int(round(texelFetch(sampler2D(GridSquareOverrides, GridOverrideSampler), tile, 0).r * 255.0));
            hidden = tileOverride == 1;
            if (mode > 1.5 && tileOverride == 255) mode = 1.0;
        }
        float mask = gridOverlayMask(fragTileUV, fragTileRowParity, mode, GridOverlayParams.y);
        if (hidden) mask = 0.0;
        const float GridAlbedoLighteningFactor = 1.6;
        baseColor.rgb = min(baseColor.rgb * mix(1.0, GridAlbedoLighteningFactor, mask), vec3(1.0));
    }

    out_AlbedoRoughness = vec4(baseColor.rgb, 0.8);                      // roughness 0.8 = RenderKit's mesh default
    out_NormalMetalness = vec4(normalize(PlaneNormal.xyz) * 0.5 + 0.5, 0.0);
    out_Emissive = vec4(0.0, 0.0, 0.0, 0.0);
}