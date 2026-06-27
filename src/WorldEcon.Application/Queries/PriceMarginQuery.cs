using ErrorOr;
using WorldEcon.Domain.Economy;
using WorldEcon.Domain.Geography;
using WorldEcon.SharedKernel;

namespace WorldEcon.Application.Queries;

/// <summary>One shop's offer for the queried good. NextShipment is omitted until stage 3 (caravans/batches).</summary>
public sealed record ShopPriceLine(
    ShopId ShopId,
    string ShopName,
    long Stock,
    Money UnitCostBasis,
    Money RetailPrice,
    Money SalePrice,
    Money MarginAbs,
    int MarginBp);

public sealed record PriceMarginResult(
    GoodId GoodId,
    string GoodName,
    SettlementId SettlementId,
    IReadOnlyList<ShopPriceLine> Shops);

public interface IPriceMarginQuery
{
    Task<ErrorOr<PriceMarginResult>> RunAsync(WorldId worldId, SettlementId settlementId, GoodId goodId);
}

public sealed class PriceMarginQuery(IShopRepository shops, IStockpileRepository stockpiles, IGoodRepository goods)
    : IPriceMarginQuery
{
    public async Task<ErrorOr<PriceMarginResult>> RunAsync(WorldId worldId, SettlementId settlementId, GoodId goodId)
    {
        var good = await goods.GetAsync(goodId);
        if (good is null)
            return Error.NotFound("good.notfound", "Good not found.");

        var shopsInTown = await shops.ListBySettlementAsync(settlementId);

        var lines = new List<ShopPriceLine>();
        foreach (var shop in shopsInTown)
        {
            var stock = await stockpiles.GetByOwnerAndGoodAsync(StockpileOwnerKind.Shop, shop.Id.Value, goodId);
            if (stock is null || stock.Quantity <= 0)
                continue;

            var quote = shop.Quote(stock.CostBasis);
            lines.Add(new ShopPriceLine(
                shop.Id, shop.Name, stock.Quantity, stock.CostBasis,
                stock.MarketPrice, quote.SalePrice, quote.MarginAbs, quote.MarginBp));
        }

        var ordered = lines
            .OrderBy(l => l.ShopName, StringComparer.Ordinal)
            .ThenBy(l => l.ShopId.Value)
            .ToList();

        return new PriceMarginResult(good.Id, good.Name, settlementId, ordered);
    }
}
