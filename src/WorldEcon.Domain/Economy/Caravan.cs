using ErrorOr;
using WorldEcon.Domain.Geography;
using WorldEcon.SharedKernel;
using WorldEcon.SharedKernel.Domain;

namespace WorldEcon.Domain.Economy;

/// <summary>A single-good shipment in transit between two settlements (spec §7.1).</summary>
// NOTE: multi-good cargo is deferred; a caravan currently carries exactly one GoodId.
public sealed class Caravan : AggregateRoot<CaravanId>
{
    public WorldId WorldId { get; }
    public MerchantId OwnerId { get; private set; }
    public SettlementId OriginId { get; private set; }
    public SettlementId DestinationId { get; private set; }
    public GoodId GoodId { get; private set; }
    public long Quantity { get; private set; }
    public Money UnitCostBasis { get; private set; }
    public Tick DepartTick { get; private set; }
    public Tick ArriveTick { get; private set; }
    public bool Delivered { get; private set; }

    private Caravan() : base(default) { } // EF

    private Caravan(CaravanId id, WorldId worldId, MerchantId ownerId, SettlementId originId,
        SettlementId destinationId, GoodId goodId, long quantity, Money unitCostBasis,
        Tick departTick, Tick arriveTick) : base(id)
    {
        WorldId = worldId;
        OwnerId = ownerId;
        OriginId = originId;
        DestinationId = destinationId;
        GoodId = goodId;
        Quantity = quantity;
        UnitCostBasis = unitCostBasis;
        DepartTick = departTick;
        ArriveTick = arriveTick;
        Delivered = false;
    }

    public static ErrorOr<Caravan> Create(WorldId worldId, MerchantId owner, SettlementId origin,
        SettlementId destination, GoodId good, long quantity, Money unitCostBasis,
        Tick departTick, Tick arriveTick)
    {
        if (quantity < 1)
            return Error.Validation("caravan.quantity.tooSmall", "Quantity must be at least 1.");
        if (unitCostBasis.IsNegative)
            return Error.Validation("caravan.unitcostbasis.negative", "Unit cost basis must not be negative.");
        if (arriveTick.Value <= departTick.Value)
            return Error.Validation("caravan.arrive.notAfterDepart", "Arrive tick must be after depart tick.");
        if (origin == destination)
            return Error.Validation("caravan.route.sameEndpoints", "Origin and destination must differ.");

        return new Caravan(CaravanId.New(), worldId, owner, origin, destination, good, quantity,
            unitCostBasis, departTick, arriveTick);
    }

    /// <summary>Marks the shipment as delivered. Idempotency is not allowed: a second call is a bug.</summary>
    public void MarkDelivered()
    {
        if (Delivered)
            throw new InvalidOperationException("Caravan is already delivered.");
        Delivered = true;
    }
}
