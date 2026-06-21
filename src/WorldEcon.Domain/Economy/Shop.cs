using ErrorOr;
using WorldEcon.Domain.Geography;
using WorldEcon.SharedKernel;
using WorldEcon.SharedKernel.Domain;

namespace WorldEcon.Domain.Economy;

/// <summary>Quoted price for one good at one shop.</summary>
public readonly record struct ShopQuote(Money SalePrice, Money MarginAbs, int MarginBp);

public sealed class Shop : AggregateRoot<ShopId>
{
    public WorldId WorldId { get; }
    public SettlementId SettlementId { get; private set; }
    public string Name { get; private set; }
    public int MarkupBp { get; private set; }
    public Money Till { get; private set; }

    private Shop() : base(default) { Name = null!; } // EF

    private Shop(ShopId id, WorldId worldId, SettlementId settlementId, string name, int markupBp, Money till) : base(id)
    {
        WorldId = worldId;
        SettlementId = settlementId;
        Name = name;
        MarkupBp = markupBp;
        Till = till;
    }

    public static ErrorOr<Shop> Create(WorldId worldId, SettlementId settlementId, string name, int markupBp, Money till)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Error.Validation("shop.name.blank", "Shop name must not be blank.");
        if (markupBp < 0)
            return Error.Validation("shop.markup.negative", "Markup must not be negative.");
        if (till.IsNegative)
            return Error.Validation("shop.till.negative", "Till must not be negative.");

        return new Shop(ShopId.New(), worldId, settlementId, name.Trim(), markupBp, till);
    }

    /// <summary>Sale price = cost + cost×markup; margin reported absolute and in basis points (over cost).</summary>
    public ShopQuote Quote(Money unitCostBasis)
    {
        var salePrice = new Money(unitCostBasis.Units + FixedMath.MulBp(unitCostBasis.Units, MarkupBp));
        return new ShopQuote(salePrice, salePrice - unitCostBasis, MarkupBp);
    }
}
