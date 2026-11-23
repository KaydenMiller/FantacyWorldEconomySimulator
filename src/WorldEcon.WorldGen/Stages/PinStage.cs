using ErrorOr;
using WorldEcon.SharedKernel.Globe;
using WorldEcon.Simulation.Random;
using WorldEcon.WorldGen.Grid;
using WorldEcon.WorldGen.Pipeline;
using WorldEcon.WorldGen.Tiles;

namespace WorldEcon.WorldGen.Stages;

/// <summary>A settlement-like request to be tied to a cell: explicit cell id, explicit lat/lon, or neither (auto-placed).</summary>
public sealed record PinRequest(string Key, string PoliticalGroup, int? CellId, double? LatDeg, double? LonDeg);

/// <summary>
/// A resolved pin. <paramref name="Warning"/> is set on an auto-placed group anchor whose separation from the other
/// groups' anchors had to be relaxed below the requested ring count; null otherwise. <paramref name="TileId"/> is the
/// tile chosen inside <paramref name="Cell"/> when tile layers were given (<see cref="PinStage.Run"/>), else −1.
/// </summary>
public sealed record PinResult(string Key, int Cell, bool AutoPlaced, string? Warning = null, int TileId = -1);

/// <summary>An authored name for the area of <paramref name="Kind"/> that contains the point.</summary>
public sealed record AreaNameRequest(string Name, GeographicRegionKind Kind, double LatDeg, double LonDeg);

/// <summary>
/// Stage 10: pinning. Lookup only: nothing in the layers is changed. Explicit requests resolve to a cell
/// (<c>CellId</c>, else <c>NearestCell(LatDeg, LonDeg)</c> when both coordinates are given); a cell that is ocean or lake is a
/// <c>pin.ocean</c> error (one code for both kinds of water; the message names "ocean cell" or "lake cell"). Requests without a location are placed in input order:
/// the first request of a <c>PoliticalGroup</c> (explicit or auto) is that group's anchor. Settings: <see cref="PinSettings"/>;
/// the numbers below are its defaults. An auto-placed anchor takes the
/// best free candidate at least <see cref="PinSettings.MinGroupSeparationRings"/> rings (default 6) from every other group's anchor; the
/// separation drops one ring at a time to 1, then any free candidate; when it drops, that anchor's
/// <see cref="PinResult.Warning"/> states the separation reached and the separation requested. Later auto-placed members take the best free candidate within
/// <see cref="PinSettings.MemberRadiusRings"/> rings (default 3; breadth-first search depth over <c>Neighbours</c>) of the anchor, else the best free candidate at the nearest ring beyond.
/// Auto-placed requests never take a cell already occupied by an earlier request; explicit pins may share cells.
/// No free candidate -> <c>pin.noland</c>. Candidates are land cells that are not Ice, Lake or DryLakeBed. Suitability (integer):
/// +4 when temperature is within 50–250 deci-°C, +4 when precipitation is within 400–1,500 mm, +2 when the cell has a
/// river (a <c>RiverEdge</c> or <c>WaterBodyKind.River</c>), +2 when it has an ocean neighbour. Ties are broken by position
/// in a candidate order shuffled with a Fisher–Yates pass driven by <c>Xoshiro256StarStar(seed)</c>, so different seeds
/// pick different cells among equals; the shuffle is computed once per call. Several requests may share a cell.
/// </summary>
public static class PinStage
{
    public static ErrorOr<IReadOnlyList<PinResult>> Run(WorldLayers layers, IReadOnlyList<PinRequest> requests, ulong seed, PinSettings? settings = null,
        TileLayers? tiles = null)
    {
        var cells = RunCells(layers, requests, seed, settings);
        if (cells.IsError || tiles is null) return cells;
        return PinTiles(tiles, requests, cells.Value, seed, settings ?? new PinSettings());
    }

    /// <summary>Pins to globe cells and then to tiles of <paramref name="world"/> (tiles only when it has them).</summary>
    public static ErrorOr<IReadOnlyList<PinResult>> Run(GeneratedWorld world, IReadOnlyList<PinRequest> requests, ulong seed, PinSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(world);
        return Run(world.Layers, requests, seed, settings, world.Tiles);
    }

    private static ErrorOr<IReadOnlyList<PinResult>> RunCells(WorldLayers layers, IReadOnlyList<PinRequest> requests, ulong seed, PinSettings? settings)
    {
        settings ??= new PinSettings();
        var minGroupSeparationRings = settings.MinGroupSeparationRings;
        var n = layers.Grid.CellCount;
        var riverCells = new bool[n];
        foreach (var e in layers.RiverEdges)
        {
            riverCells[e.LowCell] = true;
            riverCells[e.HighCell] = true;
        }

        var score = new int[n];
        var candidates = new List<int>();
        for (int c = 0; c < n; c++)
        {
            if (!IsCandidate(layers, c)) continue;
            candidates.Add(c);
            score[c] = Score(layers, c, riverCells, settings);
        }

        // Fisher–Yates over the candidate list; rank[c] is the position in the shuffled order.
        var rng = new Xoshiro256StarStar(seed);
        var order = candidates.ToArray();
        for (int i = order.Length - 1; i > 0; i--)
        {
            int j = rng.NextInt(i + 1);
            (order[i], order[j]) = (order[j], order[i]);
        }
        var rank = new int[n];
        Array.Fill(rank, int.MaxValue);
        for (int i = 0; i < order.Length; i++) rank[order[i]] = i;

        var anchors = new Dictionary<string, int>(StringComparer.Ordinal);
        var occupied = new bool[n];
        var results = new List<PinResult>(requests.Count);
        foreach (var req in requests)
        {
            int cell;
            bool auto = false;
            string? warning = null;
            if (req.CellId is int id)
            {
                if (id < 0 || id >= n) return Error.Validation("pin.cell", $"{req.Key} is pinned to cell {id}, which does not exist");
                cell = id;
            }
            else if (req.LatDeg is double lat && req.LonDeg is double lon)
            {
                cell = layers.Geometry.NearestCell(lat, lon);
            }
            else
            {
                auto = true;
                int? picked;
                if (anchors.TryGetValue(req.PoliticalGroup, out var anchor))
                {
                    picked = BestNear(layers, anchor, score, rank, occupied, settings.MemberRadiusRings);
                }
                else
                {
                    var placed = PlaceAnchor(layers, anchors, order, score, rank, occupied, minGroupSeparationRings);
                    picked = placed?.Cell;
                    if (placed is { } p && anchors.Count > 0 && p.Separation < minGroupSeparationRings)
                        warning = $"{req.Key} (group {req.PoliticalGroup}): anchor placed {p.Separation} rings from another group's anchor; requested {minGroupSeparationRings} (separation relaxed).";
                }

                if (picked is null) return Error.Validation("pin.noland", $"No land cell available for {req.Key}");
                cell = picked.Value;
            }

            if (!auto && layers.IsOcean(cell))
                return Error.Validation("pin.ocean", $"{req.Key} is pinned to ocean cell {cell}");
            if (!auto && layers.BiomeOf[cell] == (byte)Biome.Lake)
                return Error.Validation("pin.ocean", $"{req.Key} is pinned to lake cell {cell}");

            anchors.TryAdd(req.PoliticalGroup, cell);
            occupied[cell] = true;
            results.Add(new PinResult(req.Key, cell, auto, warning));
        }
        return results;
    }

    /// <summary>
    /// Tile pinning inside each request's cell (lookup only), in request order. Candidates: the cell's tiles that are
    /// not ocean, lake or dry lake bed; when the cell has none, its dry lake bed tiles; none at all → <c>pin.notile</c>.
    /// A request with latitude and longitude takes the nearest tile when it is a candidate of its cell. Otherwise the
    /// tile is chosen by suitability: <see cref="PinSettings.TileRiverScore"/> when the tile or a neighbour is a river
    /// tile, <see cref="PinSettings.TileShoreScore"/> when a neighbour is ocean or lake, <see cref="PinSettings.TileFlatScore"/>
    /// when the largest elevation difference to a neighbour is at most <see cref="PinSettings.TileFlatMaxDiffM"/>.
    /// Spacing: candidates at least <see cref="PinSettings.TileMinSpacingKm"/> (centre chord distance) from every tile
    /// already chosen in the same cell are preferred; among them the highest score wins; when none is that far, the
    /// candidate farthest from the chosen tiles wins (then score). Remaining ties: the lower
    /// <c>DeterministicHash.Combine(seed, tile, 0, 0)</c>, then the lower id.
    /// </summary>
    private static ErrorOr<IReadOnlyList<PinResult>> PinTiles(TileLayers tiles, IReadOnlyList<PinRequest> requests, IReadOnlyList<PinResult> cells,
        ulong seed, PinSettings p)
    {
        if (tiles.Landform.Length != tiles.Count)
            return Error.Validation("pin.tiles", "Tile layers have not completed TileLandform; tiles cannot be pinned.");
        var index = tiles.GlobeCells ?? TileCellIndex.Build(tiles.GlobeParent, tiles.GlobeTiles.Count);
        double radius = tiles.PlanetRadiusKm;
        var chosenInCell = new Dictionary<int, List<int>>();
        var results = new List<PinResult>(cells.Count);
        Span<int> ring = stackalloc int[6];
        for (int r = 0; r < cells.Count; r++)
        {
            var pin = cells[r];
            var req = requests[r];
            var ids = index.TilesOf(pin.Cell);
            bool anyLand = false;
            foreach (var t in ids)
            {
                if (IsTileCandidate(tiles, t, dryAllowed: false)) { anyLand = true; break; }
            }
            bool dryAllowed = !anyLand;
            if (!chosenInCell.TryGetValue(pin.Cell, out var chosen)) chosenInCell[pin.Cell] = chosen = [];

            int tile = -1;
            if (req.CellId is null && req.LatDeg is double lat && req.LonDeg is double lon)
            {
                double la = lat * Math.PI / 180, lo = lon * Math.PI / 180;
                int near = tiles.Grid.Nearest(new Vector3d(Math.Cos(la) * Math.Sin(lo), Math.Sin(la), Math.Cos(la) * Math.Cos(lo)));
                if (tiles.GlobeParent[near] == pin.Cell && IsTileCandidate(tiles, near, dryAllowed)) tile = near;
            }

            if (tile < 0)
            {
                int best = -1, bestScore = 0;
                double bestDist = 0;
                bool bestSpaced = false;
                ulong bestHash = 0;
                foreach (var t in ids)
                {
                    if (!IsTileCandidate(tiles, t, dryAllowed)) continue;
                    double dist = MinChordKm(tiles, t, chosen, radius);
                    bool spaced = dist >= p.TileMinSpacingKm;
                    int score = TileScore(tiles, t, p, ring);
                    ulong hash = DeterministicHash.Combine(seed, (ulong)t, 0, 0);
                    bool better;
                    if (best < 0) better = true;
                    else if (spaced != bestSpaced) better = spaced;
                    else if (!spaced && dist != bestDist) better = dist > bestDist;
                    else if (score != bestScore) better = score > bestScore;
                    else better = hash < bestHash || (hash == bestHash && t < best);
                    if (!better) continue;
                    (best, bestScore, bestDist, bestSpaced, bestHash) = (t, score, dist, spaced, hash);
                }
                tile = best;
            }

            if (tile < 0) return Error.Validation("pin.notile", $"{req.Key}: cell {pin.Cell} has no land tile to pin to");
            chosen.Add(tile);
            results.Add(pin with { TileId = tile });
        }
        return results;
    }

    private static bool IsTileCandidate(TileLayers tiles, int t, bool dryAllowed)
    {
        var w = (TileWaterKind)tiles.WaterKind[t];
        return w is TileWaterKind.None or TileWaterKind.River || (dryAllowed && w == TileWaterKind.DryLakeBed);
    }

    private static int TileScore(TileLayers tiles, int t, PinSettings p, Span<int> ring)
    {
        bool river = tiles.WaterKind[t] == (byte)TileWaterKind.River, shore = false;
        int steepest = 0;
        int k = tiles.Grid.Neighbours(t, ring);
        for (int x = 0; x < k; x++)
        {
            int u = ring[x];
            var w = (TileWaterKind)tiles.WaterKind[u];
            if (w == TileWaterKind.River) river = true;
            if (w is TileWaterKind.Ocean or TileWaterKind.Lake) shore = true;
            else steepest = Math.Max(steepest, Math.Abs(tiles.ElevationM[u] - tiles.ElevationM[t]));
        }
        return (river ? p.TileRiverScore : 0) + (shore ? p.TileShoreScore : 0) + (steepest <= p.TileFlatMaxDiffM ? p.TileFlatScore : 0);
    }

    private static double MinChordKm(TileLayers tiles, int t, List<int> chosen, double radiusKm)
    {
        if (chosen.Count == 0) return double.PositiveInfinity;
        var a = tiles.Grid.Centre(t);
        double best = double.PositiveInfinity;
        foreach (var c in chosen)
        {
            var b = tiles.Grid.Centre(c);
            double dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
            best = Math.Min(best, Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz)) * radiusKm);
        }
        return best;
    }

    /// <summary>
    /// Resolves each request to the area of its kind containing the nearest cell. A blank or missing name is an
    /// <c>area.name</c> error; a point with no area of that kind is an <c>area.none</c> error; two requests for one area are an <c>area.duplicate</c> error. Returns the (area index, name) pairs
    /// in request order; the areas themselves are not modified.
    /// </summary>
    public static ErrorOr<IReadOnlyList<(int AreaIndex, string Name)>> NameAreas(WorldLayers layers, IReadOnlyList<AreaNameRequest> requests)
    {
        var result = new List<(int AreaIndex, string Name)>(requests.Count);
        foreach (var req in requests)
        {
            if (string.IsNullOrWhiteSpace(req.Name))
                return Error.Validation("area.name", $"A {req.Kind} area name at ({req.LatDeg}, {req.LonDeg}) is blank");
            var cell = layers.Geometry.NearestCell(req.LatDeg, req.LonDeg);
            GeneratedArea? hit = null;
            foreach (var area in layers.Areas)
            {
                if (area.Kind != req.Kind) continue;
                foreach (var (c, _) in area.Cells)
                    if (c == cell) { hit = area; break; }
                if (hit is not null) break;
            }
            if (hit is null)
                return Error.Validation("area.none", $"'{req.Name}': no {req.Kind} area at ({req.LatDeg}, {req.LonDeg}) (cell {cell})");
            if (result.Any(r => r.AreaIndex == hit.Index))
                return Error.Validation("area.duplicate", $"'{req.Name}': the {req.Kind} area at ({req.LatDeg}, {req.LonDeg}) is already named by another request");
            result.Add((hit.Index, req.Name));
        }
        return result;
    }

    private static bool IsCandidate(WorldLayers l, int c) =>
        !l.IsOcean(c)
        && l.BiomeOf[c] != (byte)Biome.Ice
        && l.BiomeOf[c] != (byte)Biome.Lake
        && l.WaterBodyKind[c] != (byte)WaterBodyKind.Lake
        && l.WaterBodyKind[c] != (byte)WaterBodyKind.DryLakeBed;

    private static int Score(WorldLayers l, int c, bool[] riverCells, PinSettings p)
    {
        int s = 0;
        var t = l.TemperatureDeciC[c];
        if (t >= p.IdealMinDeciC && t <= p.IdealMaxDeciC) s += p.TemperatureScore;
        var mm = l.PrecipitationMm[c];
        if (mm >= p.IdealMinMm && mm <= p.IdealMaxMm) s += p.PrecipitationScore;
        if (riverCells[c] || l.WaterBodyKind[c] == (byte)WaterBodyKind.River) s += p.RiverScore;
        foreach (var nb in l.Grid.Neighbours(c))
            if (l.IsOcean(nb)) { s += p.CoastScore; break; }
        return s;
    }

    private static bool Better(int c, int best, int[] score, int[] rank) =>
        score[c] > score[best] || (score[c] == score[best] && rank[c] < rank[best]);

    /// <summary>
    /// Anchor of a group's first auto-placed request: the best free candidate at least <paramref name="minSeparation"/> rings from
    /// every other group's anchor; the requirement drops one ring at a time to 1, then any free candidate. Returns the cell and
    /// its ring distance to the nearest other anchor (<see cref="int.MaxValue"/> when there is none). Null when no free
    /// candidate exists.
    /// </summary>
    private static (int Cell, int Separation)? PlaceAnchor(WorldLayers l, Dictionary<string, int> anchors, int[] order, int[] score, int[] rank, bool[] occupied, int minSeparation)
    {
        var n = l.Grid.CellCount;
        var dist = new int[n];
        Array.Fill(dist, int.MaxValue);
        var queue = new Queue<int>();
        foreach (var a in anchors.Values.Distinct().Order())
        {
            dist[a] = 0;
            queue.Enqueue(a);
        }
        while (queue.Count > 0)
        {
            var c = queue.Dequeue();
            foreach (var nb in l.Grid.Neighbours(c))
                if (dist[nb] == int.MaxValue) { dist[nb] = dist[c] + 1; queue.Enqueue(nb); }
        }
        for (int sep = Math.Max(1, minSeparation); sep >= 1; sep--)
        {
            int? best = null;
            foreach (var c in order)
                if (!occupied[c] && dist[c] >= sep && (best is null || Better(c, best.Value, score, rank))) best = c;
            if (best is int b) return (b, dist[b]);
        }
        int? any = null;
        foreach (var c in order)
            if (!occupied[c] && (any is null || Better(c, any.Value, score, rank))) any = c;
        return any is int fallback ? (fallback, dist[fallback]) : null;
    }

    /// <summary>
    /// Best free candidate within <paramref name="radius"/> rings of the anchor; when none, the best free candidate at the
    /// nearest ring beyond. Null when no free candidate is reachable.
    /// </summary>
    private static int? BestNear(WorldLayers l, int anchor, int[] score, int[] rank, bool[] occupied, int radius)
    {
        var depth = new Dictionary<int, int> { [anchor] = 0 };
        var queue = new Queue<int>();
        queue.Enqueue(anchor);
        int? best = null;
        int bestDepth = 0;
        while (queue.Count > 0)
        {
            var c = queue.Dequeue();
            if (best is not null && depth[c] > radius && depth[c] > bestDepth) break;
            if (rank[c] != int.MaxValue && !occupied[c]
                && (best is null || Better(c, best.Value, score, rank)))
            {
                best = c;
                bestDepth = depth[c];
            }
            foreach (var nb in l.Grid.Neighbours(c))
                if (depth.TryAdd(nb, depth[c] + 1)) queue.Enqueue(nb);
        }
        return best;
    }
}
