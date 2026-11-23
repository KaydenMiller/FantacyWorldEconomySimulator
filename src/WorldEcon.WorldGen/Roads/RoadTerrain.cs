using System.Numerics;
using WorldEcon.WorldGen.Grid;
using WorldEcon.WorldGen.Pipeline;
using WorldEcon.WorldGen.Tiles;

namespace WorldEcon.WorldGen.Roads;

/// <summary>Per-tile costs and distances road generation reads. Integer costs; doubles only for distances.</summary>
public sealed class RoadTerrain
{
    private readonly short[] _elevationM;
    private readonly byte[] _waterKind;
    private readonly byte[] _landform;
    private readonly long[] _flowAccum;
    private readonly sbyte[]? _downstreamSlot;
    private readonly double _radiusM;

    public RoadTerrain(TileGrid grid, short[] elevationM, byte[] waterKind, byte[] landform, long[] flowAccum,
        double planetRadiusKm, RoadSettings settings, sbyte[]? downstreamSlot = null)
    {
        if (downstreamSlot is not null && downstreamSlot.Length != grid.Count)
            throw new ArgumentException("The downstream layer must have one entry per tile.", nameof(downstreamSlot));
        _downstreamSlot = downstreamSlot;
        ArgumentNullException.ThrowIfNull(grid);
        ArgumentNullException.ThrowIfNull(settings);
        if (elevationM.Length != grid.Count || waterKind.Length != grid.Count || landform.Length != grid.Count || flowAccum.Length != grid.Count)
            throw new ArgumentException("Every layer must have one entry per tile.");
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(planetRadiusKm);
        Grid = grid;
        Settings = settings;
        _elevationM = elevationM;
        _waterKind = waterKind;
        _landform = landform;
        _flowAccum = flowAccum;
        _radiusM = planetRadiusKm * 1000.0;

        int minLandform = new[] { settings.PlainFactorPct, settings.HillsFactorPct, settings.MountainFactorPct,
            settings.ValleyFactorPct, settings.BasinFactorPct, settings.WetlandFactorPct, settings.DryLakeBedFactorPct }.Min();
        MinLandStepCost = Math.Max(1, settings.BaseStepCost * minLandform / 100 * settings.RoadDiscountPct / 100);

        // Upper bound on the centre spacing of neighbouring tiles: the regular-hexagon spacing for the mean tile area,
        // times 1.25 (Goldberg tiles vary in size by less than that). Keeps the A* heuristic below the true cost.
        double areaPerTile = 4 * Math.PI * _radiusM * _radiusM / grid.Count;
        MaxStepMetres = (long)Math.Ceiling(Math.Sqrt(areaPerTile * 2 / Math.Sqrt(3)) * 1.25);
    }

    public static RoadTerrain From(TileLayers tiles, RoadSettings settings)
        => new(tiles.Grid, tiles.ElevationM, tiles.WaterKind, tiles.Landform, tiles.FlowAccum, tiles.PlanetRadiusKm, settings,
            tiles.DownstreamSlot.Length == tiles.Count ? tiles.DownstreamSlot : null);

    public TileGrid Grid { get; }
    public RoadSettings Settings { get; }
    public int MinLandStepCost { get; }
    public long MaxStepMetres { get; }

    public TileWaterKind Water(int tile) => (TileWaterKind)_waterKind[tile];
    public int ElevationM(int tile) => _elevationM[tile];

    /// <summary>True for tiles a land route may cross: dry land, rivers (bridged or forded) and dry lake beds.</summary>
    public bool PassableLand(int tile) => Water(tile) is TileWaterKind.None or TileWaterKind.River or TileWaterKind.DryLakeBed;

    /// <summary>True for open water: ocean and lake tiles.</summary>
    public bool OpenWater(int tile) => Water(tile) is TileWaterKind.Ocean or TileWaterKind.Lake;

    /// <summary>True for tiles a voyage may cross: ocean, lake and river tiles.</summary>
    public bool Navigable(int tile) => Water(tile) is TileWaterKind.Ocean or TileWaterKind.Lake or TileWaterKind.River;

    /// <summary>True for a land tile next to ocean or lake water.</summary>
    public bool Coastal(int tile) => PortKindOf(tile) == PortKind.Coastal;

    /// <summary>
    /// Port kind of a settlement on <paramref name="tile"/>: Coastal when a neighbour is ocean or lake water; River when
    /// the tile is a river tile or a neighbour is; None otherwise (and for a tile that is itself open water).
    /// </summary>
    public PortKind PortKindOf(int tile)
    {
        if (OpenWater(tile)) return PortKind.None;
        Span<int> ring = stackalloc int[6];
        int n = Grid.Neighbours(tile, ring);
        bool river = Water(tile) == TileWaterKind.River;
        for (int i = 0; i < n; i++)
        {
            if (OpenWater(ring[i])) return PortKind.Coastal;
            if (Water(ring[i]) == TileWaterKind.River) river = true;
        }
        return river ? PortKind.River : PortKind.None;
    }

    /// <summary>The tile <paramref name="tile"/> drains to, or −1 (no downstream layer, sink, or ocean).</summary>
    public int DownstreamOf(int tile)
    {
        if (_downstreamSlot is null) return -1;
        int slot = _downstreamSlot[tile];
        if (slot < 0) return -1;
        Span<int> ring = stackalloc int[6];
        int n = Grid.Neighbours(tile, ring);
        return slot < n ? ring[slot] : -1;
    }

    private int LandformPct(int tile) => (Landform)_landform[tile] switch
    {
        Landform.Hills => Settings.HillsFactorPct,
        Landform.Mountain => Settings.MountainFactorPct,
        Landform.Valley => Settings.ValleyFactorPct,
        Landform.Basin => Settings.BasinFactorPct,
        Landform.Wetland => Settings.WetlandFactorPct,
        Landform.DryLakeBed => Settings.DryLakeBedFactorPct,
        _ => Settings.PlainFactorPct,
    };

    /// <summary>
    /// Cost of a land step onto <paramref name="to"/>: BaseStepCost × landform% / 100, plus SlopeCostPerM × |Δelevation|,
    /// plus RiverCostPerLog2Flow × log2(flow + 1) when <paramref name="to"/> is a river; then × RoadDiscountPct / 100 when
    /// <paramref name="onRoad"/>. At least 1.
    /// </summary>
    public int LandStepCost(int from, int to, bool onRoad)
    {
        long cost = (long)Settings.BaseStepCost * LandformPct(to) / 100;
        cost += (long)Settings.SlopeCostPerM * Math.Abs(_elevationM[to] - _elevationM[from]);
        if (Water(to) == TileWaterKind.River)
            cost += (long)Settings.RiverCostPerLog2Flow * BitOperations.Log2((ulong)Math.Max(0, _flowAccum[to]) + 1);
        if (onRoad) cost = cost * Settings.RoadDiscountPct / 100;
        return (int)Math.Clamp(cost, 1, int.MaxValue);
    }

    public double StepMetresExact(int a, int b)
    {
        var ca = Grid.Centre(a);
        var cb = Grid.Centre(b);
        return Math.Atan2(Vector3d.Cross(ca, cb).Length, Vector3d.Dot(ca, cb)) * _radiusM;
    }

    public long StepMetres(int a, int b) => (long)Math.Round(StepMetresExact(a, b));

    private SearchScratch? _scratch;
    private int[]? _landComponent, _waterComponent, _openWaterComponent;

    /// <summary>Search arrays shared by the searches over this terrain (one search at a time).</summary>
    internal SearchScratch Scratch => _scratch ??= new SearchScratch(Grid.Count);

    /// <summary>Connected set of passable land the tile belongs to; −1 for a tile that is not passable land.</summary>
    public int LandComponent(int tile) => (_landComponent ??= Components(PassableLand))[tile];

    /// <summary>
    /// Connected body of water (ocean, lake and river; or only ocean and lake with <paramref name="openOnly"/>) the tile
    /// belongs to; −1 for a tile that is not such water.
    /// </summary>
    public int WaterComponent(int tile, bool openOnly)
        => openOnly ? (_openWaterComponent ??= Components(OpenWater))[tile] : (_waterComponent ??= Components(Navigable))[tile];

    private int[] Components(Func<int, bool> member)
    {
        var comp = new int[Grid.Count];
        Array.Fill(comp, -1);
        var queue = new Queue<int>();
        Span<int> ring = stackalloc int[6];
        int next = 0;
        for (int start = 0; start < comp.Length; start++)
        {
            if (comp[start] >= 0 || !member(start)) continue;
            comp[start] = next;
            queue.Enqueue(start);
            while (queue.Count > 0)
            {
                int c = queue.Dequeue();
                int n = Grid.Neighbours(c, ring);
                for (int k = 0; k < n; k++)
                {
                    int x = ring[k];
                    if (comp[x] >= 0 || !member(x)) continue;
                    comp[x] = next;
                    queue.Enqueue(x);
                }
            }
            next++;
        }
        return comp;
    }
}
