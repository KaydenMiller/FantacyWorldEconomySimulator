using System.Globalization;
using Microsoft.Data.Sqlite;
using WorldEcon.Application.Queries;
using WorldEcon.Persistence.Globe;
using WorldEcon.Seeding;
using WorldEcon.Rendering;
using WorldEcon.WorldGen.Grid;
using WorldEcon.WorldGen.Pipeline;

namespace WorldEcon.Gui.ViewModels;


/// <summary>
/// The window's state: the opened world, the map (<see cref="MapViewModel"/>: Globe, Region and Detail View in one
/// control), the details panel, the Layers and Areas tabs and the status bar. Selection rules: a click selects what is
/// under the pointer at the current level (globe cell, region cell or tile) and shows its details (a tile's details need
/// its tile block, loaded off the calling thread: "loading…" first, then the values); Zoom to region (button,
/// double-click, Enter) shows Region View centred on the selected globe cell or region cell; Back to globe (button, Esc)
/// zooms out to Globe View keeping the centre. Framework-free: the Avalonia views forward pointer and key input here.
/// <para>
/// Errors: opening a database and creating a world catch every exception except <see cref="OperationCanceledException"/>;
/// the message goes to <see cref="StatusText"/> (one line) and to <see cref="ErrorText"/> (shown in full over the map).
/// Loading a database runs on a worker thread with <see cref="IsBusy"/> set; while busy, the file actions are refused.
/// </para>
/// </summary>
public sealed class MainViewModel : ObservableObject
{
    /// <summary>Appended to load errors that come from a database written by an older schema (missing table or column).</summary>
    public const string RegenerateHint = "The database was written by an older version of this program; regenerate the world (File → New world, or the CLI command `new`).";

    /// <summary>Rows shown while a tile's block loads.</summary>
    public const string LoadingText = "loading…";

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private WorldSession? _session;
    private StageViewerViewModel? _tuning;
    private MapViewModel? _map;
    private AreasViewModel _areas = AreasViewModel.Empty;
    private string _detailsTitle = "Nothing selected";
    private IReadOnlyList<DetailRow> _detailRows = [];
    private string _statusText = "No world open. File → Open… or File → New world…";
    private bool _isBusy;
    private string _busyText = "";
    private double _busyPercent;
    private bool _busyPercentKnown;
    private string _errorText = "";
    private string _areaDetailsTitle = "No area selected";
    private IReadOnlyList<DetailRow> _areaDetailRows = [];
    private int _detailsRequest;
    private CaravanLayer? _caravans;
    private string? _hoverText;
    private NodeNameEditor? _nodeEditor;
    private TimeControlsViewModel? _time;
    private CityPanelViewModel? _city;
    private int _caravanRequest;
    private PanelWindows _windows = new(null);
    private int _sideTabIndex;

    public MainViewModel()
    {
        ZoomToRegionCommand = new RelayCommand(() => ZoomToRegion(), () => CanZoomToRegion);
        BackCommand = new RelayCommand(() => BackToGlobe(), () => CanGoBack);
    }

    public RelayCommand ZoomToRegionCommand { get; }

    public RelayCommand BackCommand { get; }

    /// <summary>Caravans of the open world (empty for a world without a file); null without a world.</summary>
    public CaravanLayer? Caravans => _caravans;

    /// <summary>Completes when the caravan query started by the last <see cref="UseSession"/> or <see cref="RefreshCaravansAsync"/> finished.</summary>
    public Task CaravansLoaded { get; private set; } = Task.CompletedTask;

    /// <summary>Raised with a settlement id when a settlement marker is clicked or a settlement link is opened.</summary>
    public event EventHandler<Guid>? SettlementRequested;

    /// <summary>Index of the Details tab in the sidebar.</summary>
    public const int DetailsTabIndex = 0;

    /// <summary>Index of the City tab in the sidebar.</summary>
    public const int CityTabIndex = 1;

    /// <summary>Index of the Layers tab in the sidebar.</summary>
    public const int LayersTabIndex = 2;

    /// <summary>Index of the Areas tab in the sidebar.</summary>
    public const int AreasTabIndex = 3;

    /// <summary>Selected sidebar tab (see the *TabIndex constants).</summary>
    public int SideTabIndex
    {
        get => _sideTabIndex;
        set => Set(ref _sideTabIndex, value);
    }

    /// <summary>
    /// Pop-out windows. The window sets a desktop host; without one (tests, single-window platforms) pop-outs are not
    /// offered. Setting it gives new windows city panels whose row clicks act on this map.
    /// </summary>
    public PanelWindows Windows
    {
        get => _windows;
        set
        {
            _windows.CloseAll();
            _windows = value ?? new PanelWindows(null);
            _windows.CityFactory ??= (session, _) => new CityPanelViewModel(session,
                obj => { if (ReferenceEquals(session, _session)) { SelectObject(obj); _windows.ActivateMain(); } },
                route => { if (ReferenceEquals(session, _session) && _map is not null) { _map.HighlightedRoute = route; _windows.ActivateMain(); } },
                id => { if (ReferenceEquals(session, _session)) CentreOnSettlement(id); });
            if (_city is not null) _city.CanPopOut = _windows.Supported;
            Raise(nameof(Windows));
        }
    }

    /// <summary>Right-click at a map pixel: a settlement marker there opens in its own window. False when nothing was opened.</summary>
    public bool ContextClick(double x, double y)
    {
        if (_map is null || !_windows.Supported) return false;
        // Only settlements: a caravan leaving the town sits on its marker and would otherwise win the pick.
        if (_map.PickObjects(x, y, MapObjectKind.Settlement).FirstOrDefault() is not { Kind: MapObjectKind.Settlement } s) return false;
        OpenSettlementInWindow(s.Id);
        return true;
    }

    /// <summary>Opens a settlement in its own window (desktop only; ignored without a host or world).</summary>
    public void OpenSettlementInWindow(Guid id)
    {
        if (!_windows.Supported || _session is null) return;
        _ = _windows.OpenCityAsync(_session, id);
    }

    /// <summary>The docked city panel (follows the selected settlement); null without a world.</summary>
    public CityPanelViewModel? City
    {
        get => _city;
        private set => Set(ref _city, value);
    }

    /// <summary>The name box for the selected junction or site; null when something else is selected.</summary>
    public NodeNameEditor? NodeEditor
    {
        get => _nodeEditor;
        private set
        {
            if (Set(ref _nodeEditor, value)) Raise(nameof(HasNodeEditor));
        }
    }

    public bool HasNodeEditor => _nodeEditor is not null;

    /// <summary>The time bar of the open world; null without a world.</summary>
    public TimeControlsViewModel? Time
    {
        get => _time;
        private set => Set(ref _time, value);
    }

    /// <summary>Tests: replaces the time bar (for example one over a scripted run).</summary>
    internal void ReplaceTime(TimeControlsViewModel time)
    {
        _time?.Dispose();
        Time = time;
    }

    /// <summary>Tooltip text for the caravan, settlement or node under the pointer; null when none.</summary>
    public string? HoverText
    {
        get => _hoverText;
        private set => Set(ref _hoverText, value);
    }

    public WorldSession? Session
    {
        get => _session;
        private set => Set(ref _session, value);
    }

    public MapViewModel? Map
    {
        get => _map;
        private set => Set(ref _map, value);
    }

    public AreasViewModel Areas
    {
        get => _areas;
        private set => Set(ref _areas, value);
    }

    public bool HasWorld => _session is not null;

    /// <summary>Tuning mode's stage viewer (File → Tune generation…); null outside tuning mode.</summary>
    public StageViewerViewModel? Tuning
    {
        get => _tuning;
        private set
        {
            if (!Set(ref _tuning, value)) return;
            Raise(nameof(IsTuning));
            Raise(nameof(ShowsMap));
        }
    }

    public bool IsTuning => _tuning is not null;

    /// <summary>True when the map is shown: an open world or tuning mode.</summary>
    public bool ShowsMap => HasWorld || IsTuning;

    /// <summary>
    /// Enters tuning mode: the open world is closed and the map follows <see cref="Tuning"/>'s current stage (the stage's
    /// layer is applied; the camera is kept while stepping). <paramref name="uiDispatch"/> posts generation results to the
    /// user interface thread.
    /// </summary>
    public void EnterTuning(GenerationSettingsViewModel settings, Func<Action, Task> uiDispatch)
    {
        LeaveTuning();
        var old = CloseSession();
        if (old is not null) _ = Task.Run(old.Dispose);
        var tuning = new StageViewerViewModel(settings, uiDispatch);
        tuning.PropertyChanged += OnTuningChanged;
        Tuning = tuning;
        StatusText = "Tuning mode: choose a seed and settings, then Run. Nothing is written until Save as world.";
    }

    /// <summary>Leaves tuning mode and clears the map (no world is open afterwards).</summary>
    public void LeaveTuning()
    {
        if (_tuning is not { } tuning) return;
        tuning.PropertyChanged -= OnTuningChanged;
        if (_map is not null)
        {
            _map.PropertyChanged -= OnMapChanged;
            _map.PropertyChanged -= OnTuningMapChanged;
        }

        Map = null;
        Tuning = null;
        tuning.Dispose();
        RaiseMapState();
    }

    /// <summary>Seeds a world database from the finished tuning run (no second generation) and opens it, leaving tuning mode.</summary>
    public async Task<bool> SaveTunedWorldAsync(string path)
    {
        if (_tuning is not { World: { } world, RunSettings: { } settings } tuning) return false;
        return await CreateDemoWorldAsync(path, settings, overwrite: true, seed: tuning.RunSeed, generated: world);
    }

    private void OnTuningChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is not StageViewerViewModel tuning || !ReferenceEquals(tuning, _tuning)) return;
        if (e.PropertyName == nameof(StageViewerViewModel.CurrentSession))
        {
            var old = _map;
            if (old is not null)
            {
                old.PropertyChanged -= OnMapChanged;
                old.PropertyChanged -= OnTuningMapChanged;
            }

            if (tuning.CurrentSession is not { } session)
            {
                Map = null;
            }
            else
            {
                var map = new MapViewModel(session) { LayerIndex = (int)tuning.CurrentLayer };
                if (old is not null)
                {
                    map.SetViewport(old.View.Width, old.View.Height);
                    if (old.Renderer.World.Globe.Grid.CellCount == session.Layers.Grid.CellCount) map.CopyViewFrom(old);
                }

                map.PropertyChanged += OnMapChanged;
                map.PropertyChanged += OnTuningMapChanged;
                Map = map;
            }

            RaiseMapState();
        }
        else if (e.PropertyName == nameof(StageViewerViewModel.CurrentLayer) && _map is { } m && m.Layer != tuning.CurrentLayer)
        {
            m.LayerIndex = (int)tuning.CurrentLayer;
        }
    }

    // A layer picked on the map in tuning mode becomes the viewer's layer (kept while stepping).
    private void OnTuningMapChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MapViewModel.LayerIndex) && sender is MapViewModel map && _tuning is { } t && t.CurrentLayer != map.Layer)
            t.CurrentLayer = map.Layer;
    }

    /// <summary>"Globe", "Region" or "Detail"; empty without a world.</summary>
    public string LevelName => _map?.LevelName ?? "";

    /// <summary>Back to globe is possible: the map shows Region or Detail View.</summary>
    public bool CanGoBack => _map is { IsGlobeLevel: false };

    /// <summary>
    /// Zoom to region is possible: a globe cell or region cell is selected and the map is not at Detail View (there a
    /// region-cell pick is the fallback for a tile block that is not loaded, and zooming to Region View would zoom out).
    /// </summary>
    public bool CanZoomToRegion => _map is { Level: not MapLevel.Detail, Selected: { Level: MapLevel.Globe or MapLevel.Region } };

    /// <summary>File → Open, File → New world and tuning are enabled (not while a load or seeding runs, nor while a simulation run is open).</summary>
    public bool CanUseFileActions => !_isBusy && !IsTimeRunning;

    /// <summary>A simulation run is open on the world's file (<see cref="WorldSession.ActiveRun"/>).</summary>
    public bool IsTimeRunning => _session?.ActiveRun is not null;

    private void OnActiveRunChanged(object? sender, EventArgs e)
    {
        if (!ReferenceEquals(sender, _session)) return;
        Raise(nameof(IsTimeRunning));
        Raise(nameof(CanUseFileActions));
        _nodeEditor?.RunStateChanged();
    }

    public string DetailsTitle
    {
        get => _detailsTitle;
        private set => Set(ref _detailsTitle, value);
    }

    public IReadOnlyList<DetailRow> DetailRows
    {
        get => _detailRows;
        private set => Set(ref _detailRows, value);
    }

    /// <summary>Completes when the details of the last selection are filled (a tile's block loaded); for tests and scripts.</summary>
    public Task DetailsLoaded { get; private set; } = Task.CompletedTask;

    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!Set(ref _isBusy, value)) return;
            Raise(nameof(CanUseFileActions));
        }
    }

    /// <summary>The last error in full (empty when the last action succeeded); the window shows it over the map.</summary>
    public string ErrorText
    {
        get => _errorText;
        private set
        {
            if (Set(ref _errorText, value)) Raise(nameof(HasError));
        }
    }

    public bool HasError => _errorText.Length > 0;

    private string _warningsText = "";

    /// <summary>Warnings from the last File → New world (pins, roads, ports), one per line; empty when there were none or they were dismissed.</summary>
    public string WarningsText
    {
        get => _warningsText;
        private set
        {
            if (Set(ref _warningsText, value)) Raise(nameof(HasWarnings));
        }
    }

    public bool HasWarnings => _warningsText.Length > 0;

    /// <summary>Hides the seeding warnings panel.</summary>
    public void DismissWarnings() => WarningsText = "";

    /// <summary>Title of the Areas tab's detail pane (the selected area).</summary>
    public string AreaDetailsTitle
    {
        get => _areaDetailsTitle;
        private set => Set(ref _areaDetailsTitle, value);
    }

    /// <summary>Rows of the Areas tab's detail pane, so an area's details show beside the list it was picked from.</summary>
    public IReadOnlyList<DetailRow> AreaDetailRows
    {
        get => _areaDetailRows;
        private set => Set(ref _areaDetailRows, value);
    }

    public string BusyText
    {
        get => _busyText;
        private set => Set(ref _busyText, value);
    }

    /// <summary>Progress of the running seeding, 0–100 (valid when <see cref="BusyPercentKnown"/>).</summary>
    public double BusyPercent
    {
        get => _busyPercent;
        private set => Set(ref _busyPercent, value);
    }

    /// <summary>True while seeding reports progress; false for loads (the progress bar is then indeterminate).</summary>
    public bool BusyPercentKnown
    {
        get => _busyPercentKnown;
        private set
        {
            if (Set(ref _busyPercentKnown, value)) Raise(nameof(BusyPercentUnknown));
        }
    }

    public bool BusyPercentUnknown => !_busyPercentKnown;

    /// <summary>Presets offered by File → New world.</summary>
    public IReadOnlyList<string> PresetNames => WorldGenPresets.Names;

    /// <summary>Shows a loaded world: a fresh map at Globe View, the Areas list, empty details. The previous session is disposed.</summary>
    public void UseSession(WorldSession session)
    {
        LeaveTuning();
        var old = _session;
        if (_map is not null) _map.PropertyChanged -= OnMapChanged;
        Session = session;
        Map = new MapViewModel(session);
        _map!.PropertyChanged += OnMapChanged;
        Areas = new AreasViewModel(session.Layers.Areas, OnAreaSelected);
        _caravans = new CaravanLayer(session);
        var map = _map;
        // A copy per update: the render thread draws from options captured earlier while the layer is rewritten here.
        _caravans.Changed += (_, _) => map.SetCaravans(_caravans?.OnRoute.ToArray());
        if (old is not null)
        {
            old.DataChanged -= OnDataChanged;
            old.ActiveRunChanged -= OnActiveRunChanged;
        }

        session.DataChanged += OnDataChanged;
        session.ActiveRunChanged += OnActiveRunChanged;
        _windows.CloseAll(); // pop-outs of the old world close before it is disposed
        _city?.Detach();
        City = NewCityPanel(session);
        _city!.CanPopOut = _windows.Supported;
        var city = _city;
        _city.PopOutRequested += (_, _) => { if (city.SettlementId is { } id) OpenSettlementInWindow(id); };
        CaravansLoaded = RefreshCaravansAsync();
        _time?.Dispose();
        Time = new TimeControlsViewModel(this, session);
        AreaDetailsTitle = "No area selected";
        AreaDetailRows = [];
        Raise(nameof(HasWorld));
        Raise(nameof(IsTimeRunning));
        Raise(nameof(CanUseFileActions));
        Raise(nameof(ShowsMap));
        ShowNothing();
        RaiseMapState();
        // Closing waits for tile loads inside the old source; off the user interface thread.
        if (old is not null && !ReferenceEquals(old, session)) _ = Task.Run(old.Dispose);
    }

    /// <summary>Loads a database on a worker thread, then shows it. Errors are reported (status bar and error text); false when busy.</summary>
    public async Task<bool> OpenAsync(string path)
    {
        if (IsBusy) return false;
        IsBusy = true;
        BusyPercentKnown = false;
        BusyText = $"Loading {Path.GetFileName(path)}…";
        try
        {
            var session = await Task.Run(() => WorldSession.LoadAsync(path));
            UseSession(session);
            ClearError();
            // Finish with the caravans read: once OpenAsync returns nothing reads the file (it may be replaced or deleted).
            await CaravansLoaded;
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ReportError($"Could not open {path}", ex);
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Generates and seeds a world on a worker thread (File → New world), then opens it. <paramref name="settings"/> null
    /// uses the <see cref="WorldGenPresets.Earth5Km"/> preset; <see cref="BusyText"/> and <see cref="BusyPercent"/> show
    /// the generation and write steps. Seeding writes a temporary file beside <paramref name="path"/> and moves it over
    /// <paramref name="path"/> only when seeding succeeded, so a failure leaves no partial file and keeps an existing file.
    /// An existing file is replaced only with <paramref name="overwrite"/> (the save dialog's overwrite confirmation).
    /// When the replaced file is the open world (<see cref="SameFile"/>), the open world is closed first (its tile reads use
    /// the file); if the replace then fails, the unchanged old file is opened again and the error says so.
    /// </summary>
    public async Task<bool> CreateDemoWorldAsync(string path, WorldGenSettings? settings = null, bool overwrite = false, ulong seed = DemoSeeder.DefaultSeed,
        GeneratedWorld? generated = null)
    {
        if (IsBusy) return false;
        if (File.Exists(path) && !overwrite)
        {
            ReportError($"Could not create {path}", new IOException("the file already exists; choose a new file name."));
            return false;
        }

        IsBusy = true;
        BusyText = "Generating the world…";
        BusyPercent = 0;
        BusyPercentKnown = true;
        // Created on the user interface thread, so reports from the seeding worker are posted back to it.
        var remaining = new RemainingTime();
        var progress = new Progress<SeedingProgress>(p =>
        {
            BusyText = string.Create(Inv, $"Generating the world: {p.Step} ({p.Fraction * 100:F0} %, {remaining.Text(p.Fraction)})…");
            BusyPercent = p.Fraction * 100;
        });
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var temp = Path.Combine(dir, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        string? closedPath = null;
        IReadOnlyList<string> warnings = [];
        WarningsText = "";
        try
        {
            var (_, report) = await Task.Run(() => DemoSeeder.CreateAsync(temp, settings, progress, seed: seed, generated: generated));
            warnings = report.Warnings;
            // The open world reads tiles from its file: close it (renderer disposed, tile source closed, running loads
            // drained) before the pools are cleared and the file is replaced, so it never reads the new world's blocks.
            if (_session is { Path: { } open } && SameFile(open, path))
            {
                var closing = CloseSession(); // detaches the city panels and pop-outs
                // Every read or write of the file still running (caravans, city panels, renames) finishes before the file is replaced.
                if (closing is not null) await closing.DrainFileWorkAsync();
                closedPath = open;
                await Task.Run(() => closing?.Close());
            }

            await Task.Run(() =>
            {
                // Release the pooled connections so the files can be moved (and deleted on Windows). The last connection
                // to close checkpoints the write-ahead log into the main file. A write-ahead log or shared-memory file left
                // from the replaced database would be applied to the new file, so both are deleted before the move.
                SqliteConnection.ClearPool(new SqliteConnection($"Data Source={temp}"));
                SqliteConnection.ClearPool(new SqliteConnection($"Data Source={path}"));
                SqliteTileBlockSource.ClearPool(temp);
                SqliteTileBlockSource.ClearPool(path);
                foreach (var sidecar in new[] { path + "-wal", path + "-shm", path + "-journal" })
                    if (File.Exists(sidecar)) File.Delete(sidecar);
                File.Move(temp, path, overwrite: true);
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            DeleteQuietly(temp);
            IsBusy = false;
            BusyPercentKnown = false;
            if (closedPath is null)
            {
                ReportError($"Could not create {path}", ex);
                return false;
            }

            // The open world was closed for the replace; the move failed, so its file is unchanged: open it again.
            var reopened = await OpenAsync(closedPath);
            ReportError($"Could not create {path}", new IOException(ex.Message + (reopened
                ? " The previous world was opened again from the unchanged file."
                : " The previous world was closed and could not be opened again: " + StatusText), ex));
            return false;
        }

        IsBusy = false;
        BusyPercentKnown = false;
        var opened = await OpenAsync(path);
        if (opened && warnings.Count > 0)
        {
            WarningsText = $"World created with {warnings.Count} warning{(warnings.Count == 1 ? "" : "s")}:\n" + string.Join("\n", warnings.Select(w => "• " + w));
            StatusText = $"{StatusText} ({warnings.Count} seeding warning{(warnings.Count == 1 ? "" : "s")})";
        }
        return opened;
    }

    /// <summary>Reports an exception from a window event handler (the handlers are <c>async void</c> and must not throw).</summary>
    public void ReportUnexpected(string context, Exception ex) => ReportError(context, ex);

    /// <summary>
    /// Re-reads the caravans of the open world from its file (worker thread), or through <see cref="WorldSession.ActiveRun"/>
    /// while a simulation run is open, and places them at the tick read with them.
    /// Does nothing for a world without a file. Errors go to the status bar and error text.
    /// </summary>
    public async Task RefreshCaravansAsync()
    {
        if (_session is not { Path: { } path } session || _caravans is not { } layer) return;
        var request = ++_caravanRequest;
        try
        {
            // Looked up at call time: while a run is open the caravans are read through it (the file shows the pre-run state).
            var result = session.ActiveRun is { } run
                ? await session.Track(run.CaravanPositionsAsync())
                : await session.Track(Task.Run(() => CaravanQuery(path)));
            // Another world replaced this one, or a later refresh was started: this result is stale.
            if (!ReferenceEquals(session, _session) || session.IsClosed || request != _caravanRequest) return;
            if (result.IsError)
            {
                StatusText = $"Could not load caravans: {result.FirstError.Description}";
                return;
            }

            layer.Load(result.Value);
            RefreshSelectedCaravan(session, layer);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (ReferenceEquals(session, _session)) ReportError("Could not load caravans", ex);
        }
    }

    /// <summary>Delivered caravan note in Details (the caravan left the map with the last read).</summary>
    public const string DeliveredText = "Delivered — no longer on the map";

    /// <summary>
    /// After a caravan read: a selected caravan's details show its new position, or, when it is no longer among the
    /// undelivered caravans, keep its selection and say it was delivered.
    /// </summary>
    private void RefreshSelectedCaravan(WorldSession session, CaravanLayer layer)
    {
        if (_map?.SelectedObject is not { Kind: MapObjectKind.Caravan } selected) return;
        if (layer.Caravan(selected.Id) is { } c)
        {
            (DetailsTitle, DetailRows) = DetailFormatter.Caravan(c, layer, session);
            return;
        }

        if (DetailRows.Any(r => r.Value == DeliveredText)) return;
        DetailRows = [new DetailRow("Status", DeliveredText), .. DetailRows];
        _map.HighlightedRoute = null;
    }

    /// <summary>Reads the caravans of a world file (tests replace it with a stub).</summary>
    internal Func<string, Task<ErrorOr.ErrorOr<CaravanPositions>>> CaravanQuery { get; set; } = path => CaravanPositionsQuery.RunAsync(path);

    /// <summary>
    /// Selects what is under a map pixel: a caravan (several when their markers merged), settlement or junction/site as
    /// last drawn, else the globe cell, region cell or tile at the current level. False when the pixel shows no map.
    /// </summary>
    public bool Click(double x, double y)
    {
        if (_map is null) return false;
        var objects = _map.PickObjects(x, y);
        if (objects.Count == 0 || objects[0].Kind != MapObjectKind.Settlement) LeaveCityTab();
        if (objects.Count > 1 && objects.All(o => o.Kind == MapObjectKind.Caravan))
        {
            SelectCaravans(objects.Select(o => o.Id).ToList());
            return true;
        }

        if (objects.Count > 0)
        {
            SelectObject(objects[0]);
            return true;
        }

        if (_map.PickAt(x, y) is not { } pick) return false;
        Select(pick);
        return true;
    }

    /// <summary>Hover at a map pixel: the cell hover ring, and <see cref="HoverText"/> for a caravan, settlement or node.</summary>
    public void HoverAt(double x, double y)
    {
        if (_map is null || _session is not { } session) return;
        _map.HoverAt(x, y);
        var objects = _map.PickObjects(x, y);
        HoverText = objects.Count == 0 ? null : objects.Count > 1 ? $"{objects.Count} caravans" : objects[0] switch
        {
            { Kind: MapObjectKind.Caravan } o when _caravans?.Caravan(o.Id) is { } c => DetailFormatter.HoverText(c),
            { Kind: MapObjectKind.Settlement } o => session.Data.Settlements.FirstOrDefault(s => s.Id == o.Id)?.Name,
            { Kind: MapObjectKind.Node } o => session.Node(o.Id)?.Label,
            _ => null,
        };
    }

    /// <summary>Selects a caravan, settlement or junction/site and shows its details; a settlement also raises <see cref="SettlementRequested"/>.</summary>
    public void SelectObject(MapObject obj)
    {
        if (_session is not { } session || _map is null) return;
        _detailsRequest++;
        _map.Selected = null;
        _map.SelectedObject = obj;
        _map.HighlightedRoute = null;
        NodeEditor = null;
        DetailsLoaded = Task.CompletedTask;
        switch (obj.Kind)
        {
            case MapObjectKind.Caravan when _caravans?.Caravan(obj.Id) is { } c:
                if (_caravans.TryPosition(c.Id, out var leg, out _)) _map.HighlightedRoute = c.Legs[leg].RouteId;
                (DetailsTitle, DetailRows) = DetailFormatter.Caravan(c, _caravans, session);
                break;
            case MapObjectKind.Node when session.Node(obj.Id) is { } n:
                ShowNode(n);
                NodeEditor = new NodeNameEditor(session, n, ShowNode);
                break;
            case MapObjectKind.Settlement when session.Data.Settlements.FirstOrDefault(s => s.Id == obj.Id) is { } s:
                DetailsTitle = s.Name;
                DetailRows = [new DetailRow("Settlement", s.Name, () => OpenSettlement(s.Id)), new DetailRow("Tile", s.TileId?.ToString(Inv) ?? "none"),
                    new DetailRow("Port", s.Port.ToString()), .. NewWindowRow(s.Id)];
                OpenSettlement(s.Id);
                break;
            default:
                DetailsTitle = "Not found";
                DetailRows = [new DetailRow("Status", "it is no longer in the loaded data")];
                break;
        }
    }

    private void ShowNode(Application.Globe.GlobeNode n)
    {
        // A rename that finishes after the user selected something else must not overwrite the new selection's details.
        if (_session is not { } session || _map?.SelectedObject != new MapObject(MapObjectKind.Node, n.Id) || session.Node(n.Id) is null) return;
        (DetailsTitle, DetailRows) = DetailFormatter.Node(n, session, _caravans, id => SelectObject(new MapObject(MapObjectKind.Caravan, id)));
        _map?.Invalidate();
    }

    /// <summary>Lists caravans whose markers merged; each row selects one.</summary>
    public void SelectCaravans(IReadOnlyList<Guid> ids)
    {
        if (_map is null || _caravans is not { } layer) return;
        _detailsRequest++;
        _map.Selected = null;
        _map.SelectedObject = null;
        _map.HighlightedRoute = null;
        NodeEditor = null;
        DetailsTitle = $"{ids.Count} caravans";
        DetailRows = DetailFormatter.CaravanLinks(ids.Select(layer.Caravan).OfType<CaravanOnMap>(), id => SelectObject(new MapObject(MapObjectKind.Caravan, id)));
        DetailsLoaded = Task.CompletedTask;
    }

    /// <summary>Shows a settlement in the docked city panel, switches the sidebar to the City tab and raises <see cref="SettlementRequested"/>.</summary>
    public void OpenSettlement(Guid id)
    {
        if (_city is not null)
        {
            _ = _city.ShowAsync(id);
            SideTabIndex = CityTabIndex;
        }

        SettlementRequested?.Invoke(this, id);
    }

    /// <summary>A city panel over <paramref name="session"/> whose row clicks act on this window's map.</summary>
    internal CityPanelViewModel NewCityPanel(WorldSession session) => new(session,
        obj => { if (ReferenceEquals(session, _session)) SelectObject(obj); },
        route => { if (ReferenceEquals(session, _session) && _map is not null) _map.HighlightedRoute = route; },
        id => { if (ReferenceEquals(session, _session)) CentreOnSettlement(id); });

    private void CentreOnSettlement(Guid id)
    {
        if (_session is not { } session || _map is null) return;
        if (session.Data.Settlements.FirstOrDefault(s => s.Id == id) is not { } s) return;
        _map.CentreOn(s.TileId is { } t && session.World.Tiles is { } tiles ? tiles.Centre(t) : session.Layers.Geometry.Centre(s.Cell));
    }

    /// <summary>Refresh (any panel, a rename, an advance): the caravans are read again; open panels re-query themselves.</summary>
    private void OnDataChanged(object? sender, EventArgs e)
    {
        if (ReferenceEquals(sender, _session)) CaravansLoaded = RefreshCaravansAsync();
    }

    /// <summary>A map click moves the sidebar off the City tab to Details (other tabs stay).</summary>
    private void LeaveCityTab()
    {
        if (SideTabIndex == CityTabIndex) SideTabIndex = DetailsTabIndex;
    }

    /// <summary>Link rows for the settlements of a tile or globe cell (each with a "New window" row on the desktop).</summary>
    private IReadOnlyList<DetailRow> SettlementLinks(Func<Application.Globe.GlobeSettlement, bool> where) =>
        _session is null ? [] : _session.Data.Settlements.Where(where)
            .SelectMany(s => (IEnumerable<DetailRow>)[new DetailRow("Open city", s.Name, () => OpenSettlement(s.Id)), .. NewWindowRow(s.Id)]).ToList();

    private IReadOnlyList<DetailRow> NewWindowRow(Guid id) =>
        _windows.Supported ? [new DetailRow("New window", "Open in new window", () => OpenSettlementInWindow(id))] : [];

    /// <summary>Double-click: select what is under the pixel and, for a globe cell or region cell, zoom to Region View on it.</summary>
    public bool Activate(double x, double y) => Click(x, y) && ZoomToRegion();

    /// <summary>Selects a globe cell (Globe level pick).</summary>
    public void SelectCell(int cell)
    {
        if (_session is null) return;
        Select(new MapPick(MapLevel.Globe, cell, cell, -1));
    }

    /// <summary>Selects an item and fills the details panel; a tile's details complete when its block is loaded (<see cref="DetailsLoaded"/>).</summary>
    public void Select(MapPick pick)
    {
        if (_session is not { } session || _map is null) return;
        _map.Selected = pick;
        _map.SelectedObject = null;
        _map.HighlightedRoute = null;
        NodeEditor = null;
        LeaveCityTab();
        RaiseMapState();
        var request = ++_detailsRequest;
        switch (pick.Level)
        {
            case MapLevel.Globe:
                ShowCell(pick.Id);
                DetailsLoaded = Task.CompletedTask;
                break;
            case MapLevel.Region:
                DetailsTitle = $"Region cell {pick.Id}";
                DetailRows = DetailFormatter.RegionCell(pick.Id, session);
                DetailsLoaded = Task.CompletedTask;
                break;
            default:
                DetailsTitle = $"Tile {pick.Id}";
                DetailRows = [new DetailRow("Status", LoadingText)];
                DetailsLoaded = ShowTileAsync(session, pick.Id, request);
                break;
        }
    }

    private async Task ShowTileAsync(WorldSession session, int tile, int request)
    {
        IReadOnlyList<DetailRow> rows;
        try
        {
            var cell = session.GlobeCellOfTile(tile);
            var block = await session.TileBlockAsync(cell);
            var i = block is null ? -1 : Array.BinarySearch(block.TileIds, tile);
            rows = block is null
                ? [new DetailRow("Status", $"no tile block is stored for globe cell {cell}; regenerate the world")]
                : i < 0
                    ? [new DetailRow("Status", $"tile {tile} is not in the block of globe cell {cell}")]
                    : [.. DetailFormatter.Tile(tile, block, i, session), .. SettlementLinks(s => s.TileId == tile)];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            rows = [new DetailRow("Status", $"could not load the tile: {ex.Message}")];
        }

        // A later selection or another world replaced this one while the block loaded.
        if (request != _detailsRequest || !ReferenceEquals(session, _session)) return;
        DetailRows = rows;
    }

    /// <summary>Region View centred on the selected globe cell or region cell; false when neither is selected or the map is at Detail View.</summary>
    public bool ZoomToRegion()
    {
        if (_map is null || _map.Level == MapLevel.Detail || _map.Selected is not { Level: MapLevel.Globe or MapLevel.Region } pick) return false;
        _map.ClearHover();
        _map.ZoomToRegion(_map.CentreOf(pick));
        return true;
    }

    /// <summary>Zooms out to Globe View keeping the centre and the selection; false when already there.</summary>
    public bool BackToGlobe()
    {
        if (_map is null || _map.IsGlobeLevel) return false;
        _map.ZoomOutToGlobe();
        return true;
    }

    private void OnMapChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MapViewModel.LevelName)) RaiseMapState();
    }

    private void OnAreaSelected(AreaItem? item)
    {
        if (_session is null || _map is null) return;
        if (item is null)
        {
            _map.Highlighted = null;
            return;
        }

        var area = _session.Layers.Areas[item.Index];
        _map.Highlighted = area.Cells.Select(x => x.Cell).ToHashSet();
        if (FacingDirection(_session.Layers, area.Cells) is { } facing)
        {
            _map.CentreOn(facing);
            BackToGlobe();
        }

        var title = $"{area.Kind} '{area.Name}'";
        var rows = DetailFormatter.Area(area, _session.Layers);
        DetailsTitle = title;
        DetailRows = rows;
        AreaDetailsTitle = title;
        AreaDetailRows = rows;
    }

    /// <summary>
    /// The member cell nearest the mean of the member centres (so the view centre is inside the area even when it is
    /// curved or wraps around); ties: lowest cell id. Null for an area with no cells.
    /// </summary>
    private static Vector3d? FacingDirection(WorldLayers l, IReadOnlyList<(int Cell, short CoverageBp)> cells)
    {
        if (cells.Count == 0) return null;
        var sum = new Vector3d(0, 0, 0);
        foreach (var (c, _) in cells) sum += l.Geometry.Centre(c);
        var best = cells[0].Cell;
        if (sum.Length > 1e-9)
        {
            var mean = sum.Normalize();
            var bestDot = double.MinValue;
            foreach (var (c, _) in cells)
            {
                var d = Vector3d.Dot(l.Geometry.Centre(c), mean);
                if (d > bestDot)
                {
                    bestDot = d;
                    best = c;
                }
            }
        }

        return l.Geometry.Centre(best);
    }

    private void ShowCell(int cell)
    {
        if (_session is null) return;
        DetailsTitle = $"Globe cell {cell}";
        DetailRows = [.. DetailFormatter.Cell(_session.CellDetails(cell), _session.Layers), .. SettlementLinks(s => s.Cell == cell)];
    }

    private void ShowNothing()
    {
        DetailsTitle = "Nothing selected";
        DetailRows = [new DetailRow("Hint", "Click the map to see details of a globe cell, region cell or tile; double-click or press Enter to zoom to Region View, Esc to return to Globe View.")];
    }

    /// <summary>Detaches the open world from the window (before its file is replaced); the caller closes the returned session.</summary>
    private WorldSession? CloseSession()
    {
        if (_map is not null) _map.PropertyChanged -= OnMapChanged;
        var old = _session;
        _detailsRequest++;
        _caravans = null;
        NodeEditor = null;
        if (old is not null)
        {
            old.DataChanged -= OnDataChanged;
            old.ActiveRunChanged -= OnActiveRunChanged;
        }

        _windows.CloseAll();
        _city?.Detach();
        City = null;
        _time?.Dispose();
        Time = null;
        Map = null;
        Session = null;
        Areas = AreasViewModel.Empty;
        Raise(nameof(HasWorld));
        Raise(nameof(IsTimeRunning));
        Raise(nameof(CanUseFileActions));
        Raise(nameof(ShowsMap));
        ShowNothing();
        RaiseMapState();
        return old;
    }

    private void ReportError(string context, Exception ex)
    {
        var message = $"{context}: {ex.Message}";
        if (IsOldSchema(ex)) message += " " + RegenerateHint;
        StatusText = message;
        ErrorText = message;
    }

    private void ClearError() => ErrorText = "";

    /// <summary>A missing table or column: the database predates the current schema. (A generator version mismatch carries its own message, see <see cref="GlobeGeneratorVersionException"/>.)</summary>
    private static bool IsOldSchema(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
            if (e is SqliteException s && (s.Message.Contains("no such table", StringComparison.Ordinal) || s.Message.Contains("no such column", StringComparison.Ordinal)))
                return true;
        return false;
    }

    /// <summary>
    /// True when two paths name the same file: full paths with every symbolic link resolved (in the file name and in the
    /// directories above it), compared case-insensitively on Windows and macOS (their default file systems ignore case)
    /// and ordinally elsewhere.
    /// </summary>
    internal static bool SameFile(string a, string b)
    {
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(ResolvePath(a), ResolvePath(b), comparison);
    }

    /// <summary>Full path with symbolic links resolved component by component (components that do not exist are kept).</summary>
    private static string ResolvePath(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full) ?? "";
        var current = root;
        foreach (var part in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            try
            {
                FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
                if (info.LinkTarget is not null && info.ResolveLinkTarget(returnFinalTarget: true) is { } target)
                    current = Path.GetFullPath(target.FullName);
            }
            catch (IOException)
            {
                // Unreadable link: compare the unresolved path.
            }
            catch (UnauthorizedAccessException)
            {
                // Same as above.
            }
        }

        return Path.TrimEndingDirectorySeparator(current);
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            SqliteConnection.ClearPool(new SqliteConnection($"Data Source={path}"));
            foreach (var f in new[] { path, path + "-journal", path + "-wal", path + "-shm" })
                if (File.Exists(f)) File.Delete(f);
        }
        catch (IOException)
        {
            // Best effort: the temporary file is named after the target and a GUID (Globally Unique Identifier), so a leftover never replaces anything.
        }
        catch (UnauthorizedAccessException)
        {
            // Same as above.
        }
    }

    private void RaiseMapState()
    {
        Raise(nameof(LevelName));
        Raise(nameof(CanGoBack));
        Raise(nameof(CanZoomToRegion));
        ZoomToRegionCommand.RaiseCanExecuteChanged();
        BackCommand.RaiseCanExecuteChanged();
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        if (_session is null)
        {
            if (!HasError) StatusText = "No world open. File → Open… or File → New world…";
            return;
        }

        var d = _session.Data;
        var version = d.GeneratorVersion is { } v ? $"generator v{v}" : "generator unknown";
        StatusText = string.Create(Inv,
            $"World '{d.World.Name}' · seed {d.World.Seed} · {_session.Layers.Grid.CellCount:N0} globe cells · {d.TileGrid.Count:N0} tiles of {_session.TileSizeKm:F1} km · {version} · {LevelName} View")
            + TileBlockStatus(_session.Renderer.Cache);
    }

    /// <summary>
    /// Re-reads the tile block cache's failure counts into <see cref="StatusText"/>; the map control calls it on the user
    /// interface thread after tile blocks finished loading.
    /// </summary>
    public void RefreshTileStatus()
    {
        if (_session is not null) UpdateStatus();
    }

    /// <summary>Status suffix for tile blocks that failed to load or are not stored (both drawn as region cells); empty when none.</summary>
    internal static string TileBlockStatus(TileBlockCache? cache)
    {
        if (cache is null) return "";
        int failed = cache.FailedCount, missing = cache.MissingCount;
        var text = "";
        if (failed > 0)
            text += string.Create(Inv, $" · {failed} tile block(s) could not be loaded, drawn as region cells, retrying: {cache.LastError?.Message}");
        if (missing > 0)
            text += string.Create(Inv, $" · {missing} globe cell(s) have no stored tile block, drawn as region cells; regenerate the world");
        return text;
    }
}
