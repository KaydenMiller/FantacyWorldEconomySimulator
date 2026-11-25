using ErrorOr;
using WorldEcon.Application.Queries;

namespace WorldEcon.Application.Simulation;

/// <summary>The outcome of one step of a run: the tick reached and whether any phase ran on the way.</summary>
public sealed record RunStep(long Tick, bool PhasesRan);

/// <summary>
/// One open simulation run on a world file (all of it is committed by <see cref="EndAsync"/>, or none of it when the run
/// is disposed without ending). While it is open, other connections to the file see the state from before the run, so
/// the economy reads that should show the running state go through the run (<see cref="CaravanPositionsAsync"/>,
/// <see cref="SettlementDetailsAsync"/>, <see cref="SettlementListAsync"/>).
/// </summary>
public interface ISimulationRun : IAsyncDisposable
{
    /// <summary>The run's current tick.</summary>
    long CurrentTick { get; }

    /// <summary>The smallest tick after <see cref="CurrentTick"/> at which any phase is due.</summary>
    long NextBoundaryTick { get; }

    /// <summary>
    /// Moves the run's clock to <paramref name="tick"/> (or to the last boundary reached when <paramref name="ct"/> is
    /// cancelled). Returns the <c>Runner.TargetNotAfterNow</c> validation error when <paramref name="tick"/> is not after
    /// <see cref="CurrentTick"/>. Throws <see cref="InvalidOperationException"/> when another step is running.
    /// </summary>
    Task<ErrorOr<RunStep>> StepToAsync(long tick, IProgress<long>? progress = null, CancellationToken ct = default);

    /// <summary>Commits the run (end-of-run money snapshot and log pruning first).</summary>
    Task EndAsync();

    /// <summary><see cref="CaravanPositionsQuery"/> on the run's state.</summary>
    Task<ErrorOr<CaravanPositions>> CaravanPositionsAsync(CancellationToken ct = default);

    /// <summary><see cref="SettlementDetailsQuery.RunAsync(Persistence.WorldDbContext, Guid, CancellationToken)"/> on the run's state.</summary>
    Task<ErrorOr<SettlementDetails>> SettlementDetailsAsync(Guid settlementId, CancellationToken ct = default);

    /// <summary><see cref="SettlementDetailsQuery.ListAsync(Persistence.WorldDbContext, CancellationToken)"/> on the run's state.</summary>
    Task<ErrorOr<IReadOnlyList<(Guid Id, string Name)>>> SettlementListAsync(CancellationToken ct = default);
}
