using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Geography;
using WorldEcon.Persistence;
using WorldEcon.SharedKernel;

namespace WorldEcon.Engine;

/// <summary>
/// The core loop that advances in-world time and runs each cadence-gated phase whose cadence divides
/// the new tick. Each advance is an <see cref="EngineRun"/>: one transaction, stepped to the target and
/// committed at the end (see that type for the skip-ahead loop and the save discipline). Phases run in a
/// fixed order; runs are not bit-for-bit repeatable yet, because rows created during a run get random ids
/// and some phases visit rows in id order (see "Seeded entity IDs" in the roadmap).
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

    /// <summary>
    /// Advances <paramref name="ctx"/>'s world by <paramref name="ticks"/> as one run (<see cref="BeginRunAsync(SimulationContext, CancellationToken)"/>):
    /// all of it is committed, or none of it is when it fails.
    /// </summary>
    public async Task AdvanceAsync(SimulationContext ctx, long ticks)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        if (ticks < 0)
            throw new ArgumentOutOfRangeException(nameof(ticks), ticks, "Cannot advance a negative number of ticks.");

        await using var run = await BeginRunAsync(ctx);
        await run.StepToAsync(new Tick(run.Context.World.CurrentTick.Value + ticks));
        await run.EndAsync();
    }

    /// <summary>
    /// Begins a run on <paramref name="db"/>: the transaction first (SQLite <c>BEGIN IMMEDIATE</c>, so no other writer
    /// can change the file between the load and the commit), then the world is loaded inside it. Joins a transaction
    /// already open on <paramref name="db"/>.
    /// </summary>
    public async Task<EngineRun> BeginRunAsync(WorldDbContext db, WorldId worldId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var tx = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(ct) : null;
        try
        {
            var ctx = await SimulationContext.LoadAsync(db, worldId);
            return new EngineRun(ctx, _phases, tx);
        }
        catch
        {
            if (tx is not null) await tx.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Begins a run on an already loaded context: the transaction first (joining one already open on its database), then
    /// the world row is reloaded so the run starts from the committed clock.
    /// </summary>
    public async Task<EngineRun> BeginRunAsync(SimulationContext ctx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var db = ctx.Db;
        var tx = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(ct) : null;
        try
        {
            var entry = db.Entry(ctx.World);
            if (entry.State == EntityState.Detached) db.Attach(ctx.World);
            await entry.ReloadAsync(ct);
            return new EngineRun(ctx, _phases, tx);
        }
        catch
        {
            if (tx is not null) await tx.DisposeAsync();
            throw;
        }
    }
}
