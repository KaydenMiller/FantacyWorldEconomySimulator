using WorldEcon.Rendering;
using WorldEcon.WorldGen.Grid;

namespace WorldEcon.Gui.ViewModels;

/// <summary>
/// The map control's state: the <see cref="MapViewState"/> (centre, scale, viewport; level and flatness follow from the
/// scale), the colour layer and overlay toggles, hover, selection and highlighted globe cells. Pointer coordinates are in
/// the map control's pixels (device-independent). <see cref="Invalidated"/> fires when something drawn changed, so the
/// view redraws on demand only. Framework-free.
/// </summary>
public sealed class MapViewModel : ObservableObject
{
    /// <summary>Region-cell size (pixels) that "zoom to Region View" sets: 45 px, inside the 30–90 px flatness ramp (spec §11).</summary>
    public const double RegionViewCellPx = 45;

    /// <summary>Tile size (pixels) that <see cref="ZoomToLevel"/> sets for Detail View: 26 px (Detail starts at 22 px).</summary>
    public const double DetailViewTilePx = 26;

    /// <summary>Scale factor per wheel notch.</summary>
    public const double WheelStep = 1.2;

    private readonly WorldSession _session;
    private MapViewState _view;
    private int _layerIndex = (int)GlobeLayer.Biome;
    private bool _borders;
    private bool _rivers = true;
    private bool _lakes = true;
    private bool _mountainSymbols = true;
    private bool _settlements = true;
    private bool _labels = true;
    private bool _arrows = true;
    private bool _equator = true;
    private bool _graticule;
    private bool _riverArrows;
    private bool _hexGrid;
    private bool _riverEnds;
    private MapPick? _selected;
    private MapPick? _hover;
    private IReadOnlySet<int>? _highlighted;
    private MapLevel _level;
    private bool _gpuActive;

    public MapViewModel(WorldSession session)
    {
        _session = session;
        var centre = session.Markers.Count > 0 ? session.Layers.Geometry.Centre(session.Markers[0].GlobeCell) : new Vector3d(0, 0, 1);
        var v = session.World.View(centre, 1, 800, 600);
        _view = v.WithScale(v.MinScale);
        _level = Renderer.EffectiveLevel(_view);
    }

    /// <summary>Raised when the picture changed (camera, layer, toggles, hover, selection, highlight, viewport).</summary>
    public event EventHandler? Invalidated;

    public MapRenderer Renderer => _session.Renderer;

    /// <summary>
    /// True while the GPU backend draws the cells and rivers of the current view (set by the GPU control); the map
    /// control then draws only the overlay on top. Changing it redraws.
    /// </summary>
    public bool GpuActive
    {
        get => _gpuActive;
        set
        {
            if (Set(ref _gpuActive, value)) Invalidate();
        }
    }

    public MapViewState View => _view;

    /// <summary>Level drawn for the current view (limited by what the world holds).</summary>
    public MapLevel Level => _level;

    /// <summary>"Globe", "Region" or "Detail" (status bar).</summary>
    public string LevelName => _level.ToString();

    public bool IsGlobeLevel => _level == MapLevel.Globe;

    public IReadOnlyList<string> LayerNames { get; } = Enum.GetNames<GlobeLayer>();

    public int LayerIndex
    {
        get => _layerIndex;
        set
        {
            if (value < 0 || value >= LayerNames.Count) return;
            if (Set(ref _layerIndex, value)) Invalidate();
        }
    }

    public GlobeLayer Layer => (GlobeLayer)_layerIndex;

    public bool Borders { get => _borders; set => Toggle(ref _borders, value); }

    public bool Rivers { get => _rivers; set => Toggle(ref _rivers, value); }

    public bool RiverArrows { get => _riverArrows; set => Toggle(ref _riverArrows, value); }

    public bool HexGrid { get => _hexGrid; set => Toggle(ref _hexGrid, value); }

    public bool RiverEnds { get => _riverEnds; set => Toggle(ref _riverEnds, value); }

    public bool Lakes { get => _lakes; set => Toggle(ref _lakes, value); }

    public bool MountainSymbols { get => _mountainSymbols; set => Toggle(ref _mountainSymbols, value); }

    public bool Settlements { get => _settlements; set => Toggle(ref _settlements, value); }

    public bool Labels { get => _labels; set => Toggle(ref _labels, value); }

    public bool Arrows { get => _arrows; set => Toggle(ref _arrows, value); }

    public bool Equator { get => _equator; set => Toggle(ref _equator, value); }

    public bool Graticule { get => _graticule; set => Toggle(ref _graticule, value); }

    public MapPick? Selected
    {
        get => _selected;
        set
        {
            if (Set(ref _selected, value)) Invalidate();
        }
    }

    public MapPick? Hover
    {
        get => _hover;
        private set
        {
            if (Set(ref _hover, value)) Invalidate();
        }
    }

    /// <summary>Globe cells drawn tinted (the selected area's members), or null.</summary>
    public IReadOnlySet<int>? Highlighted
    {
        get => _highlighted;
        set
        {
            if (Set(ref _highlighted, value)) Invalidate();
        }
    }

    public MapDrawOptions DrawOptions => new()
    {
        Layer = Layer,
        Borders = _borders,
        Rivers = _rivers,
        RiverArrows = _riverArrows,
        HexGrid = _hexGrid,
        RiverEnds = _riverEnds,
        Lakes = _lakes,
        MountainSymbols = _mountainSymbols,
        Settlements = _settlements,
        Labels = _labels,
        Arrows = _arrows,
        Equator = _equator,
        Graticule = _graticule,
        Markers = _session.Markers,
        Selected = _selected,
        Hover = _hover,
        HighlightedGlobeCells = _highlighted,
    };

    public void SetViewport(double width, double height)
    {
        if (width <= 0 || height <= 0 || (width == _view.Width && height == _view.Height)) return;
        var atGlobe = _view.ScalePxPerRad <= _view.MinScale * 1.000001;
        var v = _view.WithSize(width, height);
        // A whole-globe view stays a whole-globe view when the window size changes (MinScale follows the size).
        SetView(atGlobe ? v.WithScale(v.MinScale) : v);
    }

    /// <summary>Drag by (dx, dy) pixels: the map follows the pointer.</summary>
    public void Pan(double dxPx, double dyPx)
    {
        if (dxPx == 0 && dyPx == 0) return;
        SetView(_view.Pan(dxPx, dyPx));
    }

    /// <summary>Mouse wheel at (x, y): each notch scales by <see cref="WheelStep"/>, keeping the point under the cursor in place.</summary>
    public void ZoomBy(double wheelDelta, double x, double y)
    {
        if (wheelDelta == 0) return;
        SetView(_view.ZoomAt(Math.Pow(WheelStep, wheelDelta), x, y));
    }

    /// <summary>Region View centred on <paramref name="centre"/> (region cells <see cref="RegionViewCellPx"/> wide).</summary>
    public void ZoomToRegion(Vector3d centre) =>
        SetView(_view.CentreOn(centre).WithScale(_session.World.ScaleForRegionPx(RegionViewCellPx)));

    /// <summary>Globe View (whole globe) keeping the centre.</summary>
    public void ZoomOutToGlobe() => SetView(_view.WithScale(_view.MinScale));

    /// <summary>Sets the scale where <paramref name="level"/> is shown: whole globe, region cells 45 px, or tiles 26 px.</summary>
    public void ZoomToLevel(MapLevel level) => SetView(_view.WithScale(level switch
    {
        MapLevel.Globe => _view.MinScale,
        MapLevel.Region => _session.World.ScaleForRegionPx(RegionViewCellPx),
        _ => _session.World.ScaleForTilePx(DetailViewTilePx),
    }));

    /// <summary>Turns the view so <paramref name="direction"/> is at the centre (scale unchanged).</summary>
    public void CentreOn(Vector3d direction) => SetView(_view.CentreOn(direction));

    /// <summary>What is under (x, y) at the current level, or null off the visible map.</summary>
    public MapPick? PickAt(double x, double y) => Renderer.Pick(_view, x, y, _borders);

    public void HoverAt(double x, double y) => Hover = PickAt(x, y);

    public void ClearHover() => Hover = null;

    /// <summary>Loads the tile blocks of the visible globe cells at Detail View (completes at once at other levels).</summary>
    public Task LoadVisibleTilesAsync() => Renderer.LoadVisibleAsync(_view);

    /// <summary>Unit vector of a pick's item centre (globe cell, region cell or tile).</summary>
    public Vector3d CentreOf(MapPick pick) => pick.Level switch
    {
        MapLevel.Globe => _session.Layers.Geometry.Centre(pick.Id),
        MapLevel.Region => _session.Regions.Geometry.Centre(pick.Id),
        _ => _session.World.Tiles!.Centre(pick.Id),
    };

    private void SetView(MapViewState v)
    {
        _view = v;
        var level = Renderer.EffectiveLevel(v);
        if (level != _level)
        {
            _level = level;
            Raise(nameof(Level));
            Raise(nameof(LevelName));
            Raise(nameof(IsGlobeLevel));
        }

        Raise(nameof(View));
        Invalidate();
    }

    private void Toggle(ref bool field, bool value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (Set(ref field, value, name)) Invalidate();
    }

    private void Invalidate() => Invalidated?.Invoke(this, EventArgs.Empty);
}
