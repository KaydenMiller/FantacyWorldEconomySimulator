using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Economy;

namespace WorldEcon.Tui.Resources;

public sealed class NodesResource : IResource
{
    public string Name => "nodes";
    public IReadOnlyList<string> Aliases { get; } = ["node", "production"];

    public async Task<ResourceTable> LoadAsync(TuiContext ctx)
    {
        var settlementNames = await Lookups.SettlementNamesAsync(ctx);
        var recipeNames = await Lookups.RecipeNamesAsync(ctx);

        var nodes = (await ctx.Db.ProductionNodes
                .Where(n => n.WorldId == ctx.World.Id)
                .ToListAsync())
            .Select(n => (Node: n,
                Settlement: settlementNames.Resolve(n.SettlementId.Value),
                Recipe: recipeNames.Resolve(n.RecipeId.Value)))
            .OrderBy(x => x.Settlement, StringComparer.Ordinal)
            .ThenBy(x => x.Recipe, StringComparer.Ordinal)
            .ThenBy(x => x.Node.Id.Value)
            .ToList();

        var rows = nodes
            .Select(x => new ResourceRow(x.Node.Id.Value.ToString(),
            [
                x.Settlement,
                x.Recipe,
                x.Node.Facility.ToString(),
                x.Node.ThroughputCap.ToString(),
                x.Node.Disabled.ToString(),
            ]))
            .ToList();

        return new ResourceTable(["Settlement", "Recipe", "Facility", "Cap", "Disabled"], rows);
    }

    public async Task<IReadOnlyList<string>> DetailsAsync(string key, TuiContext ctx)
    {
        var id = new ProductionNodeId(Guid.Parse(key));
        var n = await ctx.Db.ProductionNodes.FirstOrDefaultAsync(x => x.Id == id);
        if (n is null)
            return [$"Production node {key} not found."];

        var settlementNames = await Lookups.SettlementNamesAsync(ctx);
        var recipeNames = await Lookups.RecipeNamesAsync(ctx);

        return
        [
            $"Settlement: {settlementNames.Resolve(n.SettlementId.Value)}",
            $"Recipe: {recipeNames.Resolve(n.RecipeId.Value)}",
            $"Facility: {n.Facility}",
            $"ThroughputCap: {n.ThroughputCap}",
            $"Disabled: {n.Disabled}",
            $"Id: {n.Id.Value}",
        ];
    }
}
