using WorldEcon.Engine.Logging;
using WorldEcon.SharedKernel;

namespace WorldEcon.Engine;

/// <summary>
/// The core loop that advances in-world time and runs each cadence-gated phase whose cadence divides
/// the new tick. It skips straight to the next phase-due boundary rather than stepping every minute,
/// and persists + resets the change tracker at each boundary so long advances stay fast. Determinism
/// comes from the fixed phase order plus seeded RNG and stable entity ordering inside phases.
/// </summary>
public sealed class TickEngine : ITickEngine
{
    private readonly IReadOnlyList<ISimulationPhase> _phases;

    public TickEngine(IEnumerable<ISimulationPhase> phases)
    {
        ArgumentNullException.ThrowIfNull(phases);

        var ordered = phases.OrderBy(p => p.Order).ThenBy(p => p.Name, StringComparer.Ordinal).ToList();
        foreach (var phase in ordered)
        {
            if (phase.CadenceTicks <= 0)
                throw new ArgumentOutOfRangeException(
                    nameof(phases),
                    phase.CadenceTicks,
                    $"Phase '{phase.Name}' must have CadenceTicks > 0.");
        }

        _phases = ordered;
    }

    public async Task AdvanceAsync(SimulationContext ctx, long ticks)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        if (ticks < 0)
            throw new ArgumentOutOfRangeException(nameof(ticks), ticks, "Cannot advance a negative number of ticks.");

        long finalTick = ctx.World.CurrentTick.Value + ticks;

        // Two things make a long advance fast:
        //  1. Skip-ahead loop — jump straight to the next tick any phase is due (capped at the final
        //     tick) instead of stepping every minute. Phases only ever run on cadence boundaries, so
        //     this is identical to a per-minute walk but does O(boundaries) work, not O(ticks).
        //  2. Change-tracking discipline — EF's automatic DetectChanges runs on *every* query and is
        //     O(tracked entities); across thousands of days the tracked graph (work orders, caravans,
        //     log events, …) grows and turns the advance quadratic. We turn auto-detect off (phases
        //     read in-memory mutated state directly and use Local-membership, neither of which needs
        //     it), DetectChanges manually right before each save, and periodically save+clear to batch
        //     fsyncs and bound memory. Entities reference each other by raw id (no EF navigations), so
        //     clearing is safe; we only re-attach the World so its clock keeps persisting.
        var tracker = ctx.Db.ChangeTracker;
        bool prevAutoDetect = tracker.AutoDetectChangesEnabled;
        tracker.AutoDetectChangesEnabled = false;
        try
        {
            const long saveIntervalTicks = 90 * Tick.DefaultMinutesPerDay; // ~quarter-year batches
            long lastSaveTick = ctx.World.CurrentTick.Value;

            while (ctx.World.CurrentTick.Value < finalTick)
            {
                long current = ctx.World.CurrentTick.Value;

                long nextDue = finalTick;
                foreach (var phase in _phases)
                {
                    long cadence = phase.CadenceTicks;
                    long due = (current / cadence + 1) * cadence; // smallest multiple of cadence strictly > current
                    if (due < nextDue)
                        nextDue = due;
                }

                var boundary = new Tick(nextDue);
                ctx.World.AdvanceTo(boundary);

                foreach (var phase in _phases)
                {
                    if (nextDue % phase.CadenceTicks == 0)
                        await phase.ExecuteAsync(ctx, boundary);
                }

                if (nextDue - lastSaveTick >= saveIntervalTicks)
                {
                    tracker.DetectChanges();
                    await ctx.Db.SaveChangesAsync();
                    tracker.Clear();
                    ctx.Db.Attach(ctx.World);
                    lastSaveTick = nextDue;
                }
            }

            await LogRetention.PruneAsync(ctx.Db, ctx.World.Id, ctx.World.CurrentTick);
            tracker.DetectChanges();
            await ctx.Db.SaveChangesAsync();
        }
        finally
        {
            tracker.AutoDetectChangesEnabled = prevAutoDetect;
        }
    }
}
