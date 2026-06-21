using ErrorOr;
using WorldEcon.Domain.Geography;
using WorldEcon.SharedKernel;
using WorldEcon.SharedKernel.Domain;

namespace WorldEcon.Domain.Economy;

public sealed class Stockpile : AggregateRoot<StockpileId>
{
    public WorldId WorldId { get; }
    public StockpileOwnerKind OwnerKind { get; private set; }
    public Guid OwnerId { get; private set; }
    public GoodId GoodId { get; private set; }
    public long Quantity { get; private set; }
    public Money CostBasis { get; private set; } // per-unit

    private Stockpile() : base(default) { } // EF

    private Stockpile(StockpileId id, WorldId worldId, StockpileOwnerKind ownerKind, Guid ownerId,
        GoodId goodId, long quantity, Money costBasis) : base(id)
    {
        WorldId = worldId;
        OwnerKind = ownerKind;
        OwnerId = ownerId;
        GoodId = goodId;
        Quantity = quantity;
        CostBasis = costBasis;
    }

    public static ErrorOr<Stockpile> CreateForShop(WorldId worldId, ShopId shop, GoodId good, long quantity, Money unitCostBasis)
        => Create(worldId, StockpileOwnerKind.Shop, shop.Value, good, quantity, unitCostBasis);

    public static ErrorOr<Stockpile> Create(WorldId worldId, StockpileOwnerKind ownerKind, Guid ownerId,
        GoodId good, long quantity, Money unitCostBasis)
    {
        if (quantity < 0)
            return Error.Validation("stockpile.quantity.negative", "Quantity must not be negative.");
        if (unitCostBasis.IsNegative)
            return Error.Validation("stockpile.costbasis.negative", "Cost basis must not be negative.");

        return new Stockpile(StockpileId.New(), worldId, ownerKind, ownerId, good, quantity, unitCostBasis);
    }

    public void Deposit(long quantity, Money incomingUnitBasis, ICostBasisValuation valuation)
    {
        if (quantity <= 0)
            throw new ArgumentOutOfRangeException(nameof(quantity), "Deposit quantity must be positive.");
        CostBasis = valuation.Blend(Quantity, CostBasis, quantity, incomingUnitBasis);
        Quantity += quantity;
    }

    public ErrorOr<Success> Withdraw(long quantity)
    {
        if (quantity <= 0)
            return Error.Validation("stockpile.withdraw.nonpositive", "Withdraw quantity must be positive.");
        if (quantity > Quantity)
            return Error.Validation("stockpile.withdraw.insufficient", "Not enough on hand to withdraw.");
        Quantity -= quantity;
        return Result.Success;
    }
}
