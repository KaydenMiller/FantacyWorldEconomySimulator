using WorldEcon.Simulation.Random;
using WorldEcon.WorldGen.Grid;
using WorldEcon.WorldGen.Pipeline;
using WorldEcon.WorldGen.Stages;

namespace WorldEcon.WorldGen.Tiles;

/// <summary>
/// Tile stage 5 (design spec §6 step 8): landform and biome per tile. No randomness; every pass is parallel over tile
/// ranges and each tile writes only its own slot, so the output does not depend on the thread count.
/// <para>
/// Surface. s(t) = the tile sea level for ocean tiles, max(elevation, water level) for lake tiles, else the elevation:
/// relief is measured on the water surface, not on the sea or lake floor.
/// </para>
/// <para>
/// Neighbourhood (r = <see cref="TilesSettings.ReliefRadius"/>). max_r and min_r are the largest and smallest s over the
/// tiles within r steps of the tile: r passes of "own value and the 5 or 6 neighbours' values" (pass k + 1 reads pass
/// k), two <see cref="short"/> arrays per extreme. Relief = max_r − min_r. The surrounding mean m_r is r passes of the
/// arithmetic mean of the tile and its neighbours (a weighted mean over the same neighbourhood), in sixteenths of a
/// metre, each pass rounded half away from zero, in <see cref="int"/> arithmetic.
/// </para>
/// <para>
/// Landform, first rule that holds: ocean tile → Ocean; lake tile → Lake; dry lake bed tile → DryLakeBed; relief ≥
/// <see cref="TilesSettings.MountainReliefM"/> or height above the tile sea level &gt; <see cref="BiomeSettings.MountainHeightM"/>
/// → Mountain; relief ≥ <see cref="TilesSettings.HillsReliefM"/> → Hills; river tile with max_r − s ≥
/// <see cref="TilesSettings.ValleyDepthM"/> → Valley; largest |s difference| to a neighbour ≤
/// <see cref="TilesSettings.WetlandMaxSlopeM"/>, precipitation ≥ <see cref="TilesSettings.WetlandMinPrecipMm"/> and s ≤ m_r
/// (flat, wet, and at or below its surroundings) → Wetland; m_r − s ≥ <see cref="TilesSettings.BasinDepthM"/> → Basin;
/// otherwise Plain.
/// </para>
/// <para>
/// Biome. Ocean tile → Ocean; lake tile → Lake; Mountain landform → Mountain; Hills landform → TemperateForest when
/// <see cref="BiomeStage.IsForestedHills"/> holds, else Hills; every other tile (dry lake beds included) →
/// <see cref="BiomeStage.MainType(BiomeSettings, int, double, int, int)"/> with the tile's height above the tile sea level,
/// temperature, precipitation and slope (largest |s difference| to a neighbour × 100 / centre distance km, cm per km;
/// used only by the Swamp test). The Mountain height rule is the same threshold as the landform rule, so a non-Mountain
/// landform never gets biome Mountain; the Hills height rule gives Hills (or forested hills) on high plateaus.
/// </para>
/// </summary>
public static class TileLandformStage
{
    private const int Fixed = 16; // surrounding mean in sixteenths of a metre

    public static void Run(TileLayers tiles, WorldGenSettings settings, ParallelOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(tiles);
        ArgumentNullException.ThrowIfNull(settings);
        if (tiles.WaterKind.Length != tiles.Count || tiles.FlowAccum.Length != tiles.Count)
            throw new ArgumentException("Tile layers must have completed TileHydrology.", nameof(tiles));

        var grid = tiles.Grid;
        int n = grid.Count;
        var ts = settings.Tiles;
        var bs = settings.Biomes;
        int sea = tiles.SeaLevelM;
        var elev = tiles.ElevationM;
        var water = tiles.WaterKind;
        var lakeId = tiles.LakeId;
        var lakes = tiles.Lakes;
        var temp = tiles.TemperatureDeciC;
        var precip = tiles.PrecipitationMm;
        double radius = settings.Grid.PlanetRadiusKm;
        int r = ts.ReliefRadius;

        var surface = new short[n];
        TileParallel.For(n, options, (s, e) =>
        {
            for (int t = s; t < e; t++)
            {
                var w = (TileWaterKind)water[t];
                surface[t] = w switch
                {
                    TileWaterKind.Ocean => (short)Math.Clamp(sea, short.MinValue, short.MaxValue),
                    TileWaterKind.Lake => (short)Math.Clamp(Math.Max(elev[t], lakes[lakeId[t]].WaterLevelM), short.MinValue, short.MaxValue),
                    _ => elev[t],
                };
            }
        });

        var (max, min) = Extremes(grid, surface, r, options);
        var mean = SurroundingMean(grid, surface, r, options);

        var landform = new byte[n];
        var biome = new byte[n];
        TileParallel.For(n, options, (s, e) =>
        {
            Span<int> ring = stackalloc int[6];
            for (int t = s; t < e; t++)
            {
                var w = (TileWaterKind)water[t];
                if (w == TileWaterKind.Ocean)
                {
                    landform[t] = (byte)Landform.Ocean;
                    biome[t] = (byte)Biome.Ocean;
                    continue;
                }
                if (w == TileWaterKind.Lake)
                {
                    landform[t] = (byte)Landform.Lake;
                    biome[t] = (byte)Biome.Lake;
                    continue;
                }

                int k = grid.Neighbours(t, ring);
                int st = surface[t];
                int steepest = 0;
                double slope = 0;
                var centre = grid.Centre(t);
                for (int x = 0; x < k; x++)
                {
                    int d = Math.Abs(surface[ring[x]] - st);
                    if (d == 0) continue;
                    steepest = Math.Max(steepest, d);
                    double km = ChordKm(centre, grid.Centre(ring[x]), radius);
                    slope = Math.Max(slope, d * 100.0 / km);
                }

                int height = elev[t] - sea;
                int relief = max[t] - min[t];
                Landform lf;
                if (w == TileWaterKind.DryLakeBed) lf = Landform.DryLakeBed;
                else if (relief >= ts.MountainReliefM || height > bs.MountainHeightM) lf = Landform.Mountain;
                else if (relief >= ts.HillsReliefM) lf = Landform.Hills;
                else if (w == TileWaterKind.River && max[t] - st >= ts.ValleyDepthM) lf = Landform.Valley;
                else if (steepest <= ts.WetlandMaxSlopeM && precip[t] >= ts.WetlandMinPrecipMm && (long)st * Fixed <= mean[t]) lf = Landform.Wetland;
                else if (mean[t] - ((long)st * Fixed) >= (long)ts.BasinDepthM * Fixed) lf = Landform.Basin;
                else lf = Landform.Plain;
                landform[t] = (byte)lf;

                biome[t] = (byte)(lf switch
                {
                    Landform.Mountain => Biome.Mountain,
                    Landform.Hills => BiomeStage.IsForestedHills(bs, temp[t], precip[t]) ? Biome.TemperateForest : Biome.Hills,
                    _ => BiomeStage.MainType(bs, height, slope, temp[t], precip[t]),
                });
            }
        });

        tiles.Landform = landform;
        tiles.Biome = biome;
    }

    /// <summary>Largest and smallest value within <paramref name="radius"/> steps (see the class comment).</summary>
    internal static (short[] Max, short[] Min) Extremes(TileGrid grid, short[] values, int radius, ParallelOptions? options)
    {
        int n = grid.Count;
        var maxA = (short[])values.Clone();
        var minA = (short[])values.Clone();
        var maxB = new short[n];
        var minB = new short[n];
        for (int pass = 0; pass < radius; pass++)
        {
            var (srcMax, srcMin, dstMax, dstMin) = (maxA, minA, maxB, minB);
            TileParallel.For(n, options, (s, e) =>
            {
                Span<int> ring = stackalloc int[6];
                for (int t = s; t < e; t++)
                {
                    short hi = srcMax[t], lo = srcMin[t];
                    int k = grid.Neighbours(t, ring);
                    for (int x = 0; x < k; x++)
                    {
                        int u = ring[x];
                        if (srcMax[u] > hi) hi = srcMax[u];
                        if (srcMin[u] < lo) lo = srcMin[u];
                    }
                    dstMax[t] = hi;
                    dstMin[t] = lo;
                }
            });
            (maxA, maxB) = (maxB, maxA);
            (minA, minB) = (minB, minA);
        }
        return (maxA, minA);
    }

    /// <summary>Surrounding mean in sixteenths of a metre (see the class comment).</summary>
    internal static int[] SurroundingMean(TileGrid grid, short[] values, int radius, ParallelOptions? options)
    {
        int n = grid.Count;
        var a = new int[n];
        TileParallel.For(n, options, (s, e) =>
        {
            for (int t = s; t < e; t++) a[t] = values[t] * Fixed;
        });
        var b = new int[n];
        for (int pass = 0; pass < radius; pass++)
        {
            var (src, dst) = (a, b);
            TileParallel.For(n, options, (s, e) =>
            {
                Span<int> ring = stackalloc int[6];
                for (int t = s; t < e; t++)
                {
                    long sum = src[t];
                    int k = grid.Neighbours(t, ring);
                    for (int x = 0; x < k; x++) sum += src[ring[x]];
                    int count = k + 1;
                    dst[t] = (int)(sum >= 0 ? ((2 * sum) + count) / (2L * count) : -(((-2 * sum) + count) / (2L * count)));
                }
            });
            (a, b) = (b, a);
        }
        return a;
    }

    private static double ChordKm(Vector3d a, Vector3d b, double radiusKm)
    {
        double dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
        return Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz)) * radiusKm;
    }

    public static ulong Hash(TileLayers t) =>
        DeterministicHash.Combine(LayerHash.Of(t.Landform), LayerHash.Of(t.Biome), 0, 0);
}
