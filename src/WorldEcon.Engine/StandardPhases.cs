namespace WorldEcon.Engine;

public static class StandardPhases
{
    public static IReadOnlyList<ISimulationPhase> All() => new ISimulationPhase[] { new Phases.ProductionPhase() };
}
