using WorldEcon.SharedKernel;

namespace WorldEcon.Engine;

/// <summary>
/// The core loop that advances in-world time one minute-tick at a time and runs each
/// cadence-gated phase whose cadence divides the new tick. Determinism comes from the
/// fixed phase order plus seeded RNG and stable entity ordering inside phases.
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

        // Tick-by-tick advance. Future optimization: jump straight to the next phase-due
        // boundary instead of stepping every minute when no phase is due.
        for (long step = 0; step < ticks; step++)
        {
            Tick next = ctx.World.CurrentTick.AddMinutes(1);
            ctx.World.AdvanceTo(next);

            foreach (var phase in _phases)
            {
                if (next.Value % phase.CadenceTicks == 0)
                    await phase.ExecuteAsync(ctx, next);
            }
        }

        await ctx.Db.SaveChangesAsync();
    }
}
