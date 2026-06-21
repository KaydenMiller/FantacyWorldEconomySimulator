using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Economy;

namespace WorldEcon.Tui.Resources;

public sealed class StockpilesResource : IResource
{
    public string Name => "stockpiles";
    public IReadOnlyList<string> Aliases { get; } = ["stock"];

    public async Task<ResourceTable> LoadAsync(TuiContext ctx)
    {
        var settlementNames = await Lookups.SettlementNamesAsync(ctx);
        var goodNames = await Lookups.GoodNamesAsync(ctx);
        var shopNames = await ShopNamesAsync(ctx);

        var stockpiles = (await ctx.Db.Stockpiles
                .Where(s => s.WorldId == ctx.World.Id)
                .ToListAsync())
            .Select(s => (Stockpile: s,
                Owner: $"{s.OwnerKind}:{Lookups.ResolveOwner(s.OwnerKind, s.OwnerId, settlementNames, shopNames)}",
                Good: goodNames.Resolve(s.GoodId.Value)))
            .OrderBy(x => x.Owner, StringComparer.Ordinal)
            .ThenBy(x => x.Good, StringComparer.Ordinal)
            .ThenBy(x => x.Stockpile.Id.Value)
            .ToList();

        var rows = stockpiles
            .Select(x => new ResourceRow(x.Stockpile.Id.Value.ToString(),
            [
                x.Owner,
                x.Good,
                x.Stockpile.Quantity.ToString(),
                x.Stockpile.CostBasis.Units.ToString(),
                x.Stockpile.MarketPrice.Units.ToString(),
            ]))
            .ToList();

        return new ResourceTable(["Owner", "Good", "Qty", "CostBasis", "MarketPrice"], rows);
    }

    public async Task<IReadOnlyList<string>> DetailsAsync(string key, TuiContext ctx)
    {
        var id = new StockpileId(Guid.Parse(key));
        var s = await ctx.Db.Stockpiles.FirstOrDefaultAsync(x => x.Id == id);
        if (s is null)
            return [$"Stockpile {key} not found."];

        var settlementNames = await Lookups.SettlementNamesAsync(ctx);
        var goodNames = await Lookups.GoodNamesAsync(ctx);
        var shopNames = await ShopNamesAsync(ctx);

        return
        [
            $"OwnerKind: {s.OwnerKind}",
            $"Owner: {Lookups.ResolveOwner(s.OwnerKind, s.OwnerId, settlementNames, shopNames)}",
            $"Good: {goodNames.Resolve(s.GoodId.Value)}",
            $"Quantity: {s.Quantity}",
            $"CostBasis: {s.CostBasis.Units}",
            $"MarketPrice: {s.MarketPrice.Units}",
            $"Id: {s.Id.Value}",
        ];
    }

    private static async Task<Dictionary<Guid, string>> ShopNamesAsync(TuiContext ctx)
        => (await ctx.Db.Shops.Where(s => s.WorldId == ctx.World.Id).ToListAsync())
            .ToDictionary(s => s.Id.Value, s => s.Name);
}
