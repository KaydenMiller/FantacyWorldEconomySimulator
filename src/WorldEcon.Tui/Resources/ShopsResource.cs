using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Economy;

namespace WorldEcon.Tui.Resources;

public sealed class ShopsResource : IResource
{
    public string Name => "shops";
    public IReadOnlyList<string> Aliases { get; } = ["shop"];

    public async Task<ResourceTable> LoadAsync(TuiContext ctx)
    {
        var settlementNames = await Lookups.SettlementNamesAsync(ctx);

        var shops = (await ctx.Db.Shops
                .Where(s => s.WorldId == ctx.World.Id)
                .ToListAsync())
            .OrderBy(s => s.Name, StringComparer.Ordinal)
            .ThenBy(s => s.Id.Value)
            .ToList();

        var rows = shops
            .Select(s => new ResourceRow(s.Id.Value.ToString(),
            [
                s.Name,
                settlementNames.Resolve(s.SettlementId.Value),
                FormatMarkup(s.MarkupBp),
                s.Till.Units.ToString(),
            ]))
            .ToList();

        return new ResourceTable(["Name", "Settlement", "Markup%", "Till"], rows);
    }

    public async Task<IReadOnlyList<string>> DetailsAsync(string key, TuiContext ctx)
    {
        var id = new ShopId(Guid.Parse(key));
        var s = await ctx.Db.Shops.FirstOrDefaultAsync(x => x.Id == id);
        if (s is null)
            return [$"Shop {key} not found."];

        var settlementNames = await Lookups.SettlementNamesAsync(ctx);
        var goodNames = await Lookups.GoodNamesAsync(ctx);

        var lines = new List<string>
        {
            $"Name: {s.Name}",
            $"Settlement: {settlementNames.Resolve(s.SettlementId.Value)}",
            $"Markup%: {FormatMarkup(s.MarkupBp)}",
            $"Till: {s.Till.Units}",
            $"Id: {s.Id.Value}",
            "Stock:",
        };

        var stock = (await ctx.Db.Stockpiles
                .Where(st => st.WorldId == ctx.World.Id
                    && st.OwnerKind == StockpileOwnerKind.Shop
                    && st.OwnerId == s.Id.Value)
                .ToListAsync())
            .OrderBy(st => goodNames.Resolve(st.GoodId.Value), StringComparer.Ordinal)
            .ThenBy(st => st.Id.Value)
            .ToList();

        if (stock.Count == 0)
            lines.Add("  (none)");
        else
            foreach (var st in stock)
                lines.Add($"  {goodNames.Resolve(st.GoodId.Value)}: {st.Quantity} @ {st.CostBasis.Units}");

        return lines;
    }

    private static string FormatMarkup(int markupBp) => $"{markupBp / 100m:0.##}";
}
