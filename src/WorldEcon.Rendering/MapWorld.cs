using WorldEcon.WorldGen.Grid;
using WorldEcon.WorldGen.Pipeline;
using WorldEcon.WorldGen.Tiles;

namespace WorldEcon.Rendering;

/// <summary>
/// The data <see cref="MapRenderer"/> draws: globe layers (with the tile-derived summaries), the region-cell summary,
/// lakes, and a source of tile blocks. Tiles are not held in memory; the renderer loads blocks of visible globe cells.
/// Without <see cref="Regions"/> only Globe View is drawn; without <see cref="TileSource"/> Detail View falls back to
/// region cells.
/// </summary>
public sealed class MapWorld
{
    /// <param name="globe">Globe layers.</param>
    /// <param name="regions">Region-cell summary; null for a globe-only world.</param>
    /// <param name="lakes">Tile lakes (for details; drawing uses <see cref="RegionSummary.InLargeLake"/>).</param>
    /// <param name="tileMultiplier">Grid.TileMultiplier: tile frequency = region frequency × this.</param>
    /// <param name="tileSource">Tile blocks per globe cell; null draws region cells at Detail View.</param>
    /// <param name="riverMinFlow">Tiles.RiverMinFlow: smallest flow drawn as a river at Detail View.</param>
    /// <param name="majorRiverFlow">Tiles.MajorRiverFlow: smallest region flow drawn as a river at Globe and Region View.</param>
    /// <param name="hydrology">Hydrology settings of the world (RiverMinSystemEdges, RiverTopTierPercentileBp): the
    /// short-system rule applied to the region-cell river segments, as to the globe river edges; null uses the defaults.</param>
    public MapWorld(
        WorldLayers globe,
        RegionSummary? regions = null,
        IReadOnlyList<TileLake>? lakes = null,
        int tileMultiplier = 1,
        ITileBlockSource? tileSource = null,
        long riverMinFlow = 600_000,
        long majorRiverFlow = 50_000_000,
        HydrologySettings? hydrology = null)
    {
        ArgumentNullException.ThrowIfNull(globe);
        ArgumentOutOfRangeException.ThrowIfLessThan(tileMultiplier, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(riverMinFlow, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(majorRiverFlow, 1);
        if (regions is not null && regions.Grid.Frequency % globe.Grid.Frequency != 0)
            throw new ArgumentException($"Region frequency {regions.Grid.Frequency} is not a multiple of the globe frequency {globe.Grid.Frequency}.", nameof(regions));
        Globe = globe;
        Regions = regions;
        Lakes = lakes ?? [];
        GlobeTiles = new TileGrid(globe.Grid.Frequency);
        if (regions is not null)
        {
            RegionTiles = new TileGrid(regions.Grid.Frequency);
            Tiles = new TileGrid(checked(regions.Grid.Frequency * tileMultiplier));
        }

        TileSource = regions is null ? null : tileSource;
        RiverMinFlow = riverMinFlow;
        MajorRiverFlow = majorRiverFlow;
        Hydrology = hydrology ?? new HydrologySettings();
        GlobeCellAngle = MapViewState.MeanAngularSpacing(globe.Grid.CellCount);
        RegionCellAngle = regions is null ? GlobeCellAngle / 3 : MapViewState.MeanAngularSpacing(regions.Count);
        TileAngle = Tiles is null ? RegionCellAngle / 8 : MapViewState.MeanAngularSpacing(Tiles.Count);
    }

    /// <summary>A generated world with its in-memory tile source.</summary>
    /// <param name="withImagery">False leaves out the imagery and river courses (flat cell colours, region-cell rivers).</param>
    public static MapWorld FromGenerated(GeneratedWorld generated, WorldGenSettings settings, ITileBlockSource? tileSource = null, bool withImagery = true)
    {
        ArgumentNullException.ThrowIfNull(generated);
        ArgumentNullException.ThrowIfNull(settings);
        var source = tileSource ?? (generated.Tiles is { } t ? new InMemoryTileBlockSource(t) : null);
        return new MapWorld(generated.Layers, generated.Regions, generated.Lakes, settings.Grid.TileMultiplier, source,
            settings.Tiles.RiverMinFlow, settings.Tiles.MajorRiverFlow, settings.Hydrology)
        { Imagery = withImagery ? generated.Imagery : null, RiverCourses = withImagery ? generated.RiverCourses : null };
    }

    public WorldLayers Globe { get; }

    /// <summary>
    /// Stored per-globe-cell texel samples; with them the map draws continuous imagery (each cell shows its tiles' land and
    /// water instead of one colour). Null draws flat cell colours. Must match the globe grid.
    /// </summary>
    public CellImagery? Imagery
    {
        get => _imagery;
        init
        {
            if (value is not null && value.CellCount != Globe.Grid.CellCount)
                throw new ArgumentException($"Imagery holds {value.CellCount} globe cells; the globe has {Globe.Grid.CellCount}.", nameof(Imagery));
            _imagery = value;
        }
    }

    private readonly CellImagery? _imagery;

    /// <summary>Major river edges at tile resolution; with them Globe and Region View rivers are traced from the tiles. Needs <see cref="Tiles"/>.</summary>
    public RiverCourses? RiverCourses { get; init; }

    public RegionSummary? Regions { get; }

    public IReadOnlyList<TileLake> Lakes { get; }

    /// <summary>Implicit grid at the globe frequency (same ids as <see cref="WorldLayers.Grid"/>), for <see cref="TileGrid.Nearest"/>.</summary>
    public TileGrid GlobeTiles { get; }

    /// <summary>Implicit grid at the region frequency (same ids as <see cref="RegionSummary.Grid"/>); null without regions.</summary>
    public TileGrid? RegionTiles { get; }

    /// <summary>Tile grid; null without regions.</summary>
    public TileGrid? Tiles { get; }

    public ITileBlockSource? TileSource { get; }

    public long RiverMinFlow { get; }

    public long MajorRiverFlow { get; }

    /// <summary>Short-system rule for drawn region rivers (<see cref="TileSummaryStage.DropShortSystems"/>).</summary>
    public HydrologySettings Hydrology { get; }

    /// <summary>Mean angular spacing of globe cells, radians.</summary>
    public double GlobeCellAngle { get; }

    /// <summary>Mean angular spacing of region cells, radians (globe spacing / 3 without regions).</summary>
    public double RegionCellAngle { get; }

    /// <summary>Mean angular spacing of tiles, radians (region spacing / 8 without tiles).</summary>
    public double TileAngle { get; }

    /// <summary>Deepest level this world can draw: Globe without regions, Region without a tile source, else Detail.</summary>
    public MapLevel MaxLevel => Regions is null ? MapLevel.Globe : TileSource is null ? MapLevel.Region : MapLevel.Detail;

    /// <summary>A camera for this world (angles from the grid sizes).</summary>
    public MapViewState View(Vector3d centre, double scalePxPerRad, double width, double height, MapLevelThresholds? thresholds = null) =>
        new(centre, scalePxPerRad, width, height, RegionCellAngle, TileAngle, thresholds);

    /// <summary>A camera from yaw/pitch (as <see cref="GlobeViewState"/>) and a zoom factor (1 = whole globe, disc radius 0.45 × the shorter side).</summary>
    public MapViewState ViewFromYawPitch(double width, double height, double yawDeg, double pitchDeg, double zoom = 1, MapLevelThresholds? thresholds = null) =>
        MapViewState.FromYawPitch(width, height, yawDeg, pitchDeg, zoom, RegionCellAngle, TileAngle, thresholds);

    /// <summary>Scale (pixels per radian) at which a region cell spans <paramref name="px"/> pixels.</summary>
    public double ScaleForRegionPx(double px) => px / RegionCellAngle;

    /// <summary>Scale (pixels per radian) at which a tile spans <paramref name="px"/> pixels.</summary>
    public double ScaleForTilePx(double px) => px / TileAngle;
}
