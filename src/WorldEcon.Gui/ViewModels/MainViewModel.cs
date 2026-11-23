using System.Globalization;
using Microsoft.Data.Sqlite;
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

    public MainViewModel()
    {
        ZoomToRegionCommand = new RelayCommand(() => ZoomToRegion(), () => CanZoomToRegion);
        BackCommand = new RelayCommand(() => BackToGlobe(), () => CanGoBack);
    }

    public RelayCommand ZoomToRegionCommand { get; }

    public RelayCommand BackCommand { get; }

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

    /// <summary>"Globe", "Region" or "Detail"; empty without a world.</summary>
    public string LevelName => _map?.LevelName ?? "";

    /// <summary>Back to globe is possible: the map shows Region or Detail View.</summary>
    public bool CanGoBack => _map is { IsGlobeLevel: false };

    /// <summary>
    /// Zoom to region is possible: a globe cell or region cell is selected and the map is not at Detail View (there a
    /// region-cell pick is the fallback for a tile block that is not loaded, and zooming to Region View would zoom out).
    /// </summary>
    public bool CanZoomToRegion => _map is { Level: not MapLevel.Detail, Selected: { Level: MapLevel.Globe or MapLevel.Region } };

    /// <summary>File → Open and File → New world are enabled (not while a load or seeding runs).</summary>
    public bool CanUseFileActions => !_isBusy;

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
        var old = _session;
        if (_map is not null) _map.PropertyChanged -= OnMapChanged;
        Session = session;
        Map = new MapViewModel(session);
        _map!.PropertyChanged += OnMapChanged;
        Areas = new AreasViewModel(session.Layers.Areas, OnAreaSelected);
        AreaDetailsTitle = "No area selected";
        AreaDetailRows = [];
        Raise(nameof(HasWorld));
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
    public async Task<bool> CreateDemoWorldAsync(string path, WorldGenSettings? settings = null, bool overwrite = false)
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
        var progress = new Progress<SeedingProgress>(p =>
        {
            BusyText = string.Create(Inv, $"Generating the world: {p.Step} ({p.Fraction * 100:F0} %)…");
            BusyPercent = p.Fraction * 100;
        });
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var temp = Path.Combine(dir, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        string? closedPath = null;
        IReadOnlyList<string> warnings = [];
        WarningsText = "";
        try
        {
            var (_, report) = await Task.Run(() => DemoSeeder.CreateAsync(temp, settings, progress));
            warnings = report.Warnings;
            // The open world reads tiles from its file: close it (renderer disposed, tile source closed, running loads
            // drained) before the pools are cleared and the file is replaced, so it never reads the new world's blocks.
            if (_session is { Path: { } open } && SameFile(open, path))
            {
                var closing = CloseSession();
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

    /// <summary>Selects what is under a map pixel at the current level; false when the pixel shows no map.</summary>
    public bool Click(double x, double y)
    {
        if (_map?.PickAt(x, y) is not { } pick) return false;
        Select(pick);
        return true;
    }

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
                    : DetailFormatter.Tile(tile, block, i, session);
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
        DetailRows = DetailFormatter.Cell(_session.CellDetails(cell), _session.Layers);
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
        Map = null;
        Session = null;
        Areas = AreasViewModel.Empty;
        Raise(nameof(HasWorld));
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
