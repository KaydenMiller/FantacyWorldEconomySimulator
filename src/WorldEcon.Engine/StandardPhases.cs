namespace WorldEcon.Engine;

public static class StandardPhases
{
    public static IReadOnlyList<ISimulationPhase> All() => new ISimulationPhase[]
    {
        new Phases.MerchantSpawnPhase(),
        new Phases.ConsumerSpawnPhase(),
        new Phases.ConsumerIncomePhase(),
        new Phases.ProductionPhase(),
        new Phases.PriceDiscoveryPhase(),   // retail double-auction (replaces ConsumerDemandPhase)
        new Phases.PerishabilityPhase(),
        new Phases.PricingPhase(),           // formula price for NON-consumed (industrial) goods only
        new Phases.TradePhase(),
    };
}
