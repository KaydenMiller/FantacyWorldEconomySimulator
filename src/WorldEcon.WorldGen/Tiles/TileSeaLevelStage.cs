using WorldEcon.Simulation.Random;
using WorldEcon.WorldGen.Pipeline;

namespace WorldEcon.WorldGen.Tiles;

/// <summary>
/// Tile stage 2 (design spec §6 step 5): sea level and ocean tiles.
/// <para>
/// Ocean rule for a level S: a tile is ocean when its elevation is below S and its connected component of below-S tiles
/// (over <see cref="Grid.TileGrid.Neighbours"/>) has an area of at least <see cref="TilesSettings.MinOceanAreaKm2"/>.
/// Every component that large is ocean, so separate seas count; smaller enclosed below-S components stay land (lake or
/// dry lake bed candidates for <see cref="TileHydrologyStage"/>).
/// </para>
/// <para>
/// Sea level: the integer S whose land fraction (area of non-ocean tiles / total area, tile areas from
/// <see cref="Grid.TileGrid.AreaKm2"/>) is closest to <see cref="SeaLevelSettings.LandFractionBp"/>; ties go to the lower
/// S. Only S = lowest elevation (no ocean) and S = e + 1 for each elevation e present change the ocean set, and the lower
/// S of a run with the same set wins the tie, so those are the candidates. The ocean area for every candidate comes from
/// one pass: tiles are added in (elevation, id) order (counting sort over the <see cref="short"/> range) to a union-find
/// over neighbours; the sum of the areas of components at or above the minimum is kept up to date at each union, and read
/// after the last tile of each elevation. A second pass replays the union-find up to the chosen S and marks the ocean.
/// Areas are integers in units of the mean tile area / 2²⁰ (4πR² / (tiles × 2²⁰), rounded per tile; about 2²⁰ units per
/// tile at any radius), so the sums are exact, do not depend on order, and the same grid gives the same unit areas (up to floating-point rounding) at every
/// planet radius; total ≤ 2.25 × 10¹⁵ fits <see cref="long"/> and the land-fraction products × 10,000 are compared in
/// <see cref="Int128"/>. The minimum ocean area is converted to the same unit and capped at the total area + 1. The
/// ocean set only grows with S (components only grow), so the land fraction is non-increasing in S. Both passes are
/// sequential.
/// </para>
/// <para>
/// Settings at the limits: land 10,000 bp gives S = lowest elevation (no ocean); land 0 bp gives S = highest elevation + 1
/// (one component, the whole sphere, ocean when its area reaches the minimum).
/// </para>
/// </summary>
public static class TileSeaLevelStage
{
    private const double UnitsPerMeanTile = 1 << 20;

    public static void Run(TileLayers tiles, WorldGenSettings settings, ParallelOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(tiles);
        ArgumentNullException.ThrowIfNull(settings);
        var grid = tiles.Grid;
        int n = grid.Count;
        var elevation = tiles.ElevationM;
        double radius = settings.Grid.PlanetRadiusKm;
        int landBp = Math.Clamp(settings.SeaLevel.LandFractionBp, 0, 10_000);
        double unitKm2 = 4 * Math.PI * radius * radius / (Math.Max(1, n) * UnitsPerMeanTile);

        var area = new long[n];
        FillAreas(tiles, radius, unitKm2, area, options);
        long total = 0;
        foreach (var a in area) total += a;
        long minArea = (long)Math.Min(Math.Round(settings.Tiles.MinOceanAreaKm2 / unitKm2), total + 1.0);

        var (order, binStart) = SortByElevation(elevation);
        var uf = new int[n];
        if (n == 0)
        {
            tiles.SeaLevelM = 0;
            tiles.Ocean = [];
            return;
        }

        // Pass 1: ocean area for every candidate level.
        Array.Fill(uf, -1);
        int lowest = elevation[order[0]];
        int bestLevel = lowest;
        Int128 bestDiff = Int128.Abs(((Int128)total * 10_000) - ((Int128)total * landBp)); // land fraction 1 at S = lowest
        long big = 0;
        Span<int> ring = stackalloc int[6];
        for (int b = 0; b < binStart.Length - 1; b++)
        {
            int s0 = binStart[b], s1 = binStart[b + 1];
            if (s0 == s1) continue;
            for (int i = s0; i < s1; i++)
                big += Add(order[i], grid, uf, area, minArea, ring);
            // Land area × 10,000 compared with total × landBp, in integers.
            long land = total - big;
            Int128 diff = Int128.Abs(((Int128)land * 10_000) - ((Int128)total * landBp));
            if (diff < bestDiff)
            {
                bestDiff = diff;
                bestLevel = b + short.MinValue + 1;
            }
        }

        // Pass 2: replay up to the chosen level and mark the ocean.
        Array.Fill(uf, -1);
        FillAreas(tiles, radius, unitKm2, area, options);
        int count = 0;
        for (int b = 0; b < binStart.Length - 1 && b + short.MinValue < bestLevel; b++)
        {
            for (int i = binStart[b]; i < binStart[b + 1]; i++)
                Add(order[i], grid, uf, area, minArea, ring);
            count = binStart[b + 1];
        }
        var ocean = new bool[n];
        for (int i = 0; i < count; i++)
        {
            int t = order[i];
            ocean[t] = area[Find(uf, t)] >= minArea;
        }

        tiles.SeaLevelM = bestLevel;
        tiles.Ocean = ocean;
    }

    // Adds tile t: a new component, then unions with every neighbour already added. Returns the change of the ocean area
    // (sum of the areas of components with area ≥ minArea). Roots hold their component's area in area[]; the larger area
    // becomes the root (ties: lower id).
    private static long Add(int t, Grid.TileGrid grid, int[] uf, long[] area, long minArea, Span<int> ring)
    {
        long delta = 0;
        uf[t] = t;
        if (area[t] >= minArea) delta += area[t];
        int k = grid.Neighbours(t, ring);
        for (int s = 0; s < k; s++)
        {
            int u = ring[s];
            if (uf[u] < 0) continue;
            int a = Find(uf, t), b = Find(uf, u);
            if (a == b) continue;
            long areaA = area[a], areaB = area[b], merged = areaA + areaB;
            if (areaA >= minArea) delta -= areaA;
            if (areaB >= minArea) delta -= areaB;
            if (merged >= minArea) delta += merged;
            bool aRoot = areaA > areaB || (areaA == areaB && a < b);
            int root = aRoot ? a : b, child = aRoot ? b : a;
            uf[child] = root;
            area[root] = merged;
        }
        return delta;
    }

    private static int Find(int[] uf, int x)
    {
        while (uf[x] != x)
        {
            uf[x] = uf[uf[x]];
            x = uf[x];
        }
        return x;
    }

    private static void FillAreas(TileLayers tiles, double radius, double unitKm2, long[] area, ParallelOptions? options)
    {
        var grid = tiles.Grid;
        TileParallel.For(grid.Count, options, (s, e) =>
        {
            for (int t = s; t < e; t++)
                area[t] = (long)Math.Round(grid.AreaKm2(t, radius) / unitKm2);
        });
    }

    /// <summary>Tile ids in (elevation, id) order and the start index of each of the 65,536 elevation bins (plus the end).</summary>
    internal static (int[] Order, int[] BinStart) SortByElevation(short[] elevation)
    {
        const int Bins = 1 << 16;
        var start = new int[Bins + 1];
        foreach (var e in elevation) start[e - short.MinValue + 1]++;
        for (int b = 0; b < Bins; b++) start[b + 1] += start[b];
        var next = (int[])start.Clone();
        var order = new int[elevation.Length];
        for (int t = 0; t < elevation.Length; t++)
            order[next[elevation[t] - short.MinValue]++] = t;
        return (order, start);
    }

    public static ulong Hash(TileLayers t) =>
        DeterministicHash.Combine(LayerHash.Of([t.SeaLevelM]), LayerHash.Of(t.Ocean), 0, 0);
}
