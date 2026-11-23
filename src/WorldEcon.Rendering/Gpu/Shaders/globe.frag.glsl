// Globe cell colour: texel data from the atlas, colour from the palette LUT (GpuPalette mirrors this in C#).

uniform highp usampler2DArray uClass;  // biome, landform, water kind, flow level
uniform highp isampler2DArray uValue;  // elevation m, temperature deci-C, precipitation mm, 0
uniform sampler2D uCellColour;         // per-cell colour of the active layer (CellColour mode)
uniform highp usampler2D uCellFlags;   // bit 0: highlighted
uniform int uMode;                     // GpuLayerMode
uniform int uHasAtlas;

in vec2 vUv;
flat in uint vCell;
flat in float vShade;

out vec4 fragColour;

const int CELLS_PER_ROW = 128;    // GpuScene.CellsPerRow
const int CELLS_PER_LAYER = 16384; // GpuScene.CellsPerLayer
const int TEXELS = 16;             // CellImagery.Size
const int CELL_TEX_W = 2048;       // GpuScene.CellTextureWidth
#include "palette.glsl"

void main()
{
    int cell = int(vCell);
    ivec2 cellTex = ivec2(cell % CELL_TEX_W, cell / CELL_TEX_W);
    vec3 col;
    if (uMode == 0 || uHasAtlas == 0)
    {
        col = texelFetch(uCellColour, cellTex, 0).rgb;
    }
    else
    {
        int i = clamp(int(floor(vUv.x)), 0, TEXELS - 1);
        int j = clamp(int(floor(vUv.y)), 0, TEXELS - 1);
        int local = cell % CELLS_PER_LAYER;
        ivec3 a = ivec3((local % CELLS_PER_ROW) * TEXELS + i, (local / CELLS_PER_ROW) * TEXELS + j, cell / CELLS_PER_LAYER);
        col = texelColour(uMode, texelFetch(uClass, a, 0), texelFetch(uValue, a, 0), vec3(0.0));
    }

    col *= vShade;
    if ((texelFetch(uCellFlags, cellTex, 0).r & 1u) != 0u) col = mix(col, vec3(1.0, 200.0 / 255.0, 40.0 / 255.0), 0.55);
    fragColour = vec4(col, 1.0);
}
