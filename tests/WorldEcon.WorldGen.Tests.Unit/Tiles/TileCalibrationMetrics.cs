using WorldEcon.WorldGen.Pipeline;
using WorldEcon.WorldGen.Tiles;

namespace WorldEcon.WorldGen.Tests.Unit.Tiles;

/// <summary>
/// Tile-level equivalents of the calibration phase 2 metrics (.superpowers/sdd/2026-11-21-globe-world-generation/
/// calib-phase2-targets.md), measured on a <see cref="GeneratedWorld"/> from <see cref="WorldGenerator.Generate"/>.
/// Land = tiles that are not ocean (lake tiles included, as land cells included lake cells at globe level). Shares of
/// land are tile counts unless the name says area. Definitions:
/// <list type="number">
/// <item>LakeAreaPct: area of lake tiles / area of land tiles.</item>
/// <item>MountainPct, HighlandPct: tiles with biome Mountain, Mountain or Hills / land tiles.</item>
/// <item>TemperateForestPct: tiles with biome TemperateForest / land tiles.</item>
/// <item>Rivers: (a) tile level, RiverReachPct = river tiles whose drainage path reaches an ocean or lake tile / river
/// tiles; (b) globe level on the summary river edges (major rivers, the edges drawn at Globe View), as at globe level:
/// share of edges in systems reaching the ocean or a lake (the terminal edge's downstream cell is an ocean or Lake cell,
/// or the tile river continues inside that cell to an ocean or lake tile, since a summary edge exists only where the
/// river crosses a cell border), mean edge count of the 10 largest systems, coastal stubs (systems of ≤ 2 edges whose
/// land cells all have an ocean neighbour and whose largest flow is below the 90th percentile of edge flows, the phase
/// 2 definition).</item>
/// <item>EastWestBoundaryPct: of neighbouring land-tile pairs (each pair once) with different biomes, the share whose
/// centre-to-centre step is within 20° of north–south (the boundary between them runs east–west); GridEastWestPct is
/// the same share over all land-tile pairs (the grid's own orientation mix).</item>
/// <item>PolarZeroPct, PolarMedianMm: land tiles above 65° latitude at 0 mm; their median precipitation.</item>
/// <item>Wet3500Pct: land tiles ≥ 3,500 mm; with the rainfall bounds Zero (0 mm), Median, Mean, Wet2000 (≥ 2,000 mm).</item>
/// </list>
/// </summary>
public sealed record TileCalibration(
    double LakeAreaPct, double LakeTilePct, double DryTilePct, double RiverTilePct,
    double MountainPct, double HighlandPct, double TemperateForestPct,
    double RiverReachPct, int MajorEdges, double MajorEdgesReachingPct, double Top10SystemEdges, int CoastalStubs,
    double EastWestBoundaryPct, double GridEastWestPct,
    int PolarLand, double PolarZeroPct, int PolarMedianMm,
    double Wet3500Pct, double ZeroPct, int MedianMm, double MeanMm, double Wet2000Pct,
    double LakesUnder10kPct, double Lakes10kTo100kPct, double LakesOver100kPct, int SingleTileLakes, int LakeCount,
    double GlobeLakeAreaPct, double DesertPct)
{
    public static TileCalibration Measure(GeneratedWorld world)
    {
        var t = world.Tiles ?? throw new ArgumentException("The world has no tiles.", nameof(world));
        var l = world.Layers;
        int n = t.Count;
        double radius = t.PlanetRadiusKm;
        int land = 0, lake = 0, dry = 0, river = 0, mountain = 0, hills = 0, forest = 0, desert = 0;
        double landArea = 0, lakeArea = 0;
        int polar = 0, polarZero = 0, zero = 0, wet3500 = 0, wet2000 = 0;
        long precipSum = 0;
        var precipHist = new long[65_536];
        var polarHist = new long[65_536];
        long ewBoundary = 0, boundary = 0, ewAll = 0, all = 0;
        double cos20 = Math.Cos(20 * Math.PI / 180);
        Span<int> ring = stackalloc int[6];
        for (int id = 0; id < n; id++)
        {
            if (t.Ocean[id]) continue;
            land++;
            double a = t.Grid.AreaKm2(id, radius);
            landArea += a;
            var w = (TileWaterKind)t.WaterKind[id];
            if (w == TileWaterKind.Lake) { lake++; lakeArea += a; }
            else if (w == TileWaterKind.DryLakeBed) dry++;
            else if (w == TileWaterKind.River) river++;
            var b = (Biome)t.Biome[id];
            if (b == Biome.Mountain) mountain++;
            if (b == Biome.Hills) hills++;
            if (b == Biome.TemperateForest) forest++;
            if (b == Biome.Desert) desert++;
            int p = t.PrecipitationMm[id];
            precipHist[p]++;
            precipSum += p;
            if (p == 0) zero++;
            if (p >= 3500) wet3500++;
            if (p >= 2000) wet2000++;
            var c = t.Grid.Centre(id);
            if (Math.Asin(Math.Clamp(c.Y, -1, 1)) * 180 / Math.PI > 65 || Math.Asin(Math.Clamp(c.Y, -1, 1)) * 180 / Math.PI < -65)
            {
                polar++;
                polarHist[p]++;
                if (p == 0) polarZero++;
            }

            int k = t.Grid.Neighbours(id, ring);
            for (int x = 0; x < k; x++)
            {
                int u = ring[x];
                if (u < id || t.Ocean[u]) continue;
                var d = t.Grid.Centre(u);
                var step = new Grid.Vector3d(d.X - c.X, d.Y - c.Y, d.Z - c.Z);
                // North direction at c: component of the pole (0, 1, 0) tangent to the sphere.
                var north = new Grid.Vector3d(-c.Y * c.X, 1 - (c.Y * c.Y), -c.Y * c.Z);
                double nl = north.Length, sl = step.Length;
                bool ew = nl > 1e-12 && Math.Abs(Grid.Vector3d.Dot(step, north)) / (nl * sl) >= cos20;
                all++;
                if (ew) ewAll++;
                if (t.Biome[u] == t.Biome[id]) continue;
                boundary++;
                if (ew) ewBoundary++;
            }
        }

        // Tile river reach: terminal kind of each river tile's path (memoised: 0 unknown, 1 reaches ocean/lake, 2 not).
        var state = new byte[n];
        var path = new List<int>();
        int reach = 0;
        for (int id = 0; id < n; id++)
        {
            if (t.WaterKind[id] != (byte)TileWaterKind.River) continue;
            path.Clear();
            int cur = id;
            byte result;
            while (true)
            {
                if (state[cur] != 0) { result = state[cur]; break; }
                var w = (TileWaterKind)t.WaterKind[cur];
                if (cur != id && w is TileWaterKind.Ocean or TileWaterKind.Lake) { result = 1; break; }
                path.Add(cur);
                int next = t.DownstreamOf(cur);
                if (next < 0) { result = t.Ocean[cur] ? (byte)1 : (byte)2; break; }
                cur = next;
            }
            foreach (var x in path) state[x] = result;
            if (result == 1) reach++;
        }

        var (edges, reachingPct, top10, stubs) = GlobeRivers(l, t);

        var lakes = t.Lakes.Where(x => x.Kind == TileWaterKind.Lake).ToList();
        double globeLand = 0, globeLake = 0;
        for (int c = 0; c < l.Grid.CellCount; c++)
        {
            if (l.IsOcean(c)) continue;
            double a = l.Geometry.CellAreaKm2(c, radius);
            globeLand += a;
            if (l.WaterBodyKind[c] == (byte)WaterBodyKind.Lake) globeLake += a;
        }

        double Pct(double x, double total) => total > 0 ? 100.0 * x / total : 0;
        return new TileCalibration(
            Pct(lakeArea, landArea), Pct(lake, land), Pct(dry, land), Pct(river, land),
            Pct(mountain, land), Pct(mountain + hills, land), Pct(forest, land),
            Pct(reach, river), edges, reachingPct, top10, stubs,
            Pct(ewBoundary, boundary), Pct(ewAll, all),
            polar, Pct(polarZero, polar), Median(polarHist, polar),
            Pct(wet3500, land), Pct(zero, land), Median(precipHist, land), land > 0 ? (double)precipSum / land : 0, Pct(wet2000, land),
            Pct(lakes.Where(x => x.AreaKm2 < 10_000).Sum(x => x.TileCount), land),
            Pct(lakes.Where(x => x.AreaKm2 is >= 10_000 and < 100_000).Sum(x => x.TileCount), land),
            Pct(lakes.Where(x => x.AreaKm2 >= 100_000).Sum(x => x.TileCount), land),
            lakes.Count(x => x.TileCount == 1), lakes.Count,
            Pct(globeLake, globeLand), Pct(desert, land));
    }

    /// <summary>
    /// Drainage against stored elevations: for every land tile that is not water (water kind None or River), the
    /// largest elevation of a tile on its drainage path before the path reaches ocean, lake or dry lake bed (or a sink),
    /// minus its own elevation. Returns the number of tiles above <paramref name="toleranceM"/>, the largest gap, and the
    /// number of such land tiles below the tile sea level.
    /// </summary>
    public static (long Count, int Largest, long DryBelowSea) UphillDrainage(TileLayers t, int toleranceM = 1)
    {
        int n = t.Count;
        var ahead = new int[n]; // largest elevation strictly downstream before water; int.MinValue: none
        var done = new bool[n];
        var path = new List<int>();
        long count = 0, below = 0;
        int largest = 0;
        static bool IsWater(byte w) => w is (byte)TileWaterKind.Ocean or (byte)TileWaterKind.Lake or (byte)TileWaterKind.DryLakeBed;
        for (int id = 0; id < n; id++)
        {
            if (done[id] || IsWater(t.WaterKind[id])) continue;
            path.Clear();
            int cur = id;
            int tail = int.MinValue; // max ahead of the last path tile's downstream part
            while (true)
            {
                path.Add(cur);
                int d = t.DownstreamOf(cur);
                if (d < 0 || IsWater(t.WaterKind[d])) { tail = int.MinValue; break; }
                if (done[d]) { tail = Math.Max(ahead[d], t.ElevationM[d]); break; }
                cur = d;
            }
            for (int i = path.Count - 1; i >= 0; i--)
            {
                int x = path[i];
                ahead[x] = tail;
                done[x] = true;
                tail = Math.Max(tail, t.ElevationM[x]);
            }
        }
        for (int id = 0; id < n; id++)
        {
            if (!done[id]) continue;
            if (t.ElevationM[id] < t.SeaLevelM) below++;
            if (ahead[id] == int.MinValue) continue;
            int gap = ahead[id] - t.ElevationM[id];
            if (gap > toleranceM) count++;
            largest = Math.Max(largest, gap);
        }
        return (count, largest, below);
    }

    private static int Median(long[] hist, int count)
    {
        if (count == 0) return 0;
        long target = (count - 1) / 2, seen = 0;
        for (int v = 0; v < hist.Length; v++)
        {
            seen += hist[v];
            if (seen > target) return v;
        }
        return hist.Length - 1;
    }

    // Systems of summary river edges (globe phase-2 definitions).
    private static (int Edges, double ReachingPct, double Top10, int Stubs) GlobeRivers(WorldLayers l, TileLayers t)
    {
        var edges = l.RiverEdges;
        if (edges.Count == 0) return (0, 0, 0, 0);
        int n = l.Grid.CellCount;
        var root = Enumerable.Range(0, n).ToArray();
        int Find(int x) { while (root[x] != x) { root[x] = root[root[x]]; x = root[x]; } return x; }
        foreach (var e in edges)
        {
            int a = Find(e.LowCell), b = Find(e.HighCell);
            if (a != b) root[Math.Max(a, b)] = Math.Min(a, b);
        }
        var upstreamCells = new HashSet<int>(edges.Select(e => e.FlowsLowToHigh ? e.LowCell : e.HighCell));
        var systems = edges.GroupBy(e => Find(e.LowCell)).ToList();
        var flows = edges.Select(e => e.Flow).Order().ToArray();
        long topTier = flows[Math.Min(flows.Length - 1, flows.Length * 9000 / 10_000)];
        int reaching = 0, stubs = 0;
        foreach (var s in systems)
        {
            bool reaches = s.Any(e =>
            {
                int down = e.FlowsLowToHigh ? e.HighCell : e.LowCell;
                return !upstreamCells.Contains(down) && (l.IsOcean(down) || l.WaterBodyKind[down] == (byte)WaterBodyKind.Lake || EndsInWater(t, e, down));
            });
            if (reaches) reaching += s.Count();
            if (s.Count() <= 2 && s.Max(e => e.Flow) < topTier)
            {
                var cells = s.SelectMany(e => new[] { e.LowCell, e.HighCell }).Where(c => !l.IsOcean(c)).Distinct();
                if (cells.All(c => l.Grid.Neighbours(c).ToArray().Any(l.IsOcean))) stubs++;
            }
        }
        double top10 = systems.Select(s => s.Count()).OrderDescending().Take(10).Average();
        return (edges.Count, 100.0 * reaching / edges.Count, top10, stubs);
    }

    // The tile river of a terminal edge: from the edge's upstream tile crossing into `down`, follow the tile drainage
    // while it stays in `down`; true when it reaches an ocean or lake tile there (the mouth lies inside the cell).
    private static bool EndsInWater(TileLayers t, RiverEdge e, int down)
    {
        int up = e.FlowsLowToHigh ? e.LowCell : e.HighCell;
        foreach (var start in t.GlobeCells!.TilesOf(up))
        {
            if (t.FlowAccum[start] != e.Flow || t.WaterKind[start] != (byte)TileWaterKind.River) continue;
            int cur = t.DownstreamOf(start);
            while (cur >= 0 && t.GlobeParent[cur] == down)
            {
                if (t.WaterKind[cur] is (byte)TileWaterKind.Ocean or (byte)TileWaterKind.Lake) return true;
                cur = t.DownstreamOf(cur);
            }
            return cur >= 0 && (t.WaterKind[cur] is (byte)TileWaterKind.Ocean or (byte)TileWaterKind.Lake);
        }
        return false;
    }

    public override string ToString() =>
        $"lake {LakeAreaPct:F2} % of land area ({LakeTilePct:F2} % of tiles; <10k km² {LakesUnder10kPct:F2}, 10k–100k {Lakes10kTo100kPct:F2}, ≥100k {LakesOver100kPct:F2}; {LakeCount} lakes, {SingleTileLakes} single-tile), dry {DryTilePct:F2} %, river tiles {RiverTilePct:F2} %; globe summary lake {GlobeLakeAreaPct:F2} %\n"
        + $"Mountain {MountainPct:F1} %, Mountain+Hills {HighlandPct:F1} %, TemperateForest {TemperateForestPct:F1} %, Desert {DesertPct:F1} %\n"
        + $"rivers: tile reach {RiverReachPct:F1} %; summary edges {MajorEdges}, reaching {MajorEdgesReachingPct:F1} %, top-10 mean {Top10SystemEdges:F1}, coastal stubs {CoastalStubs}\n"
        + $"east-west boundary {EastWestBoundaryPct:F1} % (grid {GridEastWestPct:F1} %); polar land {PolarLand}: 0 mm {PolarZeroPct:F1} %, median {PolarMedianMm} mm\n"
        + $"rain: >=3500 {Wet3500Pct:F2} %, 0 mm {ZeroPct:F1} %, median {MedianMm}, mean {MeanMm:F0}, >=2000 {Wet2000Pct:F1} %";
}
