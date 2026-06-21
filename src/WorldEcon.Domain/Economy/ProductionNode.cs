using ErrorOr;
using WorldEcon.Domain.Geography;
using WorldEcon.SharedKernel.Domain;

namespace WorldEcon.Domain.Economy;

public sealed class ProductionNode : AggregateRoot<ProductionNodeId>
{
    public WorldId WorldId { get; }
    public SettlementId SettlementId { get; private set; }
    public RecipeId RecipeId { get; private set; }
    public FacilityType Facility { get; private set; }
    public long ThroughputCap { get; private set; } // max concurrent active batches
    public bool Disabled { get; private set; }

    private ProductionNode() : base(default) { } // EF

    private ProductionNode(ProductionNodeId id, WorldId worldId, SettlementId settlementId,
        RecipeId recipeId, FacilityType facility, long throughputCap) : base(id)
    {
        WorldId = worldId;
        SettlementId = settlementId;
        RecipeId = recipeId;
        Facility = facility;
        ThroughputCap = throughputCap;
    }

    public static ErrorOr<ProductionNode> Create(WorldId worldId, SettlementId settlementId,
        RecipeId recipeId, FacilityType facility, long throughputCap)
    {
        if (throughputCap < 1)
            return Error.Validation("productionnode.throughput.belowone", "Throughput cap must be at least 1.");

        return new ProductionNode(ProductionNodeId.New(), worldId, settlementId, recipeId, facility, throughputCap);
    }

    public void Disable() => Disabled = true;

    public void Enable() => Disabled = false;
}
