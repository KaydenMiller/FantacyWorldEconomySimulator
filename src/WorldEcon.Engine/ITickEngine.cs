namespace WorldEcon.Engine;

/// <summary>Advances in-world time and runs cadence-gated phases for each tick.</summary>
public interface ITickEngine
{
    Task AdvanceAsync(SimulationContext ctx, long ticks);
}
