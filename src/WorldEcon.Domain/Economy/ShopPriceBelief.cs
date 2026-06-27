using ErrorOr;
using WorldEcon.Domain.Geography;
using WorldEcon.SharedKernel;
using WorldEcon.SharedKernel.Domain;

namespace WorldEcon.Domain.Economy;

/// <summary>
/// A shop's evolving belief about what a good is worth: the price band <c>[Low, High]</c> it offers
/// within. The band narrows as the shop's asks succeed (growing confidence) and widens/shifts toward
/// the market when they fail. One per (shop, good); created lazily, anchored on the good's base value.
/// All math is integer (base currency units) and deterministic.
/// </summary>
public sealed class ShopPriceBelief : AggregateRoot<ShopPriceBeliefId>
{
    public WorldId WorldId { get; }
    public ShopId ShopId { get; private set; }
    public GoodId GoodId { get; private set; }
    public Money Low { get; private set; }
    public Money High { get; private set; }

    private ShopPriceBelief() : base(default) { } // EF

    private ShopPriceBelief(ShopPriceBeliefId id, WorldId worldId, ShopId shopId, GoodId goodId,
        Money low, Money high) : base(id)
    {
        WorldId = worldId;
        ShopId = shopId;
        GoodId = goodId;
        Low = low;
        High = high;
    }

    /// <summary>Bootstraps a band around the good's base value (0.8× … 1.2×), so a new market starts
    /// sane and converges within a few days.</summary>
    public static ShopPriceBelief Bootstrap(WorldId worldId, ShopId shopId, GoodId goodId, Money baseValue)
    {
        long low = System.Math.Max(1, baseValue.Units * 8000 / 10000);
        long high = System.Math.Max(low, baseValue.Units * 12000 / 10000);
        return new ShopPriceBelief(ShopPriceBeliefId.New(), worldId, shopId, goodId, new Money(low), new Money(high));
    }

    /// <summary>The shop's ask succeeded → narrow the band toward its centre (more confident).</summary>
    public void RecordSale(long narrowFractionBasisPoints)
    {
        long center = (Low.Units + High.Units) / 2;
        long low = Low.Units + (center - Low.Units) * narrowFractionBasisPoints / 10_000;
        long high = High.Units - (High.Units - center) * narrowFractionBasisPoints / 10_000;
        Set(low, high);
    }

    /// <summary>The shop's ask failed → widen the top and shift the band toward where the market
    /// actually cleared (or, if nothing cleared, down toward the good's base value to find a buyer).</summary>
    public void RecordMiss(long widenFractionBasisPoints, long shiftFractionBasisPoints,
        Money? clearingPrice, Money baseValue)
    {
        long span = High.Units - Low.Units;
        long low = Low.Units;
        long high = High.Units + span * widenFractionBasisPoints / 10_000;
        long target = (clearingPrice ?? baseValue).Units;
        low += (target - low) * shiftFractionBasisPoints / 10_000;
        high += (target - high) * shiftFractionBasisPoints / 10_000;
        Set(low, high);
    }

    private void Set(long low, long high)
    {
        low = System.Math.Max(1, low);
        high = System.Math.Max(low, high);
        Low = new Money(low);
        High = new Money(high);
    }
}
