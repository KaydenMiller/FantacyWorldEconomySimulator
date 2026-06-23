namespace WorldEcon.Engine;

public static class StandardPhases
{
    public static IReadOnlyList<ISimulationPhase> All() => new ISimulationPhase[]
    {
        new Phases.MerchantSpawnPhase(),
        new Phases.ConsumerSpawnPhase(),
        new Phases.ConsumerIncomePhase(),
        new Phases.ProductionPhase(),
        new Phases.ConsumptionPhase(),
        new Phases.PerishabilityPhase(),
        new Phases.PricingPhase(),
        new Phases.TradePhase(),
    };
}
