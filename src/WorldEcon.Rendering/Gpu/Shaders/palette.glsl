// Shared palette lookup (included by globe.frag and tile.frag through GpuShaders' #include step).
// GpuPalette.Colour is the C# mirror; the LUT rows are GpuPalette's Row* constants.

uniform sampler2D uPalette; // GpuPalette.Lut, 2048 x 9
uniform int uElevMin;
uniform int uElevMax;

const int PAL_LAST = 2047; // GpuPalette.Width - 1

vec3 pal(int row, int i)
{
    return texelFetch(uPalette, ivec2(i, row), 0).rgb;
}

// Colour of a texel or tile in GpuLayerMode `mode` from its class (biome, landform, water kind, flow level) and value
// (elevation m, temperature deci-C, precipitation mm) channels; `cellColour` for mode 0 (layers without texel data).
vec3 texelColour(int mode, uvec4 k, ivec4 v, vec3 cellColour)
{
    int water = int(k.z); // TileWaterKind: 0 none, 1 river, 2 lake, 3 dry lake bed, 4 ocean
    if (mode == 1) return pal(0, int(k.x));
    if (mode == 2) return pal(1, int(k.y));
    if (mode == 3)
    {
        int idx = 0;
        if (uElevMax > uElevMin) idx = clamp(int(floor(float(v.x - uElevMin) * float(PAL_LAST) / float(uElevMax - uElevMin) + 0.5)), 0, PAL_LAST);
        return pal(water == 4 ? 3 : 2, idx);
    }
    if (mode == 4) return pal(4, clamp(v.y, -300, 400) + 300);
    if (mode == 5) return pal(5, int(floor(float(clamp(v.z, 0, 4000)) * float(PAL_LAST) / 4000.0 + 0.5)));
    if (mode == 6)
    {
        if (water == 1) return pal(6, int(k.w));
        if (water == 2) return pal(7, 0);
        if (water == 3) return pal(7, 1);
        return pal(0, int(k.x));
    }
    return cellColour;
}

// Continuous layers (elevation, temperature, precipitation) blend within a water kind; the others are categorical.
bool isContinuous(int mode)
{
    return mode == 3 || mode == 4 || mode == 5;
}

// The class key of the CPU renderer's BuildTileKeys: the water kind (land 0, ocean 1, lake 2) for continuous layers,
// else the colour itself (packed).
uint texelKey(int mode, uvec4 k, vec3 colour)
{
    if (isContinuous(mode)) return k.z == 4u ? 1u : k.z == 2u ? 2u : 0u;
    uvec3 c = uvec3(floor(colour * 255.0 + 0.5));
    return (c.r << 16) | (c.g << 8) | c.b;
}
