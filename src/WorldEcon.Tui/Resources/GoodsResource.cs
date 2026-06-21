using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Economy;

namespace WorldEcon.Tui.Resources;

public sealed class GoodsResource : IResource
{
    public string Name => "goods";
    public IReadOnlyList<string> Aliases { get; } = ["good"];

    public async Task<ResourceTable> LoadAsync(TuiContext ctx)
    {
        var goods = (await ctx.Db.Goods
                .Where(g => g.WorldId == ctx.World.Id)
                .ToListAsync())
            .OrderBy(g => g.Name, StringComparer.Ordinal)
            .ThenBy(g => g.Id.Value)
            .ToList();

        var rows = goods
            .Select(g => new ResourceRow(g.Id.Value.ToString(),
            [
                g.Name,
                g.Category.ToString(),
                g.BaseValue.Units.ToString(),
                g.ShelfLifeTicks.ToString(),
                g.ConsumptionPerCapitaBp.ToString(),
            ]))
            .ToList();

        return new ResourceTable(["Name", "Category", "BaseValue", "ShelfLife", "ConsumptionBp"], rows);
    }

    public async Task<IReadOnlyList<string>> DetailsAsync(string key, TuiContext ctx)
    {
        var id = new GoodId(Guid.Parse(key));
        var g = await ctx.Db.Goods.FirstOrDefaultAsync(x => x.Id == id);
        if (g is null)
            return [$"Good {key} not found."];

        return
        [
            $"Name: {g.Name}",
            $"Category: {g.Category}",
            $"BaseValue: {g.BaseValue.Units}",
            $"BaseUnit: {g.BaseUnit}",
            $"Size: {g.Size}",
            $"ShelfLifeTicks: {g.ShelfLifeTicks}",
            $"Divisible: {g.Divisible}",
            $"ConsumptionPerCapitaBp: {g.ConsumptionPerCapitaBp}",
            $"Provenance: {g.Provenance}",
            $"Id: {g.Id.Value}",
        ];
    }
}
