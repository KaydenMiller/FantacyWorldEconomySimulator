using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Economy;

namespace WorldEcon.Tui.Resources;

public sealed class RecipesResource : IResource
{
    public string Name => "recipes";
    public IReadOnlyList<string> Aliases { get; } = ["recipe"];

    public async Task<ResourceTable> LoadAsync(TuiContext ctx)
    {
        var goodNames = await Lookups.GoodNamesAsync(ctx);

        var recipes = (await ctx.Db.Recipes
                .Where(r => r.WorldId == ctx.World.Id)
                .ToListAsync())
            .OrderBy(r => r.Name, StringComparer.Ordinal)
            .ThenBy(r => r.Id.Value)
            .ToList();

        var rows = recipes
            .Select(r => new ResourceRow(r.Id.Value.ToString(),
            [
                r.Name,
                r.Facility.ToString(),
                Summarize(r, goodNames),
                r.TicksToProduce.ToString(),
            ]))
            .ToList();

        return new ResourceTable(["Name", "Facility", "In->Out", "Ticks"], rows);
    }

    public async Task<IReadOnlyList<string>> DetailsAsync(string key, TuiContext ctx)
    {
        var id = new RecipeId(Guid.Parse(key));
        var r = await ctx.Db.Recipes.FirstOrDefaultAsync(x => x.Id == id);
        if (r is null)
            return [$"Recipe {key} not found."];

        var goodNames = await Lookups.GoodNamesAsync(ctx);

        var lines = new List<string>
        {
            $"Name: {r.Name}",
            $"Facility: {r.Facility}",
            $"LaborCost: {r.LaborCost}",
            $"TicksToProduce: {r.TicksToProduce}",
            $"Id: {r.Id.Value}",
            "Inputs:",
        };
        foreach (var l in r.Inputs)
            lines.Add($"  {l.Quantity}x {goodNames.Resolve(l.Good.Value)}");
        lines.Add("Outputs:");
        foreach (var l in r.Outputs)
            lines.Add($"  {l.Quantity}x {goodNames.Resolve(l.Good.Value)}");

        return lines;
    }

    private static string Summarize(Recipe r, IReadOnlyDictionary<Guid, string> goodNames)
    {
        var inputs = string.Join(" + ", r.Inputs.Select(l => $"{l.Quantity} {goodNames.Resolve(l.Good.Value)}"));
        var outputs = string.Join(" + ", r.Outputs.Select(l => $"{l.Quantity} {goodNames.Resolve(l.Good.Value)}"));
        return $"{inputs} -> {outputs}";
    }
}
