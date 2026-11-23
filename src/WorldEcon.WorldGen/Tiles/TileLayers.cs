using WorldEcon.WorldGen.Grid;

namespace WorldEcon.WorldGen.Tiles;

/// <summary>
/// Tile-level generated data (design spec §5, §7): the implicit tile grid, the region level, parent tables between the
/// three levels, and one array per tile layer, indexed by tile id. Arrays only, no per-tile objects. Each tile stage sets
/// its own arrays; later stages (hydrology, landform, biome) add properties here.
/// </summary>
public sealed class TileLayers
{
    /// <summary>As the other constructor, with <see cref="PlanetRadiusKm"/> set.</summary>
    public TileLayers(TileGrid grid, TileGrid globeTiles, GlobeGrid regionGrid, GlobeGeometry regionGeometry,
        int[] globeParent, int[] regionParent, int[] regionGlobeParent, double planetRadiusKm)
        : this(grid, globeTiles, regionGrid, regionGeometry, globeParent, regionParent, regionGlobeParent)
    {
        PlanetRadiusKm = planetRadiusKm;
    }

    public TileLayers(TileGrid grid, TileGrid globeTiles, GlobeGrid regionGrid, GlobeGeometry regionGeometry,
        int[] globeParent, int[] regionParent, int[] regionGlobeParent)
    {
        ArgumentNullException.ThrowIfNull(grid);
        ArgumentNullException.ThrowIfNull(globeTiles);
        ArgumentNullException.ThrowIfNull(regionGrid);
        ArgumentNullException.ThrowIfNull(regionGeometry);
        ArgumentNullException.ThrowIfNull(globeParent);
        ArgumentNullException.ThrowIfNull(regionParent);
        ArgumentNullException.ThrowIfNull(regionGlobeParent);
        Grid = grid;
        GlobeTiles = globeTiles;
        RegionGrid = regionGrid;
        RegionGeometry = regionGeometry;
        GlobeParent = globeParent;
        RegionParent = regionParent;
        RegionGlobeParent = regionGlobeParent;
    }

    /// <summary>Tile grid at Frequency × RegionMultiplier × TileMultiplier.</summary>
    public TileGrid Grid { get; }

    /// <summary>Implicit grid at the globe frequency (same ids as the globe <see cref="GlobeGrid"/>), used for lattice lookups.</summary>
    public TileGrid GlobeTiles { get; }

    /// <summary>Region level: <see cref="GlobeGrid.Build"/> at Frequency × RegionMultiplier.</summary>
    public GlobeGrid RegionGrid { get; }

    public GlobeGeometry RegionGeometry { get; }

    /// <summary>Globe cell of each tile (<see cref="GridHierarchy"/> parent rule).</summary>
    public int[] GlobeParent { get; }

    /// <summary>Region cell of each tile.</summary>
    public int[] RegionParent { get; }

    /// <summary>Globe cell of each region cell. Can differ from <see cref="GlobeParent"/> of the region's tiles along borders.</summary>
    public int[] RegionGlobeParent { get; }

    /// <summary>TileElevation: elevation in metres per tile.</summary>
    public short[] ElevationM { get; set; } = [];

    /// <summary>TileSeaLevel: tiles with elevation strictly below this value (metres) are below sea level.</summary>
    public int SeaLevelM { get; set; }

    /// <summary>TileSeaLevel: true for tiles below sea level connected to the largest below-sea component. Enclosed below-sea basins are false (land).</summary>
    public bool[] Ocean { get; set; } = [];

    /// <summary>TileClimate: mean surface temperature per tile, tenths of °C.</summary>
    public short[] TemperatureDeciC { get; set; } = [];

    /// <summary>TileClimate: precipitation per tile, mm/year.</summary>
    public ushort[] PrecipitationMm { get; set; } = [];

    /// <summary>TileHydrology: <see cref="TileWaterKind"/> per tile, as byte.</summary>
    public byte[] WaterKind { get; set; } = [];

    /// <summary>TileHydrology: index into <see cref="Lakes"/> of the lake or dry lake bed the tile belongs to; −1 for none.</summary>
    public int[] LakeId { get; set; } = [];

    /// <summary>
    /// TileHydrology: slot (index into <see cref="Grid.TileGrid.Neighbours"/>) of the neighbour the tile drains to; −1 for
    /// ocean tiles and sinks (the lowest tile of a dry lake bed, the synthetic outlet of a world without ocean).
    /// </summary>
    public sbyte[] DownstreamSlot { get; set; } = [];

    /// <summary>
    /// TileHydrology: flow in mm·km²/yr, the tile's own precipitation × area plus all flow draining into it. 0 on ocean
    /// tiles (flow reaching the sea is counted on the last land tile).
    /// </summary>
    public long[] FlowAccum { get; set; } = [];

    /// <summary>TileHydrology: lakes and dry lake beds, ordered by id.</summary>
    public List<TileLake> Lakes { get; set; } = [];

    /// <summary>TileLandform: <see cref="Pipeline.Landform"/> per tile, as byte.</summary>
    public byte[] Landform { get; set; } = [];

    /// <summary>TileLandform: <see cref="Pipeline.Biome"/> per tile, as byte.</summary>
    public byte[] Biome { get; set; } = [];

    /// <summary>Tiles grouped by <see cref="GlobeParent"/>; set by <see cref="TileSummaryStage"/> (null before it runs).</summary>
    public TileCellIndex? GlobeCells { get; set; }

    private double _planetRadiusKm;

    /// <summary>
    /// Planet radius in km (<see cref="Pipeline.GridSettings.PlanetRadiusKm"/>), used for distances and areas. Set by
    /// <see cref="TileStages.CreateLayers"/> or the constructor overload; reading it before it is set (positive) throws
    /// <see cref="InvalidOperationException"/>, so no code silently assumes an Earth radius.
    /// </summary>
    public double PlanetRadiusKm
    {
        get => _planetRadiusKm > 0 ? _planetRadiusKm : throw new InvalidOperationException("TileLayers.PlanetRadiusKm has not been set.");
        set => _planetRadiusKm = value > 0 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "The planet radius must be positive.");
    }

    /// <summary>Number of tiles.</summary>
    public int Count => Grid.Count;

    /// <summary>Tile that <paramref name="tile"/> drains to (from <see cref="DownstreamSlot"/>), −1 for none.</summary>
    public int DownstreamOf(int tile)
    {
        int slot = DownstreamSlot[tile];
        if (slot < 0) return -1;
        Span<int> ring = stackalloc int[6];
        Grid.Neighbours(tile, ring);
        return ring[slot];
    }

    /// <summary>Height above the tile sea level in metres, 0 at or below it.</summary>
    public int HeightAboveSea(int tile) => Math.Max(0, ElevationM[tile] - SeaLevelM);
}
