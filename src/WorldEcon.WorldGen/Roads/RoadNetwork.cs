using WorldEcon.WorldGen.Grid;

namespace WorldEcon.WorldGen.Roads;

public sealed record NetworkNode(int Index, RoadNodeKind Kind, int TileId, string Key);

/// <summary>A route between two network nodes. Tiles run from NodeA's tile to NodeB's tile.</summary>
public sealed record NetworkSegment(int Id, int NodeA, int NodeB, int[] Tiles, int RoadIndex, RoadCategory Category);

/// <summary>One segment of a road, in road order; AtoB is false when the road travels it from NodeB to NodeA.</summary>
public sealed record NetworkRoadEntry(int SegmentId, bool AtoB);

/// <summary>
/// Builds the route network one road path at a time, splitting paths into segments at nodes and crossings:
/// <list type="bullet">
/// <item>a Settlement node on an interior tile always cuts the path;</item>
/// <item>a Junction or Site node cuts it when the node's spokes lie on both sides of the path's chord;</item>
/// <item>an existing segment of the same category whose chord on a tile interleaves with the path's chord is split,
/// and a Junction is created there.</item>
/// </list>
/// Shared tiles without interleaving chords produce no cut, so parallel routes stay separate. Air paths are never cut.
/// </summary>
public sealed class RoadNetwork(TileGrid grid)
{
    private readonly List<NetworkNode> _nodes = [];
    private readonly Dictionary<int, int> _nodeAt = new();
    private readonly SortedDictionary<int, NetworkSegment> _segments = new();
    private readonly Dictionary<int, List<NetworkRoadEntry>> _roads = new();
    private readonly Dictionary<RoadCategory, Dictionary<int, List<(int Seg, int Pos)>>> _interior = new();
    private readonly Dictionary<RoadCategory, HashSet<int>> _usedTiles = new();
    private int _nextSegment;

    public IReadOnlyList<NetworkNode> Nodes => _nodes;
    public IReadOnlyList<NetworkSegment> Segments => _segments.Values.ToList();

    public IReadOnlyList<NetworkRoadEntry> RoadEntries(int roadIndex)
        => _roads.TryGetValue(roadIndex, out var l) ? l : [];

    /// <summary>Tiles used by routes of <paramref name="category"/> (land roads attract later land roads, and so on).</summary>
    public IReadOnlySet<int> UsedTiles(RoadCategory category) => UsedSet(category);

    /// <summary>Land road tiles.</summary>
    public IReadOnlySet<int> RoadTiles => UsedSet(RoadCategory.Land);

    public int AddNode(RoadNodeKind kind, int tile, string key)
    {
        if (_nodeAt.ContainsKey(tile))
            throw new InvalidOperationException($"Tile {tile} already holds node '{_nodes[_nodeAt[tile]].Key}'.");
        int index = _nodes.Count;
        _nodes.Add(new NetworkNode(index, kind, tile, key));
        _nodeAt[tile] = index;
        return index;
    }

    public int? NodeAt(int tile) => _nodeAt.TryGetValue(tile, out int i) ? i : null;

    public void AddRoad(int roadIndex, int[] tiles, RoadCategory category = RoadCategory.Land)
    {
        if (tiles.Length < 2) throw new ArgumentException("A road path needs at least two tiles.", nameof(tiles));
        if (NodeAt(tiles[0]) is null || NodeAt(tiles[^1]) is null)
            throw new ArgumentException("A road path must start and end on node tiles.", nameof(tiles));
        if (!_roads.ContainsKey(roadIndex)) _roads[roadIndex] = [];

        var cuts = new List<int> { 0 };
        if (category != RoadCategory.Air)
        {
            for (int i = 1; i < tiles.Length - 1; i++)
                if (CutsAt(tiles, i, category)) cuts.Add(i);
        }
        cuts.Add(tiles.Length - 1);

        for (int c = 0; c + 1 < cuts.Count; c++)
        {
            var piece = tiles[cuts[c]..(cuts[c + 1] + 1)];
            int seg = AddSegment(_nodeAt[piece[0]], _nodeAt[piece[^1]], piece, roadIndex, category);
            _roads[roadIndex].Add(new NetworkRoadEntry(seg, true));
        }
    }

    private bool CutsAt(int[] tiles, int i, RoadCategory category)
    {
        int t = tiles[i];
        int n = TileChords.EdgeCount(grid, t);
        int a = TileChords.EdgeIndex(grid, t, tiles[i - 1]);
        int b = TileChords.EdgeIndex(grid, t, tiles[i + 1]);

        if (_nodeAt.TryGetValue(t, out int node))
        {
            if (_nodes[node].Kind == RoadNodeKind.Settlement) return true;
            return TileChords.SpokesOnBothSides(n, a, b, Spokes(node, category));
        }

        if (!Interior(category).TryGetValue(t, out var through)) return false;
        var crossing = through
            .Where(x => Interleave(n, a, b, t, _segments[x.Seg], x.Pos))
            .OrderBy(x => x.Seg)
            .ToList();
        if (crossing.Count == 0) return false;
        int junction = AddNode(RoadNodeKind.Junction, t, $"junction:{t}");
        foreach (var (seg, pos) in crossing)
            Split(seg, pos, junction);
        return true;
    }

    private bool Interleave(int n, int a, int b, int t, NetworkSegment s, int pos)
        => TileChords.Interleave(n, a, b,
            TileChords.EdgeIndex(grid, t, s.Tiles[pos - 1]),
            TileChords.EdgeIndex(grid, t, s.Tiles[pos + 1]));

    private IEnumerable<int> Spokes(int node, RoadCategory category)
    {
        int tile = _nodes[node].TileId;
        foreach (var s in _segments.Values)
        {
            if (s.Category != category) continue;
            if (s.NodeA == node) yield return TileChords.EdgeIndex(grid, tile, s.Tiles[1]);
            if (s.NodeB == node) yield return TileChords.EdgeIndex(grid, tile, s.Tiles[^2]);
        }
    }

    private void Split(int segId, int pos, int junction)
    {
        var s = _segments[segId];
        Unindex(s);
        _segments.Remove(segId);
        int s1 = AddSegment(s.NodeA, junction, s.Tiles[..(pos + 1)], s.RoadIndex, s.Category);
        int s2 = AddSegment(junction, s.NodeB, s.Tiles[pos..], s.RoadIndex, s.Category);
        var entries = _roads[s.RoadIndex];
        int at = entries.FindIndex(e => e.SegmentId == segId);
        bool atoB = entries[at].AtoB;
        entries.RemoveAt(at);
        entries.InsertRange(at, atoB
            ? [new NetworkRoadEntry(s1, true), new NetworkRoadEntry(s2, true)]
            : [new NetworkRoadEntry(s2, false), new NetworkRoadEntry(s1, false)]);
    }

    private int AddSegment(int a, int b, int[] tiles, int roadIndex, RoadCategory category)
    {
        int id = _nextSegment++;
        var s = new NetworkSegment(id, a, b, tiles, roadIndex, category);
        _segments[id] = s;
        var map = Interior(category);
        for (int p = 1; p < tiles.Length - 1; p++)
        {
            if (!map.TryGetValue(tiles[p], out var list)) map[tiles[p]] = list = [];
            list.Add((id, p));
        }
        foreach (int t in tiles) UsedSet(category).Add(t);
        return id;
    }

    private void Unindex(NetworkSegment s)
    {
        var map = Interior(s.Category);
        for (int p = 1; p < s.Tiles.Length - 1; p++)
            if (map.TryGetValue(s.Tiles[p], out var list))
                list.RemoveAll(x => x.Seg == s.Id);
    }

    private Dictionary<int, List<(int Seg, int Pos)>> Interior(RoadCategory c)
        => _interior.TryGetValue(c, out var m) ? m : _interior[c] = new();

    private HashSet<int> UsedSet(RoadCategory c)
        => _usedTiles.TryGetValue(c, out var s) ? s : _usedTiles[c] = [];
}
