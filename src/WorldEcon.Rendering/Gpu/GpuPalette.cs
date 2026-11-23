using WorldEcon.WorldGen.Pipeline;
using WorldEcon.WorldGen.Tiles;

namespace WorldEcon.Rendering.Gpu;

/// <summary>How the fragment shader colours a texel for a <see cref="GlobeLayer"/> (the <c>uMode</c> uniform).</summary>
public enum GpuLayerMode
{
    /// <summary>The cell's flat colour from the cell colour texture (layers without texel data: Plates, Currents, Winds, Areas).</summary>
    CellColour = 0,
    Biome = 1,
    Landform = 2,
    Elevation = 3,
    Temperature = 4,
    Precipitation = 5,
    Hydrology = 6,
}

/// <summary>
/// Colour lookup tables for the GPU: every texel colour of every layer comes from <see cref="LayerPalette"/> through a
/// row of <see cref="Lut"/>, so palettes stay defined in C# only (design 2026-11-23-gpu-map-rendering §6). Layer changes
/// need no CPU rebuild; only the per-cell colour texture of the <see cref="GpuLayerMode.CellColour"/> layers is rebuilt.
/// <see cref="Colour"/> is the C# mirror of the fragment shader's lookup (tests compare it with the CPU renderer).
/// </summary>
public sealed class GpuPalette
{
    /// <summary>Entries per LUT row.</summary>
    public const int Width = 2048;

    /// <summary>LUT rows (see the row constants).</summary>
    public const int Rows = 9;

    public const int RowBiome = 0;
    public const int RowLandform = 1;
    public const int RowElevationLand = 2;
    public const int RowElevationOcean = 3;
    public const int RowTemperature = 4;
    public const int RowPrecipitation = 5;
    public const int RowRiver = 6;
    public const int RowHydrologyWater = 7;

    /// <summary>River stroke colour by width level (0–31), as the CPU renderer's <c>StrokeRivers</c>.</summary>
    public const int RowRiverStroke = 8;

    /// <summary>Lowest and highest temperature on the ramp (deci-°C), as <see cref="LayerPalette.TemperatureColor"/>.</summary>
    public const int TemperatureMin = -300;
    public const int TemperatureMax = 400;

    /// <summary>Precipitation where the ramp ends (mm), as <see cref="LayerPalette.PrecipitationColor"/>.</summary>
    public const int PrecipitationMax = 4000;

    private GpuPalette(byte[] lut, int elevationMin, int elevationMax)
    {
        Lut = lut;
        ElevationMin = elevationMin;
        ElevationMax = elevationMax;
    }

    /// <summary>RGBA8, <see cref="Width"/> × <see cref="Rows"/>, row-major (R, G, B, A per entry).</summary>
    public byte[] Lut { get; }

    /// <summary>Elevation at LUT entry 0 of the elevation rows (m).</summary>
    public int ElevationMin { get; }

    /// <summary>Elevation at LUT entry <see cref="Width"/> − 1 of the elevation rows (m).</summary>
    public int ElevationMax { get; }

    /// <summary>The shader mode of <paramref name="layer"/>; matches the cases of the CPU renderer's <c>TexelColour</c>.</summary>
    public static GpuLayerMode ModeOf(GlobeLayer layer) => layer switch
    {
        GlobeLayer.Biome or GlobeLayer.Settlements => GpuLayerMode.Biome,
        GlobeLayer.Landform => GpuLayerMode.Landform,
        GlobeLayer.Elevation => GpuLayerMode.Elevation,
        GlobeLayer.Temperature => GpuLayerMode.Temperature,
        GlobeLayer.Precipitation => GpuLayerMode.Precipitation,
        GlobeLayer.Hydrology => GpuLayerMode.Hydrology,
        _ => GpuLayerMode.CellColour,
    };

    /// <summary>Builds the LUT for <paramref name="renderer"/>'s world (sea level, elevation range, river scale).</summary>
    public static GpuPalette Build(MapRenderer renderer)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        var w = renderer.World;
        var sea = w.Globe.SeaLevelM;
        int eMin = sea - 1, eMax = sea + 1;
        if (w.Imagery is { } img)
            foreach (var e in img.ElevationM)
            {
                if (e < eMin) eMin = e;
                if (e > eMax) eMax = e;
            }

        var lut = new byte[Width * Rows * 4];
        void Put(int row, int i, uint argb)
        {
            var o = ((row * Width) + i) * 4;
            lut[o] = (byte)(argb >> 16);
            lut[o + 1] = (byte)(argb >> 8);
            lut[o + 2] = (byte)argb;
            lut[o + 3] = 255;
        }

        for (var i = 0; i < 256; i++)
        {
            Put(RowBiome, i, LayerPalette.BiomeColor((Biome)i));
            Put(RowLandform, i, LayerPalette.LandformColor((Landform)i));
            Put(RowRiver, i, LayerPalette.HydrologyColor(WaterBodyKind.River, Biome.Ocean, renderer.RiverT(CellImagery.LevelToFlow((byte)i))));
        }

        for (var i = 0; i < Width; i++)
        {
            var e = ElevationAt(i, eMin, eMax);
            Put(RowElevationLand, i, LayerPalette.ElevationColor(e, sea, ocean: false));
            Put(RowElevationOcean, i, LayerPalette.ElevationColor(e, sea, ocean: true));
            Put(RowPrecipitation, i, LayerPalette.PrecipitationColor((int)Math.Round(i * (double)PrecipitationMax / (Width - 1))));
        }

        for (var t = TemperatureMin; t <= TemperatureMax; t++) Put(RowTemperature, t - TemperatureMin, LayerPalette.TemperatureColor((short)t));
        Put(RowHydrologyWater, 0, LayerPalette.HydrologyColor(WaterBodyKind.Lake, Biome.Ocean, 0));
        Put(RowHydrologyWater, 1, LayerPalette.HydrologyColor(WaterBodyKind.DryLakeBed, Biome.Ocean, 0));
        for (var b = 0; b < MapRenderer.RiverWidthLevels; b++) Put(RowRiverStroke, b, LayerPalette.RiverStrokeColor(b / (float)(MapRenderer.RiverWidthLevels - 1)));
        return new GpuPalette(lut, eMin, eMax);
    }

    /// <summary>Elevation (m) of elevation-row entry <paramref name="i"/>.</summary>
    public static int ElevationAt(int i, int eMin, int eMax) => (int)Math.Round(eMin + (i * (double)(eMax - eMin) / (Width - 1)));

    /// <summary>Elevation-row entry of <paramref name="elevationM"/>: floor(x + 0.5) in single precision, as the shader.</summary>
    public int ElevationIndex(int elevationM) =>
        ElevationMax <= ElevationMin ? 0 : Math.Clamp((int)Math.Floor(((elevationM - ElevationMin) * (float)(Width - 1) / (ElevationMax - ElevationMin)) + 0.5f), 0, Width - 1);

    /// <summary>Precipitation-row entry of <paramref name="mm"/>.</summary>
    public static int PrecipitationIndex(int mm) => (int)Math.Floor((Math.Clamp(mm, 0, PrecipitationMax) * (float)(Width - 1) / PrecipitationMax) + 0.5f);

    /// <summary>
    /// C# mirror of the fragment shader: the ARGB colour of a texel with the given class and value channels in
    /// <paramref name="mode"/> (<paramref name="cellColour"/> for <see cref="GpuLayerMode.CellColour"/>).
    /// </summary>
    public uint Colour(GpuLayerMode mode, byte biome, byte landform, byte water, byte flowLevel, short elevationM, short temperatureDeciC, int precipitationMm, uint cellColour)
    {
        uint At(int row, int i)
        {
            var o = ((row * Width) + i) * 4;
            return 0xFF000000u | ((uint)Lut[o] << 16) | ((uint)Lut[o + 1] << 8) | Lut[o + 2];
        }

        var kind = (TileWaterKind)water;
        return mode switch
        {
            GpuLayerMode.Biome => At(RowBiome, biome),
            GpuLayerMode.Landform => At(RowLandform, landform),
            GpuLayerMode.Elevation => At(kind == TileWaterKind.Ocean ? RowElevationOcean : RowElevationLand, ElevationIndex(elevationM)),
            GpuLayerMode.Temperature => At(RowTemperature, Math.Clamp((int)temperatureDeciC, TemperatureMin, TemperatureMax) - TemperatureMin),
            GpuLayerMode.Precipitation => At(RowPrecipitation, PrecipitationIndex(precipitationMm)),
            GpuLayerMode.Hydrology => kind switch
            {
                TileWaterKind.River => At(RowRiver, flowLevel),
                TileWaterKind.Lake => At(RowHydrologyWater, 0),
                TileWaterKind.DryLakeBed => At(RowHydrologyWater, 1),
                _ => At(RowBiome, biome),
            },
            _ => cellColour | 0xFF000000u,
        };
    }

    /// <summary>RGBA8 per-cell colours of <paramref name="layer"/> (<see cref="GpuScene.CellTextureWidth"/> wide), for the cell colour texture.</summary>
    public static byte[] CellColours(MapRenderer renderer, GlobeLayer layer, int rows)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        var colours = renderer.GlobeColoursFor(layer);
        var bytes = new byte[GpuScene.CellTextureWidth * rows * 4];
        for (var c = 0; c < colours.Length; c++)
        {
            bytes[(c * 4)] = (byte)(colours[c] >> 16);
            bytes[(c * 4) + 1] = (byte)(colours[c] >> 8);
            bytes[(c * 4) + 2] = (byte)colours[c];
            bytes[(c * 4) + 3] = 255;
        }

        return bytes;
    }
}
