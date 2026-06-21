using WorldEcon.SharedKernel;

namespace WorldEcon.Engine;

/// <summary>
/// A cadence-gated subsystem phase run within a tick. Concrete phases (production, trade, ...)
/// arrive in later plan phases; the engine only orchestrates ordering and cadence here.
/// </summary>
public interface ISimulationPhase
{
    /// <summary>Stable name; used as a tie-breaker when two phases share an <see cref="Order"/>.</summary>
    string Name { get; }

    /// <summary>Fixed run order within a tick (lower runs first).</summary>
    int Order { get; }

    /// <summary>Run only when <c>tick.Value % CadenceTicks == 0</c>. Must be &gt; 0.</summary>
    long CadenceTicks { get; }

    Task ExecuteAsync(SimulationContext ctx, Tick tick);
}
