using WorldEcon.Rendering;
using WorldEcon.WorldGen.Pipeline;
using WorldEcon.WorldGen.Pipeline.Checkpoints;

namespace WorldEcon.Gui.ViewModels;

/// <summary>State of a stage in the tuning mode's stage strip.</summary>
public enum StageState
{
    Pending,
    Running,
    Done,
    Loaded,
}

/// <summary>One entry of the stage strip. <see cref="WorldGenStage.Summary"/> stands for the finished world ("World").</summary>
public sealed record StageRow(WorldGenStage Stage, StageState State, int FrameCount)
{
    public string Name => Stage == WorldGenStage.Summary ? "World" : Stage.ToString();

    /// <summary>● done, ◆ loaded from a checkpoint, ◐ running, ○ pending.</summary>
    public string Symbol => State switch
    {
        StageState.Done => "●",
        StageState.Loaded => "◆",
        StageState.Running => "◐",
        _ => "○",
    };
}

/// <summary>
/// Tuning mode (design: stage checkpoints and viewer). Runs generation without a database, shows each globe stage's
/// result as soon as it finishes (<see cref="CurrentSession"/>), then lets the user step through the stages and their
/// frames, rerun every stage after a kept one with reloaded settings, save and open checkpoints. Generation runs on a
/// worker thread; its results reach this object through <c>uiDispatch</c> (the user interface thread in the app).
/// </summary>
public sealed class StageViewerViewModel : ObservableObject, IDisposable
{
    private static readonly WorldGenStage[] GlobeStages =
        [WorldGenStage.Tectonics, WorldGenStage.SeaLevel, WorldGenStage.Currents, WorldGenStage.Winds, WorldGenStage.Climate];

    private readonly GenerationSettingsViewModel _settings;
    private readonly Func<Action, Task> _ui;
    private readonly Dictionary<WorldGenStage, List<StageSnapshot>> _snapshots = [];
    private readonly List<Task> _pending = [];
    private IReadOnlyList<StageRow> _stages = [.. GlobeStages.Select(s => new StageRow(s, StageState.Pending, 1)), new StageRow(WorldGenStage.Summary, StageState.Pending, 1)];
    private WorldGenStage? _selected;
    private int _frame;
    private GlobeLayer _layer = GlobeLayer.Biome;
    private GlobeLayer? _userLayer;
    private IMapSession? _session;
    private bool _running;
    private string? _error;
    private string? _progressText;
    private GeneratedWorld? _world;
    private string? _metrics;

    public StageViewerViewModel(GenerationSettingsViewModel settings, Func<Action, Task> uiDispatch)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _ui = uiDispatch ?? throw new ArgumentNullException(nameof(uiDispatch));
    }

    public GenerationSettingsViewModel Settings => _settings;

    public IReadOnlyList<StageRow> Stages
    {
        get => _stages;
        private set => Set(ref _stages, value);
    }

    /// <summary>The stage shown; <see cref="WorldGenStage.Summary"/> is the finished world. Selecting one shows its final frame.</summary>
    public WorldGenStage? SelectedStage
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value)) return;
            _frame = value is { } s && FramesOf(s) is { Count: > 0 } f ? f.Count - 1 : 0;
            Show();
        }
    }

    public int SelectedFrame
    {
        get => _frame;
        set
        {
            if (_selected is not { } s || FramesOf(s) is not { Count: > 0 } frames) return;
            var v = Math.Clamp(value, 0, frames.Count - 1);
            if (v == _frame) return;
            _frame = v;
            Show();
        }
    }

    /// <summary><see cref="SelectedFrame"/> as a double, for a slider.</summary>
    public double SelectedFrameValue
    {
        get => _frame;
        set => SelectedFrame = (int)Math.Round(value);
    }

    /// <summary>"World", a stage name, or "none".</summary>
    public string SelectedStageName => _selected switch
    {
        null => "none",
        WorldGenStage.Summary => "World",
        { } s => s.ToString(),
    };

    public IReadOnlyList<string> LayerNames { get; } = Enum.GetNames<GlobeLayer>();

    /// <summary><see cref="CurrentLayer"/> as an index into <see cref="LayerNames"/>, for a picker; other values are ignored.</summary>
    public int CurrentLayerIndex
    {
        get => (int)_layer;
        set
        {
            if (value >= 0 && value < LayerNames.Count) CurrentLayer = (GlobeLayer)value;
        }
    }

    public int SelectedFrameCount => _selected is { } s && FramesOf(s) is { Count: > 0 } f ? f.Count : 1;

    public int SelectedFrameMax => SelectedFrameCount - 1;

    public bool HasFrames => SelectedFrameCount > 1;

    public string? SelectedFrameLabel => _selected is { } s && FramesOf(s) is { Count: > 0 } f && _frame < f.Count ? f[_frame].FrameLabel : null;

    /// <summary>The map layer shown. A layer the user picks is kept while stepping, until the next run.</summary>
    public GlobeLayer CurrentLayer
    {
        get => _layer;
        set
        {
            _userLayer = value;
            if (Set(ref _layer, value)) Raise(nameof(CurrentLayerIndex));
        }
    }

    /// <summary>What the map draws: the selected stage's frame, or the finished world. Replaced (and the old one disposed) on every change.</summary>
    public IMapSession? CurrentSession => _session;

    public bool IsRunning
    {
        get => _running;
        private set
        {
            if (Set(ref _running, value)) RaiseCommands();
        }
    }

    public string? Error
    {
        get => _error;
        private set => Set(ref _error, value);
    }

    /// <summary>Progress of the tile stages (which have no globe view of their own).</summary>
    public string? ProgressText
    {
        get => _progressText;
        private set => Set(ref _progressText, value);
    }

    /// <summary>Measurements of the finished world (<see cref="WorldEcon.WorldGen.Queries.WorldMetrics"/>); null until a run completes.</summary>
    public string? MetricsText
    {
        get => _metrics;
        private set => Set(ref _metrics, value);
    }

    /// <summary>Seed and settings of the run the snapshots came from.</summary>
    public ulong RunSeed { get; private set; }

    public WorldGenSettings? RunSettings { get; private set; }

    /// <summary>The finished world of the last run (null until it completes).</summary>
    public GeneratedWorld? World => _world;

    public bool CanRun => !_running;

    public bool CanRerun => !_running && _selected is { } s && GlobeStages.Contains(s) && SnapshotOf(s) is not null;

    public bool CanSaveCheckpoint => CanRerun;

    public bool CanSaveAsWorld => !_running && _world is not null;

    /// <summary>The final snapshot of a globe stage of the current run, or null.</summary>
    public StageSnapshot? SnapshotOf(WorldGenStage stage) =>
        _snapshots.TryGetValue(stage, out var list) ? list.LastOrDefault(x => x.FrameIndex == x.FrameCount - 1) : null;

    /// <summary>Runs every stage with the dialog's seed and settings (the file is read again). False when running or the settings are invalid.</summary>
    public async Task<bool> RunAsync()
    {
        if (_running) return false;
        WorldGenSettings settings;
        try
        {
            settings = _settings.Build();
        }
        catch (InvalidOperationException ex)
        {
            Error = ex.Message;
            return false;
        }

        return await StartAsync(settings, _settings.Seed, resume: null);
    }

    /// <summary>Re-reads the settings file; on failure the error is shown and the previous settings and snapshots stay.</summary>
    public bool Reload()
    {
        try
        {
            _settings.Build();
            Error = null;
            return true;
        }
        catch (InvalidOperationException ex)
        {
            Error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Keeps the selected stage (and those before it) unchanged and reruns every stage after it with the current settings.
    /// Refused, with the differences in <see cref="Error"/>, when the settings changed a section that the selected or an
    /// earlier stage reads, or the seed changed; the snapshots are then unchanged.
    /// </summary>
    public async Task<bool> RerunFromSelectedAsync()
    {
        if (!CanRerun || _selected is not { } stage || RunSettings is null) return false;
        WorldGenSettings settings;
        try
        {
            settings = _settings.Build();
        }
        catch (InvalidOperationException ex)
        {
            Error = ex.Message;
            return false;
        }

        var checkpoint = CheckpointFor(stage);
        var diff = StageFingerprint.Differences(checkpoint.Prints, checkpoint.WorldSeed, checkpoint.GeneratorVersion,
            StageFingerprint.For(settings, stage), _settings.Seed, WorldGenerator.GeneratorVersion);
        if (diff.Count > 0)
        {
            Error = new CheckpointMismatchException(diff).Message;
            return false;
        }

        return await StartAsync(settings, _settings.Seed, checkpoint);
    }

    /// <summary>Writes the selected globe stage (and the stages before it) to a checkpoint file.</summary>
    public void SaveCheckpoint(string path)
    {
        if (!CanSaveCheckpoint || _selected is not { } stage) throw new InvalidOperationException("Select a finished globe stage to save a checkpoint.");
        CheckpointCodec.Write(path, CheckpointFor(stage));
    }

    /// <summary>Starts a run from a checkpoint file with its own seed and settings; a file that cannot be read sets <see cref="Error"/>.</summary>
    public async Task<bool> OpenCheckpointAsync(string path)
    {
        if (_running) return false;
        WorldGenCheckpoint checkpoint;
        try
        {
            checkpoint = await Task.Run(() => CheckpointCodec.Read(path));
        }
        catch (CheckpointFormatException ex)
        {
            Error = ex.Message;
            return false;
        }

        _settings.SeedText = checkpoint.WorldSeed.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return await StartAsync(checkpoint.Settings, checkpoint.WorldSeed, checkpoint);
    }

    public void Dispose()
    {
        _session?.Dispose();
        _session = null;
    }

    private WorldGenCheckpoint CheckpointFor(WorldGenStage stage) =>
        WorldGenCheckpoint.FromSnapshots(RunSeed, RunSettings!, GlobeStages.SelectMany(s => _snapshots.GetValueOrDefault(s) ?? []).ToList(), stage);

    private async Task<bool> StartAsync(WorldGenSettings settings, ulong seed, WorldGenCheckpoint? resume)
    {
        IsRunning = true;
        Error = null;
        ProgressText = null;
        _userLayer = null;
        _world = null;
        MetricsText = null;
        _snapshots.Clear();
        Stages = [.. _stages.Select(r => r with { State = StageState.Pending, FrameCount = 1 })];
        RunSeed = seed;
        RunSettings = settings;
        var observer = new Observer(this);
        var progress = new DispatchProgress(this, new RemainingTime());
        try
        {
            await Task.Run(() => WorldGenerator.Generate(seed, settings, new WorldGenOptions(Progress: progress, Observer: observer, Resume: resume)));
            Task[] pending;
            lock (_pending) pending = [.. _pending];
            await Task.WhenAll(pending);
            return Error is null;
        }
        catch (Exception ex) when (ex is CheckpointMismatchException or InvalidOperationException or ArgumentException)
        {
            Error = ex.Message;
            return false;
        }
        finally
        {
            lock (_pending) _pending.Clear();
            ProgressText = null;
            IsRunning = false;
            Raise(nameof(World));
        }
    }

    private void Post(Action action)
    {
        var t = _ui(action);
        lock (_pending) _pending.Add(t);
    }

    private void OnStage(StageSnapshot s)
    {
        if (!_snapshots.TryGetValue(s.Stage, out var list)) _snapshots[s.Stage] = list = [];
        list.Add(s);
        var state = s.Loaded ? StageState.Loaded : StageState.Done;
        var final = s.FrameIndex == s.FrameCount - 1;
        Stages = [.. _stages.Select(r => r.Stage == s.Stage ? r with { State = final ? state : StageState.Running, FrameCount = s.FrameCount } : r)];
        if (final) Follow(s.Stage);
    }

    private void OnWorld(GeneratedWorld world)
    {
        _world = world;
        MetricsText = WorldEcon.WorldGen.Queries.WorldMetrics.Compute(world.Layers, RunSettings?.Grid.PlanetRadiusKm ?? 6371).Format().TrimEnd();
        Stages = [.. _stages.Select(r => r.Stage == WorldGenStage.Summary ? r with { State = StageState.Done } : r)];
        Follow(WorldGenStage.Summary);
    }

    // During a run the view follows the newest finished stage.
    private void Follow(WorldGenStage stage)
    {
        _selected = stage;
        Raise(nameof(SelectedStage));
        _frame = FramesOf(stage) is { Count: > 0 } f ? f.Count - 1 : 0;
        Show();
    }

    private List<StageSnapshot>? FramesOf(WorldGenStage stage) => _snapshots.GetValueOrDefault(stage);

    private void Show()
    {
        var next = _userLayer ?? (_selected is { } st ? StageLayers.DefaultFor(st) : GlobeLayer.Biome);
        if (Set(ref _layer, next, nameof(CurrentLayer))) Raise(nameof(CurrentLayerIndex));
        IMapSession? session = null;
        if (_selected == WorldGenStage.Summary && _world is not null && RunSettings is not null)
            session = PreviewSession.FromWorld(_world, RunSettings);
        else if (_selected is { } s && FramesOf(s) is { Count: > 0 } frames)
            session = PreviewSession.FromSnapshot(frames[Math.Clamp(_frame, 0, frames.Count - 1)]);
        var old = _session;
        _session = session;
        Raise(nameof(CurrentSession));
        old?.Dispose();
        Raise(nameof(SelectedFrame));
        Raise(nameof(SelectedFrameValue));
        Raise(nameof(SelectedStageName));
        Raise(nameof(SelectedFrameCount));
        Raise(nameof(SelectedFrameMax));
        Raise(nameof(HasFrames));
        Raise(nameof(SelectedFrameLabel));
        RaiseCommands();
    }

    private void RaiseCommands()
    {
        Raise(nameof(CanRun));
        Raise(nameof(CanRerun));
        Raise(nameof(CanSaveCheckpoint));
        Raise(nameof(CanSaveAsWorld));
    }

    private sealed class Observer(StageViewerViewModel vm) : IWorldGenObserver
    {
        public void StageCompleted(StageSnapshot snapshot) => vm.Post(() => vm.OnStage(snapshot));

        public void WorldCompleted(GeneratedWorld world) => vm.Post(() => vm.OnWorld(world));
    }

    // Every stage reports (the Tectonics stage after each step); the text carries the time-remaining estimate.
    private sealed class DispatchProgress(StageViewerViewModel vm, RemainingTime remaining) : IProgress<(WorldGenStage Stage, double Fraction)>
    {
        public void Report((WorldGenStage Stage, double Fraction) value)
        {
            var text = $"{value.Stage}… ({value.Fraction * 100:F0} % of the run, {remaining.Text(value.Fraction)})";
            vm.Post(() => vm.ProgressText = text);
        }
    }
}
