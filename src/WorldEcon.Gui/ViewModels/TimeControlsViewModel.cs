using System.ComponentModel;
using System.Globalization;
using ErrorOr;
using WorldEcon.Application.Simulation;
using WorldEcon.SharedKernel;
using WorldEcon.SharedKernel.Calendar;
using WorldEcon.Simulation.Time;

namespace WorldEcon.Gui.ViewModels;

/// <summary>
/// The time bar of one open world: the date label, Play/Pause with a speed, jumps (+1h … +1y), To: a date and time,
/// and the progress of a running jump. Rules are in <see cref="TimeController"/>; this class wires it to the window:
/// the clock moves the caravans (<see cref="CaravanLayer.SetTick"/>), steps that ran phases re-read the economy views,
/// the open run is the session's <see cref="WorldSession.ActiveRun"/>, and a failed run is reported as an error.
/// Framework-free.
/// </summary>
public sealed class TimeControlsViewModel : ObservableObject, IDisposable
{
    /// <summary>Tooltip of the disabled bar for a world without a file.</summary>
    public const string NoFileText = "Time controls need a world opened from a file.";

    /// <summary>Tooltip of the disabled bar for a file not in WAL mode.</summary>
    public const string NotWalText = "Time controls need the file in WAL mode; close other programs using it and reopen.";

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly string[] JumpUnits = ["1h", "1d", "1w", "1M", "1y"];

    private readonly MainViewModel _main;
    private readonly WorldSession _session;
    private readonly CalendarSystem _calendar;
    private readonly CalendarDefinition _definition;
    private readonly TimeController _time;
    private int _speedIndex = (int)PlaySpeed.OneHour;
    private int _toYear;
    private int _toMonthIndex;
    private int _toDay = 1;
    private string _toTime = "00:00";
    private bool _disposed;

    /// <summary>The time bar of <paramref name="session"/> in <paramref name="main"/>; runs open on the session's file.</summary>
    public TimeControlsViewModel(MainViewModel main, WorldSession session)
        : this(main, session, OpenRunner(session), null)
    {
    }

    /// <summary>For tests: <paramref name="openRun"/> replaces the runner, <paramref name="nowMs"/> the real-time clock.</summary>
    internal TimeControlsViewModel(MainViewModel main, WorldSession session, Func<CancellationToken, Task<ErrorOr<ISimulationRun>>> openRun,
        Func<long>? nowMs)
    {
        _main = main ?? throw new ArgumentNullException(nameof(main));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _definition = session.Data.World.Calendar;
        _calendar = new CalendarSystem(_definition);
        _time = new TimeController(session.CurrentTick, openRun, nowMs) { Speed = (PlaySpeed)_speedIndex };
        _time.ClockChanged += OnClockChanged;
        _time.DataStepped += OnDataStepped;
        _time.RunChanged += OnRunChanged;
        _time.RunFailed += OnRunFailed;
        _main.PropertyChanged += OnMainChanged;
        MonthNames = _definition.Months.Select(m => m.Name).ToList();
        PrefillTo();
    }

    private static Func<CancellationToken, Task<ErrorOr<ISimulationRun>>> OpenRunner(WorldSession session) => async ct =>
    {
        if (session.Path is not { } path) return Error.Failure("Time.NoFile", NoFileText);
        var opened = await Task.Run(() => SimulationRunner.OpenAsync(path, ct), ct);
        return opened.IsError ? opened.Errors : opened.Value;
    };

    /// <summary>The rules behind the bar (tests and the window's timer).</summary>
    internal TimeController Controller => _time;

    /// <summary>
    /// The bar is enabled: a world opened from a file in WAL mode, and no world load or creation running.
    /// </summary>
    public bool IsAvailable => UnavailableReason is null && !_main.IsBusy;

    /// <summary>Why the bar is disabled (its tooltip), or null.</summary>
    public string? UnavailableReason => _session.Path is null ? NoFileText : !_session.Data.TimeControlsAvailable ? NotWalText : null;

    /// <summary>"Y{y} M{m} D{d} HH:mm (tick n)" of the clock.</summary>
    public string DateText => string.Create(Inv, $"{DetailFormatter.Date(_session, _time.DisplayTick)} (tick {_time.DisplayTick:N0})");

    /// <summary>The clock the bar shows.</summary>
    public long DisplayTick => _time.DisplayTick;

    /// <summary>Preset speed names, in <see cref="PlaySpeed"/> order.</summary>
    public IReadOnlyList<string> SpeedNames { get; } = ["1 min/s", "10 min/s", "1 h/s", "6 h/s", "1 day/s", "Max"];

    /// <summary>Index of the chosen speed in <see cref="SpeedNames"/> (default 1 h/s).</summary>
    public int SpeedIndex
    {
        get => _speedIndex;
        set
        {
            if (value < 0 || value >= SpeedNames.Count || !Set(ref _speedIndex, value)) return;
            _time.Speed = (PlaySpeed)value;
        }
    }

    public bool IsPlaying => _time.IsPlaying;

    /// <summary>"Pause" while playing, else "Play".</summary>
    public string PlayButtonText => IsPlaying ? "Pause" : "Play";

    /// <summary>A run is open (Play, a jump or To:).</summary>
    public bool IsRunActive => _time.IsRunActive;

    /// <summary>Jumps and To: are possible: available, not playing, no other advance running.</summary>
    public bool CanJump => IsAvailable && !_time.IsPlaying && !_time.IsRunActive;

    public bool IsJumping => _time.IsJumping;

    /// <summary>The jump and To: controls are shown (not during a jump, which shows progress and Stop instead).</summary>
    public bool ShowsJumpControls => !IsJumping;

    /// <summary>"Advancing… {date} · {n} %" during a jump.</summary>
    public string ProgressText => IsJumping
        ? string.Create(Inv, $"Advancing… {DetailFormatter.Date(_session, _time.DisplayTick)} · {(int)Math.Floor(_time.JumpFraction * 100)} %")
        : "";

    /// <summary>Play or Pause.</summary>
    public async Task TogglePlayAsync()
    {
        if (_time.IsPlaying)
        {
            await _time.PauseAsync();
            RaiseState();
            return;
        }

        if (!IsAvailable || _time.IsRunActive) return;
        var started = await _time.PlayAsync();
        if (started.IsError) Report("Could not start time", started.FirstError.Description);
        RaiseState();
    }

    /// <summary>Jumps by one of <c>1h 1d 1w 1M 1y</c> (completes when the jump is committed).</summary>
    public async Task JumpAsync(string unit)
    {
        if (!JumpUnits.Contains(unit, StringComparer.Ordinal)) throw new ArgumentOutOfRangeException(nameof(unit), unit, "Jumps are 1h, 1d, 1w, 1M or 1y.");
        if (!CanJump || !_calendar.TryParseDurationToTicks(unit, out var ticks)) return;
        await RunAdvanceAsync(_time.JumpAsync(ticks));
    }

    /// <summary>Stops the running jump at its last completed boundary (it commits there).</summary>
    public void Stop() => _time.StopJump();

    // ---- To: ----

    /// <summary>Month names of the world's calendar.</summary>
    public IReadOnlyList<string> MonthNames { get; }

    public int ToYear
    {
        get => _toYear;
        set
        {
            if (!Set(ref _toYear, value)) return;
            ClampDay();
            Raise(nameof(CanGo));
        }
    }

    /// <summary>0-based month of the To: date.</summary>
    public int ToMonthIndex
    {
        get => _toMonthIndex;
        set
        {
            if (value < 0 || value >= MonthNames.Count || !Set(ref _toMonthIndex, value)) return;
            ClampDay();
            Raise(nameof(CanGo));
        }
    }

    /// <summary>Day of the To: date, clamped to 1..the month's length.</summary>
    public int ToDay
    {
        get => _toDay;
        set
        {
            if (!Set(ref _toDay, Math.Clamp(value, 1, MonthLength))) return;
            Raise(nameof(CanGo));
        }
    }

    /// <summary>Days in the To: month (the day field's maximum).</summary>
    public int MonthLength => _definition.Months[_toMonthIndex].Days;

    /// <summary>Time of day of the To: date, "hh:mm".</summary>
    public string ToTime
    {
        get => _toTime;
        set
        {
            if (!Set(ref _toTime, value ?? "")) return;
            Raise(nameof(CanGo));
        }
    }

    /// <summary>The To: tick, or null when the fields do not make a valid date and time.</summary>
    public long? ToTick
    {
        get
        {
            if (!TryParseTime(_toTime, out var hour, out var minute)) return null;
            if (_toDay < 1 || _toDay > MonthLength) return null;
            return _calendar.ToTick(new CalendarDate(_toYear, _toMonthIndex + 1, _toDay, hour, minute)).Value;
        }
    }

    /// <summary>The To: date is valid and after the clock, and jumps are possible.</summary>
    public bool CanGo => CanJump && ToTick is { } t && t > _time.DisplayTick;

    /// <summary>Advances to the To: date (completes when committed).</summary>
    public async Task GoAsync()
    {
        if (!CanGo || ToTick is not { } target) return;
        await RunAdvanceAsync(_time.AdvanceToAsync(target));
    }

    /// <summary>Window close: stops a jump or Play and commits.</summary>
    public async Task EndAnyRunAsync()
    {
        await _time.EndAnyRunAsync();
        RaiseState();
    }

    /// <summary>The window's 10 Hz timer while playing.</summary>
    public void Tick(TimeSpan elapsed) => _time.Update(elapsed);

    /// <summary>Stops following the session and window (the world was closed).</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _time.ClockChanged -= OnClockChanged;
        _time.DataStepped -= OnDataStepped;
        _time.RunChanged -= OnRunChanged;
        _time.RunFailed -= OnRunFailed;
        _main.PropertyChanged -= OnMainChanged;
    }

    private async Task RunAdvanceAsync(Task<ErrorOr<Success>> advance)
    {
        RaiseState();
        var result = await advance;
        if (result.IsError) Report("Could not advance time", result.FirstError.Description);
        RaiseState();
    }

    private bool TryParseTime(string text, out int hour, out int minute)
    {
        hour = minute = 0;
        var parts = text.Trim().Split(':');
        return parts.Length == 2
               && int.TryParse(parts[0], NumberStyles.None, Inv, out hour) && hour >= 0 && hour < _definition.HoursPerDay
               && int.TryParse(parts[1], NumberStyles.None, Inv, out minute) && minute >= 0 && minute < _definition.MinutesPerHour;
    }

    private void ClampDay()
    {
        Raise(nameof(MonthLength));
        if (_toDay > MonthLength) ToDay = MonthLength;
    }

    private void PrefillTo()
    {
        var d = _calendar.ToDate(new Tick(_time.DisplayTick));
        _toYear = d.Year;
        _toMonthIndex = d.Month - 1;
        _toDay = d.Day;
        _toTime = string.Create(Inv, $"{d.Hour:D2}:{d.Minute:D2}");
        foreach (var name in new[] { nameof(ToYear), nameof(ToMonthIndex), nameof(ToDay), nameof(ToTime), nameof(MonthLength), nameof(CanGo) })
            Raise(name);
    }

    private void OnClockChanged(object? sender, EventArgs e)
    {
        if (_disposed) return;
        if (!_time.IsJumping) _main.Caravans?.SetTick(_time.DisplayTick); // the map holds during a jump (spec section 3)
        Raise(nameof(DisplayTick));
        Raise(nameof(DateText));
        Raise(nameof(ProgressText));
        Raise(nameof(CanGo));
    }

    // The session's DataChanged re-reads the caravans (MainViewModel) and every open city panel.
    private void OnDataStepped(object? sender, EventArgs e)
    {
        if (!_disposed && !_session.IsClosed) _session.RaiseDataChanged();
    }

    private void OnRunChanged(object? sender, ISimulationRun? run)
    {
        if (_disposed) return;
        _session.ActiveRun = run;
        if (run is null)
        {
            _session.CurrentTick = _time.FileTick;
            PrefillTo();
            if (!_session.IsClosed) _session.RaiseDataChanged(); // one re-read of the committed (or restored) state
        }

        RaiseState();
    }

    private void OnRunFailed(object? sender, string message)
    {
        if (!_disposed) Report("Time run failed", message + " Nothing was saved.");
    }

    private void OnMainChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsBusy)) RaiseState();
    }

    private void Report(string context, string message) => _main.ReportUnexpected(context, new InvalidOperationException(message));

    private void RaiseState()
    {
        foreach (var name in new[]
                 {
                     nameof(IsAvailable), nameof(IsPlaying), nameof(PlayButtonText), nameof(IsRunActive), nameof(CanJump), nameof(IsJumping),
                     nameof(ShowsJumpControls), nameof(ProgressText), nameof(CanGo), nameof(DateText), nameof(DisplayTick),
                 })
            Raise(name);
    }
}
