using System.Diagnostics;
using ErrorOr;
using WorldEcon.Application.Simulation;

namespace WorldEcon.Gui.ViewModels;

/// <summary>Play speeds of the time bar, in in-world time per real second.</summary>
public enum PlaySpeed
{
    /// <summary>1 min/s.</summary>
    OneMinute,

    /// <summary>10 min/s.</summary>
    TenMinutes,

    /// <summary>1 h/s.</summary>
    OneHour,

    /// <summary>6 h/s.</summary>
    SixHours,

    /// <summary>1 day/s.</summary>
    OneDay,

    /// <summary>As fast as the simulation runs (boundary steps back to back).</summary>
    Max,
}

/// <summary>
/// The rules of the time bar, framework-free. A run (<see cref="ISimulationRun"/>) is opened by Play, a jump or To: and
/// committed when Play is paused or the jump ends; it is rolled back when a step fails.
/// <list type="bullet">
/// <item>Play: <see cref="Update"/> (10 Hz from the view) moves <see cref="DisplayTick"/> by the speed, up to the run's
/// next phase boundary; reaching it starts a step to the boundary on a worker thread and holds the clock until the step
/// completes (the remaining fraction is dropped). At <see cref="PlaySpeed.Max"/> boundary steps run back to back.</item>
/// <item>Pause steps the run to <see cref="DisplayTick"/> (when it is past the run's tick) and commits.</item>
/// <item>Jumps and To: step straight to the target with progress; <see cref="StopJump"/> stops at the last completed
/// boundary and commits there. They are refused while playing.</item>
/// </list>
/// <see cref="DataStepped"/> asks the window to re-read caravans and panels after phases ran; at Max it is raised at most
/// once per 250 ms of real time, plus once when Max stops. A jump raises none (the window re-reads after the commit).
/// </summary>
public sealed class TimeController
{
    /// <summary>Shortest real time between two <see cref="DataStepped"/> events at Max and during a jump.</summary>
    public const long DataThrottleMs = 250;

    private readonly Func<CancellationToken, Task<ErrorOr<ISimulationRun>>> _openRun;
    private readonly Func<long> _nowMs;
    private ISimulationRun? _run;
    private long _runStartTick;
    private double _fraction;
    private Task _step = Task.CompletedTask;
    private Task _maxLoop = Task.CompletedTask;
    private CancellationTokenSource? _jumpCts;
    private long _lastDataMs = long.MinValue / 2;
    private bool _dataPending;
    private bool _opening;
    private PlaySpeed _speed = PlaySpeed.OneHour;

    /// <param name="fileTick">The world file's committed tick (the clock shown before any run).</param>
    /// <param name="openRun">Opens a run on the world file (<see cref="SimulationRunner.OpenAsync(string, CancellationToken)"/> in the app).</param>
    /// <param name="nowMs">Real-time clock in milliseconds (tests pass a fake); defaults to a stopwatch.</param>
    public TimeController(long fileTick, Func<CancellationToken, Task<ErrorOr<ISimulationRun>>> openRun, Func<long>? nowMs = null)
    {
        _openRun = openRun ?? throw new ArgumentNullException(nameof(openRun));
        DisplayTick = fileTick;
        FileTick = fileTick;
        if (nowMs is null)
        {
            var sw = Stopwatch.StartNew();
            nowMs = () => sw.ElapsedMilliseconds;
        }

        _nowMs = nowMs;
    }

    /// <summary>In-world minutes per real second at <paramref name="speed"/>; 0 for <see cref="PlaySpeed.Max"/> (unpaced).</summary>
    public static long TicksPerSecond(PlaySpeed speed) => speed switch
    {
        PlaySpeed.OneMinute => 1,
        PlaySpeed.TenMinutes => 10,
        PlaySpeed.OneHour => 60,
        PlaySpeed.SixHours => 360,
        PlaySpeed.OneDay => 1440,
        _ => 0,
    };

    /// <summary>The clock the time bar shows.</summary>
    public long DisplayTick { get; private set; }

    /// <summary>The world file's committed tick (updated when a run commits).</summary>
    public long FileTick { get; private set; }

    /// <summary>Play speed; a change applies at the next <see cref="Update"/> (switching to Max while playing starts the back-to-back loop).</summary>
    public PlaySpeed Speed
    {
        get => _speed;
        set
        {
            if (_speed == value) return;
            _speed = value;
            _fraction = 0;
            if (IsPlaying && value == PlaySpeed.Max && _maxLoop.IsCompleted && _run is { } run) _maxLoop = MaxLoopAsync(run);
        }
    }

    /// <summary>Play is on.</summary>
    public bool IsPlaying { get; private set; }

    /// <summary>A run is open (or being opened): Play, a jump or To:.</summary>
    public bool IsRunActive => _run is not null || _opening;

    /// <summary>A jump or To: is running.</summary>
    public bool IsJumping { get; private set; }

    /// <summary>Fraction of the running jump done, 0..1.</summary>
    public double JumpFraction { get; private set; }

    /// <summary>Target tick of the running jump.</summary>
    public long JumpTarget { get; private set; }

    /// <summary><see cref="DisplayTick"/> moved.</summary>
    public event EventHandler? ClockChanged;

    /// <summary>Phases ran: the window re-reads caravans and panels.</summary>
    public event EventHandler? DataStepped;

    /// <summary>A run was opened (the run) or closed (null).</summary>
    public event EventHandler<ISimulationRun?>? RunChanged;

    /// <summary>A step or the commit failed (the message); the run was rolled back and <see cref="DisplayTick"/> reset to the file's tick.</summary>
    public event EventHandler<string>? RunFailed;

    /// <summary>Starts Play (opening a run). Refused while a jump runs.</summary>
    public async Task<ErrorOr<Success>> PlayAsync()
    {
        if (IsPlaying) return Result.Success;
        if (IsJumping || _opening) return Error.Conflict("Time.Busy", "Wait for the running advance to finish.");
        var opened = await OpenAsync();
        if (opened.IsError) return opened.Errors;
        var run = opened.Value;
        IsPlaying = true;
        _fraction = 0;
        if (_speed == PlaySpeed.Max) _maxLoop = MaxLoopAsync(run);
        return Result.Success;
    }

    /// <summary>
    /// Moves the clock by <see cref="Speed"/> × <paramref name="elapsed"/> while playing (not at Max). Reaching the run's
    /// next boundary starts a step to it; while that step runs the clock holds.
    /// </summary>
    public void Update(TimeSpan elapsed)
    {
        if (!IsPlaying || _run is not { } run || _speed == PlaySpeed.Max || !_step.IsCompleted || !_maxLoop.IsCompleted) return;
        _fraction += TicksPerSecond(_speed) * elapsed.TotalSeconds;
        var whole = (long)Math.Floor(_fraction + 1e-9); // 10 × 0.1 s at 1 min/s is one tick, not 0.999…
        if (whole < 1) return;
        _fraction -= whole;
        var target = DisplayTick + whole;
        var boundary = run.NextBoundaryTick;
        if (target >= boundary)
        {
            _fraction = 0;
            SetClock(boundary);
            _step = PlayStepAsync(run, boundary);
            return;
        }

        SetClock(target);
    }

    /// <summary>Stops Play: waits for a running step, steps the run to <see cref="DisplayTick"/> when needed, then commits.</summary>
    public Task PauseAsync()
    {
        if (!IsPlaying) return _pausing;
        IsPlaying = false;
        _pausing = PauseCoreAsync();
        return _pausing;
    }

    private Task _pausing = Task.CompletedTask;

    private async Task PauseCoreAsync()
    {
        await _maxLoop;
        await _step;
        FlushData();
        if (_run is not { } run) return; // a failed step already closed it
        if (DisplayTick > run.CurrentTick)
        {
            if (!await TryStepAsync(run, DisplayTick, null, CancellationToken.None)) return;
        }

        await CommitAsync(run);
    }

    /// <summary>Advances by <paramref name="ticks"/> (a jump). See <see cref="AdvanceToAsync"/>.</summary>
    public Task<ErrorOr<Success>> JumpAsync(long ticks) => AdvanceToAsync(DisplayTick + ticks);

    /// <summary>
    /// Advances to <paramref name="tick"/> in one run with progress, then commits. Refused while playing or while another
    /// advance runs (<see cref="ErrorType.Conflict"/>), and for a target not after the clock (<see cref="ErrorType.Validation"/>).
    /// Completes when the run is committed (or failed).
    /// </summary>
    public Task<ErrorOr<Success>> AdvanceToAsync(long tick)
    {
        var jump = AdvanceCoreAsync(tick);
        if (!jump.IsCompleted) _jump = jump;
        return jump;
    }

    private Task _jump = Task.CompletedTask;

    private async Task<ErrorOr<Success>> AdvanceCoreAsync(long tick)
    {
        if (IsPlaying) return Error.Conflict("Time.Playing", "Pause before jumping.");
        if (IsJumping || _opening || _run is not null) return Error.Conflict("Time.Busy", "Wait for the running advance to finish.");
        if (tick <= DisplayTick) return Error.Validation("Time.TargetNotAfterNow", "The target time must be after the current time.");
        var opened = await OpenAsync();
        if (opened.IsError) return opened.Errors;
        var run = opened.Value;
        var start = run.CurrentTick;
        if (tick <= start)
        {
            await CommitAsync(run);
            return Error.Validation("Time.TargetNotAfterNow", "The target time must be after the current time.");
        }

        using var cts = new CancellationTokenSource();
        _jumpCts = cts;
        IsJumping = true;
        JumpTarget = tick;
        JumpFraction = 0;
        var progress = new Progress<long>(p =>
        {
            if (!IsJumping) return;
            // Only the clock and the fraction move: the map and panels re-read once, after the commit (spec section 3).
            JumpFraction = Math.Clamp((double)(p - start) / (tick - start), 0, 1);
            SetClock(p);
        });
        try
        {
            if (!await TryStepAsync(run, tick, progress, cts.Token)) return Result.Success;
            SetClock(run.CurrentTick);
            await CommitAsync(run);
            return Result.Success;
        }
        finally
        {
            IsJumping = false;
            JumpFraction = 0;
            _jumpCts = null;
            _dataPending = false;
        }
    }

    /// <summary>Stops the running jump at its last completed boundary; the jump then commits there.</summary>
    public void StopJump() => _jumpCts?.Cancel();

    /// <summary>Window close: stops a jump or Play and commits (waits for it).</summary>
    public async Task EndAnyRunAsync()
    {
        if (IsJumping)
        {
            StopJump();
            await _jump;
            return;
        }

        if (IsPlaying)
        {
            await PauseAsync();
            return;
        }

        await _pausing; // a Pause that is stepping or committing
        await _step;
        if (_run is { } run) await CommitAsync(run);
    }

    private async Task<ErrorOr<ISimulationRun>> OpenAsync()
    {
        _opening = true;
        try
        {
            var opened = await _openRun(CancellationToken.None);
            if (opened.IsError) return opened;
            var run = opened.Value;
            _run = run;
            _runStartTick = run.CurrentTick;
            SetClock(run.CurrentTick);
            RunChanged?.Invoke(this, run);
            return opened;
        }
        finally
        {
            _opening = false;
        }
    }

    private async Task PlayStepAsync(ISimulationRun run, long boundary)
    {
        var before = run.CurrentTick;
        var stepped = await TryStepAsync(run, boundary, null, CancellationToken.None);
        if (stepped && run.CurrentTick > before && _lastStepRanPhases) DataStepped?.Invoke(this, EventArgs.Empty);
    }

    private async Task MaxLoopAsync(ISimulationRun run)
    {
        await _step; // a paced step that was running when Max was chosen
        while (IsPlaying && _speed == PlaySpeed.Max && ReferenceEquals(_run, run))
        {
            if (!await TryStepAsync(run, run.NextBoundaryTick, null, CancellationToken.None)) return;
            SetClock(run.CurrentTick);
            if (_lastStepRanPhases) NoteData(throttled: true);
        }

        FlushData();
    }

    private bool _lastStepRanPhases;

    /// <summary>Steps the run on a worker thread. False when it failed (the run is then rolled back and closed).</summary>
    private async Task<bool> TryStepAsync(ISimulationRun run, long target, IProgress<long>? progress, CancellationToken ct)
    {
        try
        {
            var result = await Task.Run(() => run.StepToAsync(target, progress, ct), CancellationToken.None);
            if (result.IsError)
            {
                await FailAsync(run, result.FirstError.Description);
                return false;
            }

            _lastStepRanPhases = result.Value.PhasesRan;
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await FailAsync(run, ex.Message);
            return false;
        }
    }

    private Task _commit = Task.CompletedTask;
    private ISimulationRun? _committing;

    /// <summary>Commits <paramref name="run"/> once: a second call while it commits waits for the first.</summary>
    private Task CommitAsync(ISimulationRun run)
    {
        if (ReferenceEquals(_committing, run)) return _commit;
        if (!ReferenceEquals(_run, run)) return Task.CompletedTask;
        _committing = run;
        _commit = CommitCoreAsync(run);
        return _commit;
    }

    private async Task CommitCoreAsync(ISimulationRun run)
    {
        try
        {
            await Task.Run(run.EndAsync);
        }
        catch (Exception ex)
        {
            await FailAsync(run, ex.Message);
            return;
        }

        _run = null;
        _committing = null;
        FileTick = run.CurrentTick;
        try
        {
            await run.DisposeAsync(); // committed already: a failure here loses nothing
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Committed already; RunFailed would say nothing was saved, so this is only traced.
            Trace.WriteLine($"Closing a committed time run failed: {ex.Message}");
        }
        finally
        {
            SetClock(FileTick);
            RunChanged?.Invoke(this, null);
        }
    }

    private async Task FailAsync(ISimulationRun run, string message)
    {
        if (!ReferenceEquals(_run, run)) return;
        _run = null;
        _committing = null;
        IsPlaying = false;
        _dataPending = false;
        try
        {
            await run.DisposeAsync(); // rolls back
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            message += $" (closing the run also failed: {ex.Message})";
        }

        SetClock(_runStartTick);
        RunFailed?.Invoke(this, message);
        RunChanged?.Invoke(this, null);
    }

    private void SetClock(long tick)
    {
        if (DisplayTick == tick) return;
        DisplayTick = tick;
        ClockChanged?.Invoke(this, EventArgs.Empty);
    }

    private void NoteData(bool throttled)
    {
        var now = _nowMs();
        if (!throttled || now - _lastDataMs >= DataThrottleMs)
        {
            _lastDataMs = now;
            _dataPending = false;
            DataStepped?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            _dataPending = true;
        }
    }

    private void FlushData()
    {
        if (!_dataPending) return;
        _dataPending = false;
        _lastDataMs = _nowMs();
        DataStepped?.Invoke(this, EventArgs.Empty);
    }
}
