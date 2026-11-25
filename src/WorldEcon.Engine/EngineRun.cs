using Microsoft.EntityFrameworkCore.Storage;
using WorldEcon.Engine.Logging;
using WorldEcon.SharedKernel;

namespace WorldEcon.Engine;

/// <summary>
/// One run of the simulation clock inside one SQLite transaction (started by <see cref="TickEngine.BeginRunAsync(SimulationContext, CancellationToken)"/>).
/// <see cref="StepToAsync"/> moves the clock forward in any number of steps and saves at the end of each step (inside
/// the transaction); <see cref="EndAsync"/> writes the end-of-run money snapshot, prunes the log and commits. Disposing
/// a run that was not ended rolls the whole run back, so a failure or a killed process leaves the file at its tick from
/// before the run. When the caller already has a transaction open on the context, the run joins it and leaves the
/// commit or rollback to the caller.
/// </summary>
public sealed class EngineRun : IAsyncDisposable
{
    private readonly IReadOnlyList<ISimulationPhase> _phases;
    private readonly IDbContextTransaction? _ownTransaction;
    private readonly bool _prevAutoDetect;
    private int _lastMonthKey;
    private bool _ended;
    private bool _disposed;

    internal EngineRun(SimulationContext context, IReadOnlyList<ISimulationPhase> phases, IDbContextTransaction? ownTransaction)
    {
        Context = context;
        _phases = phases;
        _ownTransaction = ownTransaction;

        // Change-tracking discipline: EF's automatic DetectChanges runs on *every* query and is O(tracked entities);
        // across thousands of days the tracked graph (work orders, caravans, log events, …) grows and turns the advance
        // quadratic. Auto-detect is off for the run (phases read in-memory mutated state directly and use
        // Local-membership, neither of which needs it); DetectChanges runs right before each save.
        var tracker = context.Db.ChangeTracker;
        _prevAutoDetect = tracker.AutoDetectChangesEnabled;
        tracker.AutoDetectChangesEnabled = false;

        // Money-supply snapshots roll up on calendar month rollover (calendar-correct for variable month lengths) plus
        // one at the end of the run. Seeded from the starting month so the first snapshot is taken when the month
        // actually changes, not immediately.
        _lastMonthKey = MonthKey(context.World.CurrentTick);
    }

    /// <summary>The simulation state the run advances (its world's clock is the run's current tick).</summary>
    public SimulationContext Context { get; }

    /// <summary>The smallest tick after the current one at which any phase is due (<see cref="long.MaxValue"/> with no phases).</summary>
    public long NextBoundaryTick => NextDue(Context.World.CurrentTick.Value, long.MaxValue);

    /// <summary>True when the last <see cref="StepToAsync"/> call ran at least one phase.</summary>
    public bool LastStepRanPhases { get; private set; }

    /// <summary>
    /// Moves the clock to <paramref name="target"/>, running every phase due on the way, and saves after each boundary
    /// and at the end (all inside the run's transaction). It skips straight to the next phase-due boundary rather than stepping every minute: phases only
    /// run on cadence boundaries, so this matches a per-minute walk with O(boundaries) work. <paramref name="progress"/>
    /// receives each boundary reached. <paramref name="ct"/> is checked after each boundary's phases: a cancelled step
    /// stops there and returns the tick reached (it does not throw). Returns the tick reached.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="target"/> is before the current tick.</exception>
    /// <exception cref="InvalidOperationException">The run has ended or been disposed.</exception>
    public async Task<Tick> StepToAsync(Tick target, IProgress<Tick>? progress = null, CancellationToken ct = default)
    {
        ThrowIfFinished();
        var ctx = Context;
        if (target.Value < ctx.World.CurrentTick.Value)
            throw new ArgumentOutOfRangeException(nameof(target), target.Value,
                $"Cannot step back from tick {ctx.World.CurrentTick.Value}.");

        LastStepRanPhases = false;
        while (ctx.World.CurrentTick.Value < target.Value && !ct.IsCancellationRequested)
        {
            long nextDue = NextDue(ctx.World.CurrentTick.Value, target.Value);
            var boundary = new Tick(nextDue);
            ctx.World.AdvanceTo(boundary);

            var ranPhases = false;
            foreach (var phase in _phases)
            {
                if (nextDue % phase.CadenceTicks == 0)
                {
                    await phase.ExecuteAsync(ctx, boundary);
                    ranPhases = true;
                }
            }

            LastStepRanPhases |= ranPhases;

            int monthKey = MonthKey(boundary);
            if (monthKey != _lastMonthKey)
            {
                await ctx.Money.SnapshotAsync(boundary);
                _lastMonthKey = monthKey;
            }

            // Saved and cleared after every boundary whose phases ran, so the next boundary's queries see this one's new
            // rows (an unsaved Added entity is invisible to queries). The result then does not depend on how a run is
            // cut into steps (Play's one-boundary steps match one long advance), and the tracked graph stays small.
            if (ranPhases)
                await SaveAndClearAsync();

            progress?.Report(boundary);
        }

        // Every step ends saved with an empty tracker: the next step's queries see this step's rows, and a run of many
        // small steps (Play) does not grow the tracked graph. Entities reference each other by raw id (no EF
        // navigations), so clearing is safe; the World is re-attached so its clock keeps persisting.
        await SaveAndClearAsync();
        return ctx.World.CurrentTick;
    }

    /// <summary>
    /// Ends the run: money snapshot at the current tick, log pruning, save, then commit (when the run owns the
    /// transaction). Restores the context's automatic change detection.
    /// </summary>
    /// <exception cref="InvalidOperationException">The run has already ended or been disposed.</exception>
    public async Task EndAsync()
    {
        ThrowIfFinished();
        var ctx = Context;
        await ctx.Money.SnapshotAsync(ctx.World.CurrentTick); // final data point for this run
        await LogRetention.PruneAsync(ctx.Db, ctx.World.Id, ctx.World.CurrentTick);
        ctx.Db.ChangeTracker.DetectChanges();
        await ctx.Db.SaveChangesAsync();
        if (_ownTransaction is not null)
            await _ownTransaction.CommitAsync();
        _ended = true;
        ctx.Db.ChangeTracker.AutoDetectChangesEnabled = _prevAutoDetect;
    }

    /// <summary>
    /// Releases the run. A run that was not ended is rolled back (when it owns the transaction) and the context's change
    /// tracker is cleared, so nothing from the run is saved or left tracked; the context's <see cref="SimulationContext.World"/>
    /// object still holds the abandoned clock and is to be reloaded or replaced by the caller.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        var tracker = Context.Db.ChangeTracker;
        try
        {
            if (!_ended)
            {
                if (_ownTransaction is not null)
                    await _ownTransaction.RollbackAsync();
                tracker.Clear();
            }
        }
        finally
        {
            if (_ownTransaction is not null)
                await _ownTransaction.DisposeAsync();
            tracker.AutoDetectChangesEnabled = _prevAutoDetect;
        }
    }

    private async Task SaveAndClearAsync()
    {
        var ctx = Context;
        var tracker = ctx.Db.ChangeTracker;
        tracker.DetectChanges();
        await ctx.Db.SaveChangesAsync();
        tracker.Clear();
        ctx.Db.Attach(ctx.World);
    }

    // Smallest tick strictly after current at which any phase is due, capped at cap.
    private long NextDue(long current, long cap)
    {
        long nextDue = cap;
        foreach (var phase in _phases)
        {
            long cadence = phase.CadenceTicks;
            long due = (current / cadence + 1) * cadence; // smallest multiple of cadence strictly > current
            if (due < nextDue)
                nextDue = due;
        }

        return nextDue;
    }

    private void ThrowIfFinished()
    {
        if (_ended || _disposed)
            throw new InvalidOperationException("The run has already ended.");
    }

    /// <summary>A comparable key for the in-world month (year*100+month) — used to detect month rollover for
    /// money-ledger snapshots, correct even when months have different lengths.</summary>
    private int MonthKey(Tick tick)
    {
        var d = Context.Calendar.ToDate(tick);
        return d.Year * 100 + d.Month;
    }
}
