using System.Runtime.CompilerServices;
using WorldEcon.Simulation.Random;
using WorldEcon.WorldGen.Pipeline;

namespace WorldEcon.Rendering;

/// <summary>Maps a cell of a <see cref="WorldLayers"/> to an opaque ARGB colour (0xAARRGGBB) for a <see cref="GlobeLayer"/>.</summary>
public static class LayerPalette
{
    private const uint NoArea = 0xFF3C3C3C;

    // Hydrology colours. River cells: teal, light (smallest river flow in the world) to dark (largest); river edges are
    // stroked over them in light to saturated cyan on the same scale. Lake: saturated blue, distinct from the Biome
    // layer's Lake and Ocean colours. Dry lake bed: rust, distinct from Ice, Desert and every other biome colour.
    private const uint RiverCellLow = 0xFF6FC2C8;
    private const uint RiverCellHigh = 0xFF1F7F96;
    private const uint RiverEdgeLow = 0xFFC8FFFF;
    private const uint RiverEdgeHigh = 0xFF00D8FF;
    private const uint HydrologyLake = 0xFF1A3CFF;
    private const uint HydrologyDryLakeBed = 0xFFB4502A;

    private static readonly uint[] BiomeColors =
    [
        0xFF1F4E8C, // Ocean
        0xFF3F7FD0, // Lake
        0xFFF2F6F8, // Ice
        0xFFA9B8A0, // Tundra
        0xFF2F6B55, // Taiga
        0xFF3E8E41, // TemperateForest
        0xFF9CCB5B, // Grassland
        0xFFB5A55A, // Shrubland
        0xFFE0C98A, // Desert
        0xFFCFB84A, // Savanna
        0xFF1B7A2E, // TropicalForest
        0xFF4F7A5A, // Swamp
        0xFF8A7F78, // Mountain
        0xFF9A8B5B, // Hills
    ];

    private static readonly ConditionalWeakTable<WorldLayers, int[]> AreaOwners = new();

    private static readonly ConditionalWeakTable<WorldLayers, RiverRange> RiverRanges = new();

    /// <summary>log10 of the smallest and largest flow over the world's river cells and river edges (0, 0 when there are none).</summary>
    private sealed record RiverRange(double LogMin, double LogMax);

    /// <summary>Colour of a cell whose layer has no data yet (a world part-way through generation, in the stage viewer).</summary>
    public const uint NoData = NoArea;

    /// <summary>
    /// Colour of <paramref name="cell"/> on <paramref name="layer"/>. A layer whose data a partly generated world does not
    /// have yet is <see cref="NoData"/>; the Currents and Winds base map falls back from biomes to elevation, then to
    /// <see cref="NoData"/>.
    /// </summary>
    public static uint ColorOf(WorldLayers l, GlobeLayer layer, int cell) => layer switch
    {
        GlobeLayer.Plates => l.PlateOf.Length == 0 ? NoData : HashedHue((ulong)l.PlateOf[cell]),
        GlobeLayer.Elevation => l.ElevationM.Length == 0 ? NoData : Elevation(l, cell),
        GlobeLayer.Temperature => l.TemperatureDeciC.Length == 0 ? NoData : TemperatureColor(l.TemperatureDeciC[cell]),
        GlobeLayer.Precipitation => l.PrecipitationMm.Length == 0 ? NoData : PrecipitationColor(l.PrecipitationMm[cell]),
        GlobeLayer.Hydrology => l.WaterBodyKind.Length == 0 || l.BiomeOf.Length == 0 ? NoData : Hydrology(l, cell),
        GlobeLayer.Areas => Areas(l, cell),
        GlobeLayer.Landform => l.ElevationM.Length == 0 ? NoData : LandformColor(l.IsOcean(cell) ? Landform.Ocean : MainLandform(l, cell)),
        GlobeLayer.CrustAge => l.Crust is { } ca && cell < ca.Count ? CrustAgeColor(ca.Kind[cell], ca.FormedAgeMy[cell]) : NoData,
        GlobeLayer.Orogeny => l.Crust is { } og && cell < og.Count ? OrogenyColor(og.Orogeny[cell], og.LastOrogenyAgeMy[cell]) : NoData,
        GlobeLayer.Sediment => l.Crust is { } sd && cell < sd.Count ? Lerp(0xFF20242C, 0xFFE8C468, Math.Clamp(sd.SedimentDam[cell] / 500.0, 0, 1)) : NoData,
        GlobeLayer.Currents or GlobeLayer.Winds when l.BiomeOf.Length == 0 => l.ElevationM.Length == 0 ? NoData : Elevation(l, cell),
        _ => l.BiomeOf.Length == 0 ? NoData : BiomeColor(l, cell), // Biome, Settlements, and the Currents/Winds base map
    };

    /// <summary>Orogeny layer colour of a cell without mountain building.</summary>
    public const uint NoOrogeny = 0xFF2E3440;

    /// <summary>Oceanic crust red (new, at a ridge) → deep blue (200 My); continental and arc crust sand (young) → dark brown (3,000 My).</summary>
    public static uint CrustAgeColor(WorldEcon.WorldGen.Tectonics.CrustKind kind, ushort ageMy) =>
        kind == WorldEcon.WorldGen.Tectonics.CrustKind.Oceanic
            ? Lerp(0xFFE04030, 0xFF102A70, Math.Clamp(ageMy / 200.0, 0, 1))
            : Lerp(0xFFE8D8A0, 0xFF4A3018, Math.Clamp(ageMy / 3000.0, 0, 1));

    /// <summary>Himalayan magenta, Andean orange, arc cyan, fading toward grey over 300 My; none <see cref="NoOrogeny"/>.</summary>
    public static uint OrogenyColor(WorldEcon.WorldGen.Tectonics.OrogenyKind kind, ushort ageMy)
    {
        if (kind == WorldEcon.WorldGen.Tectonics.OrogenyKind.None || ageMy == WorldEcon.WorldGen.Tectonics.CrustRecord.Never) return NoOrogeny;
        var c = kind switch
        {
            WorldEcon.WorldGen.Tectonics.OrogenyKind.Himalayan => 0xFFE040C0u,
            WorldEcon.WorldGen.Tectonics.OrogenyKind.Andean => 0xFFF09030u,
            _ => 0xFF40D0E0u,
        };
        return Lerp(c, 0xFF606670, Math.Clamp(ageMy / 300.0, 0, 1));
    }

    /// <summary>Landform with the largest composition share of a globe cell (ties: first entry); Plain when the cell has no composition.</summary>
    public static Landform MainLandform(WorldLayers l, int cell)
    {
        if (cell >= l.Composition.Length || l.Composition[cell] is not { Length: > 0 } entries) return Landform.Plain;
        var best = entries[0];
        for (var i = 1; i < entries.Length; i++)
            if (entries[i].ShareBp > best.ShareBp) best = entries[i];
        return best.Landform;
    }

    /// <summary>Share of <see cref="Landform.Mountain"/> in a globe cell's composition, basis points.</summary>
    public static int MountainShareBp(WorldLayers l, int cell)
    {
        if (cell >= l.Composition.Length || l.Composition[cell] is not { } entries) return 0;
        var sum = 0;
        foreach (var e in entries)
            if (e.Landform == Landform.Mountain) sum += e.ShareBp;
        return sum;
    }

    private static readonly uint[] LandformColors =
    [
        0xFF1F4E8C, // Ocean (Biome Ocean)
        0xFF3F7FD0, // Lake (Biome Lake)
        0xFFB4502A, // DryLakeBed (Hydrology dry lake bed)
        0xFF8A7F78, // Mountain (Biome Mountain)
        0xFFB59A62, // Hills
        0xFF9CC76A, // Plain
        0xFFC9C07A, // Basin
        0xFF4F9A4A, // Valley
        0xFF3F8C80, // Wetland
    ];

    /// <summary>Landform layer colour, shared by globe cells, region cells and tiles.</summary>
    public static uint LandformColor(Landform landform) => (int)landform < LandformColors.Length ? LandformColors[(int)landform] : NoArea;

    /// <summary>
    /// Elevation colour for any level: depth ramp on ocean (<paramref name="ocean"/>), otherwise the land ramp on the
    /// height above <paramref name="seaLevelM"/> (0 for land below sea level, such as an enclosed basin).
    /// </summary>
    public static uint ElevationColor(int elevationM, int seaLevelM, bool ocean)
    {
        if (ocean) return ElevationColor(Math.Min(elevationM, seaLevelM - 1), seaLevelM);
        return ElevationColor(Math.Max(elevationM, seaLevelM), seaLevelM);
    }

    /// <summary>
    /// Hydrology colour for any level: river ramp at position <paramref name="riverT"/> (0–1) for rivers, the lake and
    /// dry-lake-bed colours, otherwise the biome colour.
    /// </summary>
    public static uint HydrologyColor(WaterBodyKind kind, Biome biome, double riverT) => kind switch
    {
        WaterBodyKind.River => Lerp(RiverCellLow, RiverCellHigh, Math.Clamp(riverT, 0, 1)),
        WaterBodyKind.Lake => HydrologyLake,
        WaterBodyKind.DryLakeBed => HydrologyDryLakeBed,
        _ => BiomeColor(biome),
    };

    /// <summary>Stroke colour of a river line at position <paramref name="t"/> (0–1) on the flow scale (all levels, all layers).</summary>
    public static uint RiverStrokeColor(double t) => Lerp(RiverEdgeLow, RiverEdgeHigh, Math.Clamp(t, 0, 1));

    /// <summary>Fill colour of the large-lake overlay on a layer: the Hydrology lake colour on Hydrology, the Biome lake colour otherwise.</summary>
    public static uint LakeOverlayColor(GlobeLayer layer) => layer == GlobeLayer.Hydrology ? HydrologyLake : BiomeColor(Biome.Lake);

    /// <summary>Mountain symbol fill colour.</summary>
    public const uint MountainSymbolColor = 0xFF3E352D;

    private static uint BiomeColor(WorldLayers l, int cell) => BiomeColor((Biome)l.BiomeOf[cell]);

    /// <summary>Biome layer colour of a biome (globe cells, region cells and tiles).</summary>
    public static uint BiomeColor(Biome biome) => (int)biome < BiomeColors.Length ? BiomeColors[(int)biome] : NoArea;

    private static uint Elevation(WorldLayers l, int cell) => ElevationColor(l.ElevationM[cell], l.SeaLevelM);

    /// <summary>Elevation ramp: blue by depth below <paramref name="seaLevelM"/>, green → brown → white above it.</summary>
    public static uint ElevationColor(int elevationM, int seaLevelM)
    {
        var e = elevationM;
        if (e < seaLevelM)
        {
            var depth = Math.Clamp((seaLevelM - e) / 6000.0, 0, 1);
            return Lerp(0xFF4A8CD2, 0xFF0A1E5A, depth);
        }

        var h = e - seaLevelM;
        if (h < 2500)
        {
            return Lerp(0xFF3C963C, 0xFF82643C, h / 2500.0);
        }

        return Lerp(0xFF82643C, 0xFFFFFFFF, Math.Min(1.0, (h - 2500) / 3500.0));
    }

    /// <summary>Temperature ramp, −30 °C (blue) through pale yellow to +40 °C (red); input in tenths of °C.</summary>
    public static uint TemperatureColor(short deciC)
    {
        var t = Math.Clamp((deciC + 300) / 700.0, 0, 1); // -30 °C .. +40 °C
        return t < 0.5
            ? Lerp(0xFF2850DC, 0xFFF0F0B4, t * 2)
            : Lerp(0xFFF0F0B4, 0xFFE63228, (t - 0.5) * 2);
    }

    /// <summary>Precipitation ramp, 0 mm/yr (tan) to 4,000 mm/yr (blue).</summary>
    public static uint PrecipitationColor(int mm) => Lerp(0xFFD2B482, 0xFF1E50C8, Math.Clamp(mm / 4000.0, 0, 1));

    private static uint Hydrology(WorldLayers l, int cell)
    {
        var kind = (WaterBodyKind)l.WaterBodyKind[cell];
        if (kind is not (WaterBodyKind.River or WaterBodyKind.Lake or WaterBodyKind.DryLakeBed)) return BiomeColor(l, cell);
        var t = kind == WaterBodyKind.River ? RiverScale(l, l.FlowAccum[cell]) : 0;
        return HydrologyColor(kind, Biome.Ocean, t);
    }

    /// <summary>
    /// Position of <paramref name="flow"/> on the world's river flow range, 0–1, on a log10 scale: 0 at the smallest
    /// flow among river cells and river edges, 1 at the largest (0 when the range is empty or a single value).
    /// </summary>
    public static double RiverScale(WorldLayers l, long flow)
    {
        var range = RiverRanges.GetValue(l, BuildRiverRange);
        if (range.LogMax <= range.LogMin) return 0;
        return Math.Clamp((Math.Log10(Math.Max(1L, flow)) - range.LogMin) / (range.LogMax - range.LogMin), 0, 1);
    }

    private static RiverRange BuildRiverRange(WorldLayers l)
    {
        long min = long.MaxValue, max = 0;
        for (var c = 0; c < l.Grid.CellCount; c++)
        {
            if (l.WaterBodyKind[c] != (byte)WaterBodyKind.River) continue;
            min = Math.Min(min, l.FlowAccum[c]);
            max = Math.Max(max, l.FlowAccum[c]);
        }

        foreach (var e in l.RiverEdges)
        {
            min = Math.Min(min, e.Flow);
            max = Math.Max(max, e.Flow);
        }

        return min > max ? new RiverRange(0, 0) : new RiverRange(Math.Log10(Math.Max(1L, min)), Math.Log10(Math.Max(1L, max)));
    }

    private static uint Areas(WorldLayers l, int cell)
    {
        var owners = AreaOwners.GetValue(l, BuildAreaOwners);
        return owners[cell] < 0 ? NoArea : HashedHue((ulong)owners[cell]);
    }

    /// <summary>Per cell: index of the area with the highest coverage (tie: lowest index), or -1.</summary>
    private static int[] BuildAreaOwners(WorldLayers l)
    {
        var n = l.Grid.CellCount;
        var owner = new int[n];
        var best = new int[n];
        Array.Fill(owner, -1);
        foreach (var area in l.Areas.OrderBy(a => a.Index))
        {
            foreach (var (cell, coverage) in area.Cells)
            {
                if (owner[cell] < 0 || coverage > best[cell])
                {
                    owner[cell] = area.Index;
                    best[cell] = coverage;
                }
            }
        }

        return owner;
    }

    private static uint HashedHue(ulong id)
    {
        var hue = DeterministicHash.Combine(0, id, 0, 0) % 3600 / 10.0;
        return FromHsv(hue, 0.55, 0.85);
    }

    private static uint FromHsv(double hueDeg, double s, double v)
    {
        var c = v * s;
        var hp = hueDeg / 60.0;
        var x = c * (1 - Math.Abs((hp % 2) - 1));
        var (r, g, b) = (int)hp switch
        {
            0 => (c, x, 0.0),
            1 => (x, c, 0.0),
            2 => (0.0, c, x),
            3 => (0.0, x, c),
            4 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        var m = v - c;
        return Pack(r + m, g + m, b + m);
    }

    private static uint Pack(double r, double g, double b) =>
        0xFF000000u | ((uint)Math.Round(r * 255) << 16) | ((uint)Math.Round(g * 255) << 8) | (uint)Math.Round(b * 255);

    private static uint Lerp(uint a, uint b, double t)
    {
        static int Ch(uint c, int shift) => (int)((c >> shift) & 0xFF);
        int Mix(int shift) => (int)Math.Round(Ch(a, shift) + ((Ch(b, shift) - Ch(a, shift)) * t));
        return 0xFF000000u | ((uint)Mix(16) << 16) | ((uint)Mix(8) << 8) | (uint)Mix(0);
    }
}
