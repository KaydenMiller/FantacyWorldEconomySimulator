using WorldEcon.Simulation.Random;
using WorldEcon.WorldGen.Pipeline;

namespace WorldEcon.WorldGen.Tiles;

/// <summary>
/// Summary stage (design spec §6 step 9, §8): writes the globe-cell layers of <see cref="WorldLayers"/> from the tiles and
/// builds the <see cref="RegionSummary"/>. A cell's tiles are those whose parent at that level is the cell
/// (<see cref="TileLayers.GlobeParent"/>, <see cref="TileLayers.RegionParent"/>). Counts are tile counts, not areas.
/// Parallel over cells; each cell writes only its own slots, so the output does not depend on the thread count.
/// <para>
/// Globe cell (overwrites the Elevation, SeaLevel, Biomes and Hydrology layers; temperature and precipitation keep the
/// globe Climate values). <see cref="WorldLayers.SeaLevelM"/> = the tile sea level. Ocean cell: ocean tiles × 2 ≥ tiles.
/// Elevation = the median tile elevation (lower median: index ⌊(count − 1) / 2⌋ of the ascending sort), then
/// min(median, sea level − 1) for an ocean cell and max(median, sea level) otherwise, so <see cref="WorldLayers.IsOcean"/>
/// equals the ocean rule. Composition = landform tile counts; the 4 largest (ties: lower <see cref="Landform"/> value)
/// are kept, the counts of the others are added to the largest; share = round half up (count × 10,000 / tiles); the
/// largest entry absorbs the rounding difference to 10,000; entries in that order (largest first). Biome: Ocean for an
/// ocean cell, Lake when lake tiles × 2 ≥ tiles, else the most common biome among tiles that are neither ocean nor lake
/// (ties: lower <see cref="Biome"/> value). Water body kind: None for an ocean cell; Lake when lake tiles × 3 ≥ tiles;
/// else DryLakeBed when dry lake bed tiles × 3 ≥ tiles; else River when a river tile has flow ≥
/// <see cref="TilesSettings.MajorRiverFlow"/> and the cell is an end of a kept river edge (below); else None. Flow = the largest tile flow (0 for an ocean cell); downstream
/// = the cell that the drainage path of the largest-flow tile (ties: lowest id) enters first, −1 for an ocean cell or a
/// path ending inside the cell. River edges: one <see cref="RiverEdge"/> per pair of cells where a river tile with flow ≥
/// MajorRiverFlow drains into a tile of the other cell, carrying the largest such flow (ties: lowest tile id) and its
/// direction; sorted by (LowCell, HighCell); then short systems are dropped as at globe level (<see cref="DropShortSystems"/>).
/// A cell left without a kept edge loses water kind River.
/// </para>
/// <para>
/// Region cell: <see cref="RegionSummary"/>. Water = ocean + lake tiles; Kind = Ocean (ocean ≥ lake) or Lake when water
/// × 3 ≥ tiles, else the most common landform of the other tiles (ties: lower value). Biome = most common biome of the
/// tiles that are neither ocean nor lake (none: Lake when lake ≥ ocean tiles, else Ocean). Elevation, temperature and
/// precipitation = means rounded half away from zero. Mountain share = round half up (Mountain tiles × 10,000 / tiles).
/// Flow and downstream as for globe cells, at region level, for every region cell. LakeId = the lake (kind Lake) with
/// the largest area among the cell's tiles (ties: lowest id), −1 none. A large lake is a lake (kind Lake) with area ≥
/// <see cref="TilesSettings.LargeLakeMinKm2"/>. InLargeLake = the cell's tiles of large lakes × 3 ≥ the cell's tiles, or
/// the cell is, for some large lake, the region cell holding the most tiles of that lake (ties: lowest region cell id).
/// The second rule gives every large lake at least one marked cell (spec §8: every large lake is drawn at Globe View),
/// including a lake spread over several region cells with less than 1/3 of each.
/// </para>
/// </summary>
public static class TileSummaryStage
{
    private const int BpFull = 10_000;
    private const int CellChunk = 256;
    private static readonly int LandformCount = Enum.GetValues<Landform>().Length;
    private static readonly int BiomeCount = Enum.GetValues<Biome>().Length;

    /// <summary>Overwrites the globe summary layers of <paramref name="globe"/>, sets <see cref="TileLayers.GlobeCells"/>, and returns the region summary.</summary>
    public static RegionSummary Run(TileLayers tiles, WorldLayers globe, WorldGenSettings settings, ParallelOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(tiles);
        ArgumentNullException.ThrowIfNull(globe);
        ArgumentNullException.ThrowIfNull(settings);
        if (tiles.Landform.Length != tiles.Count)
            throw new ArgumentException("Tile layers must have completed TileLandform.", nameof(tiles));
        if (globe.Grid.CellCount != tiles.GlobeTiles.Count)
            throw new ArgumentException("Globe layers and tile layers have different globe grids.", nameof(globe));

        tiles.GlobeCells = TileCellIndex.Build(tiles.GlobeParent, globe.Grid.CellCount);
        SummariseGlobe(tiles, globe, settings, options);
        var regionCells = TileCellIndex.Build(tiles.RegionParent, tiles.RegionGrid.CellCount);
        return SummariseRegions(tiles, regionCells, settings, options);
    }

    private static void SummariseGlobe(TileLayers tiles, WorldLayers globe, WorldGenSettings settings, ParallelOptions? options)
    {
        var index = tiles.GlobeCells!;
        int cells = index.CellCount;
        int sea = tiles.SeaLevelM;
        long major = settings.Tiles.MajorRiverFlow;
        var elevation = new int[cells];
        var biome = new byte[cells];
        var composition = new CompositionEntry[cells][];
        var waterKind = new byte[cells];
        var flowOut = new long[cells];
        var downstream = new int[cells];

        TileParallel.For(cells, CellChunk, options, (s, e) =>
        {
            var elev = new short[64];
            Span<int> landforms = stackalloc int[LandformCount];
            Span<int> biomes = stackalloc int[BiomeCount];
            for (int c = s; c < e; c++)
            {
                var ids = index.TilesOf(c);
                int count = ids.Length;
                if (elev.Length < count) elev = new short[count];
                landforms.Clear();
                biomes.Clear();
                int ocean = 0, lake = 0, dry = 0, best = -1;
                bool majorRiver = false;
                for (int i = 0; i < count; i++)
                {
                    int t = ids[i];
                    elev[i] = tiles.ElevationM[t];
                    landforms[tiles.Landform[t]]++;
                    var w = (TileWaterKind)tiles.WaterKind[t];
                    if (w == TileWaterKind.Ocean) ocean++;
                    else if (w == TileWaterKind.Lake) lake++;
                    else
                    {
                        if (w == TileWaterKind.DryLakeBed) dry++;
                        else if (w == TileWaterKind.River && tiles.FlowAccum[t] >= major) majorRiver = true;
                        biomes[tiles.Biome[t]]++;
                    }
                    if (best < 0 || tiles.FlowAccum[t] > tiles.FlowAccum[best]) best = t; // ids ascending: ties keep the lowest
                }

                Array.Sort(elev, 0, count);
                int median = elev[(count - 1) / 2];
                bool isOcean = 2 * ocean >= count;
                elevation[c] = isOcean ? Math.Min(median, sea - 1) : Math.Max(median, sea);
                composition[c] = Composition(landforms, count);
                biome[c] = (byte)(isOcean ? Biome.Ocean : 2 * lake >= count ? Biome.Lake : MostCommonLandBiome(biomes, lake >= ocean));
                waterKind[c] = (byte)(isOcean ? WaterBodyKind.None
                    : 3 * lake >= count ? WaterBodyKind.Lake
                    : 3 * dry >= count ? WaterBodyKind.DryLakeBed
                    : majorRiver ? WaterBodyKind.River
                    : WaterBodyKind.None);
                flowOut[c] = isOcean ? 0 : tiles.FlowAccum[best];
                downstream[c] = isOcean ? -1 : Exit(tiles, tiles.GlobeParent, best);
            }
        });

        globe.SeaLevelM = sea;
        globe.ElevationM = elevation;
        globe.BiomeOf = biome;
        globe.Composition = composition;
        globe.WaterBodyKind = waterKind;
        globe.FlowAccum = flowOut;
        globe.DownstreamOf = downstream;
        globe.RiverEdges = DropShortSystems(RiverEdges(tiles, major), cells, settings.Hydrology);
        // Water kind River only on land cells at an end of a kept river edge, so a River cell always has a drawn river.
        var onEdge = new bool[cells];
        foreach (var e in globe.RiverEdges) { onEdge[e.LowCell] = true; onEdge[e.HighCell] = true; }
        for (int c = 0; c < cells; c++)
            if (waterKind[c] == (byte)WaterBodyKind.River && !onEdge[c]) waterKind[c] = (byte)WaterBodyKind.None;
    }

    /// <summary>Composition from landform tile counts (see the class comment).</summary>
    internal static CompositionEntry[] Composition(ReadOnlySpan<int> counts, int total)
    {
        Span<int> forms = stackalloc int[4];
        int kept = 0;
        for (int pick = 0; pick < 4; pick++)
        {
            int bestForm = -1;
            for (int f = 0; f < counts.Length; f++)
            {
                if (counts[f] == 0 || forms[..kept].Contains(f)) continue;
                if (bestForm < 0 || counts[f] > counts[bestForm]) bestForm = f;
            }
            if (bestForm < 0) break;
            forms[kept++] = bestForm;
        }

        Span<int> num = stackalloc int[4];
        int folded = total;
        for (int i = 0; i < kept; i++)
        {
            num[i] = counts[forms[i]];
            if (i > 0) folded -= num[i];
        }
        num[0] = folded; // the largest entry also holds the tiles of the landforms not kept

        var result = new CompositionEntry[kept];
        int sum = 0;
        for (int i = 0; i < kept; i++)
        {
            int bp = (int)((((long)num[i] * BpFull * 2) + total) / (2L * total));
            sum += bp;
            result[i] = new CompositionEntry((Landform)forms[i], (short)bp);
        }
        result[0] = result[0] with { ShareBp = (short)(result[0].ShareBp + BpFull - sum) };
        return result;
    }

    private static Biome MostCommonLandBiome(ReadOnlySpan<int> biomes, bool lakeFallback)
    {
        int best = -1;
        for (int b = 0; b < biomes.Length; b++)
        {
            if (b is (int)Biome.Ocean or (int)Biome.Lake || biomes[b] == 0) continue;
            if (best < 0 || biomes[b] > biomes[best]) best = b;
        }
        return best >= 0 ? (Biome)best : lakeFallback ? Biome.Lake : Biome.Ocean;
    }

    /// <summary>The cell (per <paramref name="parent"/>) that the drainage path from <paramref name="tile"/> enters first; −1 when it ends in the tile's own cell.</summary>
    internal static int Exit(TileLayers tiles, int[] parent, int tile)
    {
        int own = parent[tile];
        int cur = tile;
        for (int guard = 0; guard < tiles.Count; guard++)
        {
            int next = tiles.DownstreamOf(cur);
            if (next < 0) return -1;
            if (parent[next] != own) return parent[next];
            cur = next;
        }
        throw new InvalidOperationException($"Drainage cycle from tile {tile}.");
    }

    private static List<RiverEdge> RiverEdges(TileLayers tiles, long major)
    {
        var best = new Dictionary<(int Low, int High), (long Flow, bool LowToHigh)>();
        var parent = tiles.GlobeParent;
        for (int t = 0; t < tiles.Count; t++)
        {
            if (tiles.WaterKind[t] != (byte)TileWaterKind.River || tiles.FlowAccum[t] < major) continue;
            int d = tiles.DownstreamOf(t);
            if (d < 0) continue;
            int a = parent[t], b = parent[d];
            if (a == b) continue;
            var key = (Math.Min(a, b), Math.Max(a, b));
            long flow = tiles.FlowAccum[t];
            if (best.TryGetValue(key, out var cur) && cur.Flow >= flow) continue; // ascending t: ties keep the lowest tile
            best[key] = (flow, a < b);
        }
        var edges = best.Select(kv => new RiverEdge(kv.Key.Low, kv.Key.High, kv.Value.LowToHigh, kv.Value.Flow)).ToList();
        edges.Sort((x, y) => x.LowCell != y.LowCell ? x.LowCell.CompareTo(y.LowCell) : x.HighCell.CompareTo(y.HighCell));
        return edges;
    }

    /// <summary>
    /// Drops river systems (edges connected through shared cells) with fewer than
    /// <see cref="HydrologySettings.RiverMinSystemEdges"/> edges unless one edge has a flow at or above the
    /// <see cref="HydrologySettings.RiverTopTierPercentileBp"/> percentile of all edge flows (sorted ascending, index
    /// ⌊count × bp / 10,000⌋, clamped to the last): the globe rule that removes one- and two-edge coastal stubs, applied
    /// to the summary edges and, by <c>MapRenderer</c>, to the region-cell river segments drawn at Globe and Region View.
    /// <paramref name="cells"/> is the cell count of the level the edges connect. Union-find with integer comparisons only, so the result does not depend on edge order.
    /// </summary>
    public static List<RiverEdge> DropShortSystems(List<RiverEdge> edges, int cells, HydrologySettings s)
    {
        if (s.RiverMinSystemEdges <= 1 || edges.Count == 0) return edges;
        var root = new int[cells];
        for (int c = 0; c < cells; c++) root[c] = c;
        int Find(int x)
        {
            while (root[x] != x) { root[x] = root[root[x]]; x = root[x]; }
            return x;
        }
        foreach (var e in edges)
        {
            int a = Find(e.LowCell), b = Find(e.HighCell);
            if (a != b) root[Math.Max(a, b)] = Math.Min(a, b);
        }
        var count = new int[cells];
        var maxFlow = new long[cells];
        foreach (var e in edges)
        {
            int r = Find(e.LowCell);
            count[r]++;
            maxFlow[r] = Math.Max(maxFlow[r], e.Flow);
        }
        var flows = edges.Select(e => e.Flow).Order().ToArray();
        long topTier = flows[(int)Math.Min(flows.Length - 1, (long)flows.Length * s.RiverTopTierPercentileBp / BpFull)];
        return edges.Where(e => count[Find(e.LowCell)] >= s.RiverMinSystemEdges || maxFlow[Find(e.LowCell)] >= topTier).ToList();
    }

    private static RegionSummary SummariseRegions(TileLayers tiles, TileCellIndex index, WorldGenSettings settings, ParallelOptions? options)
    {
        var summary = new RegionSummary(tiles.RegionGrid, tiles.RegionGeometry, tiles.RegionGlobeParent);
        double largeLake = settings.Tiles.LargeLakeMinKm2;
        var lakes = tiles.Lakes;
        var largeTiles = new int[summary.Count];
        TileParallel.For(summary.Count, CellChunk * 4, options, (s, e) =>
        {
            Span<int> landforms = stackalloc int[LandformCount];
            Span<int> biomes = stackalloc int[BiomeCount];
            for (int c = s; c < e; c++)
            {
                var ids = index.TilesOf(c);
                int count = ids.Length;
                landforms.Clear();
                biomes.Clear();
                int ocean = 0, lake = 0, best = -1, lakeId = -1;
                long elevSum = 0, tempSum = 0, precipSum = 0;
                for (int i = 0; i < count; i++)
                {
                    int t = ids[i];
                    elevSum += tiles.ElevationM[t];
                    tempSum += tiles.TemperatureDeciC[t];
                    precipSum += tiles.PrecipitationMm[t];
                    var w = (TileWaterKind)tiles.WaterKind[t];
                    if (w == TileWaterKind.Ocean) ocean++;
                    else if (w == TileWaterKind.Lake)
                    {
                        lake++;
                        int id = tiles.LakeId[t];
                        if (lakes[id].AreaKm2 >= largeLake) largeTiles[c]++;
                        if (lakeId < 0 || lakes[id].AreaKm2 > lakes[lakeId].AreaKm2 || (lakes[id].AreaKm2 == lakes[lakeId].AreaKm2 && id < lakeId)) lakeId = id;
                    }
                    else
                    {
                        landforms[tiles.Landform[t]]++;
                        biomes[tiles.Biome[t]]++;
                    }
                    if (best < 0 || tiles.FlowAccum[t] > tiles.FlowAccum[best]) best = t;
                }

                summary.TileCount[c] = count;
                if (count == 0)
                {
                    summary.Kind[c] = (byte)Landform.Ocean;
                    summary.Biome[c] = (byte)Biome.Ocean;
                    summary.Downstream[c] = -1;
                    summary.LakeId[c] = -1;
                    continue;
                }
                bool water = 3 * (ocean + lake) >= count;
                Landform kind;
                if (water) kind = ocean >= lake ? Landform.Ocean : Landform.Lake;
                else
                {
                    int f = -1;
                    for (int x = 0; x < landforms.Length; x++)
                        if (landforms[x] > 0 && (f < 0 || landforms[x] > landforms[f])) f = x;
                    kind = (Landform)f;
                }
                summary.Kind[c] = (byte)kind;
                summary.Biome[c] = (byte)MostCommonLandBiome(biomes, lake >= ocean);
                summary.ElevationM[c] = (short)RoundMean(elevSum, count);
                summary.TemperatureDeciC[c] = (short)RoundMean(tempSum, count);
                summary.PrecipitationMm[c] = (ushort)RoundMean(precipSum, count);
                summary.MountainShareBp[c] = (ushort)((((long)landforms[(int)Landform.Mountain] * BpFull * 2) + count) / (2L * count));
                summary.Flow[c] = tiles.FlowAccum[best];
                summary.Downstream[c] = Exit(tiles, tiles.RegionParent, best);
                summary.LakeId[c] = lakeId;
                summary.InLargeLake[c] = 3 * largeTiles[c] >= count && largeTiles[c] > 0;
            }
        });
        MarkLargeLakeFallback(tiles, index, summary, largeTiles, largeLake);
        return summary;
    }

    /// <summary>
    /// Marks, for every large lake, the region cell with the most of its tiles (ties: lowest cell id). Sequential in cell
    /// id order over the cells that hold large-lake tiles (a few thousand at the default preset), so the result does not
    /// depend on the thread count.
    /// </summary>
    private static void MarkLargeLakeFallback(TileLayers tiles, TileCellIndex index, RegionSummary summary, int[] largeTiles, double largeLake)
    {
        var lakes = tiles.Lakes;
        var bestCell = new int[lakes.Count];
        var bestCount = new int[lakes.Count];
        Array.Fill(bestCell, -1);
        var counts = new Dictionary<int, int>();
        for (int c = 0; c < summary.Count; c++)
        {
            if (largeTiles[c] == 0) continue;
            counts.Clear();
            foreach (int t in index.TilesOf(c))
            {
                if (tiles.WaterKind[t] != (byte)TileWaterKind.Lake) continue;
                int id = tiles.LakeId[t];
                if (lakes[id].AreaKm2 >= largeLake) counts[id] = counts.GetValueOrDefault(id) + 1;
            }
            foreach (var (id, n) in counts)
                if (n > bestCount[id]) { bestCount[id] = n; bestCell[id] = c; } // ascending c: ties keep the lowest id
        }
        foreach (int c in bestCell)
            if (c >= 0) summary.InLargeLake[c] = true;
    }

    private static long RoundMean(long sum, int count) =>
        sum >= 0 ? ((2 * sum) + count) / (2L * count) : -(((-2 * sum) + count) / (2L * count));

    /// <summary>Hash of the globe layers this stage writes (elevation, sea level, biome, composition, drainage, water body kind, river edges).</summary>
    public static ulong GlobeHash(WorldLayers l)
    {
        var h = DeterministicHash.Combine(LayerHash.Of(l.ElevationM), unchecked((ulong)(long)l.SeaLevelM), LayerHash.Of(l.BiomeOf), 0);
        foreach (var comp in l.Composition)
        {
            h = DeterministicHash.Combine(h, (ulong)comp.Length, 0, 0);
            foreach (var e in comp)
                h = DeterministicHash.Combine(h, (ulong)e.Landform, unchecked((ulong)(long)e.ShareBp), 0);
        }
        h = DeterministicHash.Combine(h, LayerHash.Of(l.DownstreamOf), LayerHash.Of(l.FlowAccum), LayerHash.Of(l.WaterBodyKind));
        foreach (var e in l.RiverEdges)
            h = DeterministicHash.Combine(h, (ulong)e.LowCell, (ulong)e.HighCell, unchecked((ulong)e.Flow) ^ (e.FlowsLowToHigh ? 1UL : 0UL));
        return DeterministicHash.Combine(h, (ulong)l.RiverEdges.Count, 0, 0);
    }

    public static ulong RegionHash(RegionSummary r)
    {
        var h = DeterministicHash.Combine(LayerHash.Of(r.TileCount), LayerHash.Of(r.Kind), LayerHash.Of(r.Biome), LayerHash.Of(r.ElevationM));
        h = DeterministicHash.Combine(h, LayerHash.Of(r.TemperatureDeciC), LayerHash.Of(r.PrecipitationMm), LayerHash.Of(r.MountainShareBp));
        h = DeterministicHash.Combine(h, LayerHash.Of(r.Flow), LayerHash.Of(r.Downstream), LayerHash.Of(r.LakeId));
        return DeterministicHash.Combine(h, LayerHash.Of(r.InLargeLake), LayerHash.Of(r.GlobeParent), 0);
    }

    /// <summary>Stage hash: globe summary and region summary.</summary>
    public static ulong Hash(WorldLayers globe, RegionSummary regions) =>
        DeterministicHash.Combine(GlobeHash(globe), RegionHash(regions), 0, 0);
}
