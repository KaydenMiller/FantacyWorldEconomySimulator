using ErrorOr;
using WorldEcon.Domain.Geography;
using WorldEcon.SharedKernel.Domain;

namespace WorldEcon.Domain.Economy;

public sealed class ResourceEndowment : AggregateRoot<ResourceEndowmentId>
{
    public WorldId WorldId { get; }
    public SettlementId SettlementId { get; private set; }
    public GoodId GoodId { get; private set; }
    public long Abundance { get; private set; } // raw units extracted per production cycle
    public ShopId? ProducerShopId { get; private set; }

    /// <summary>Links this endowment to its producer-shop. Write-once: a second call is ignored.</summary>
    public void AssignProducerShop(ShopId shopId)
    {
        if (ProducerShopId.HasValue) return;
        ProducerShopId = shopId;
    }

    private ResourceEndowment() : base(default) { } // EF

    private ResourceEndowment(ResourceEndowmentId id, WorldId worldId, SettlementId settlementId,
        GoodId goodId, long abundance) : base(id)
    {
        WorldId = worldId;
        SettlementId = settlementId;
        GoodId = goodId;
        Abundance = abundance;
    }

    public static ErrorOr<ResourceEndowment> Create(WorldId worldId, SettlementId settlementId,
        GoodId goodId, long abundance)
    {
        if (abundance < 0)
            return Error.Validation("resourceendowment.abundance.negative", "Abundance must not be negative.");

        return new ResourceEndowment(ResourceEndowmentId.New(), worldId, settlementId, goodId, abundance);
    }
}
