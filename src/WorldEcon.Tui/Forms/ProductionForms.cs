using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Economy;
using WorldEcon.Domain.Geography;

namespace WorldEcon.Tui.Forms;

/// <summary>Create a <see cref="Recipe"/> with input and output lines.</summary>
public sealed class RecipeForm : IEntityForm
{
    public string Label => "Recipe";
    public string? ResourceName => "recipes";

    public async Task<FormOutcome> RunAsync(TuiContext ctx, IUserInteraction ui)
    {
        const string t = "New Recipe";

        var goods = await FormRefs.GoodsAsync(ctx);
        if (goods.Count == 0)
            return FormOutcome.Fail("Create a good first.");

        var name = await FormPrompts.RequiredTextAsync(ui, t, "Name:");
        if (name is null) return FormOutcome.Cancelled;

        var facility = await FormPrompts.EnumAsync<FacilityType>(ui, t, "Facility:");
        if (facility is null) return FormOutcome.Cancelled;

        var lines = new List<RecipeLine>();

        // Inputs (zero or more).
        while (true)
        {
            var add = await FormPrompts.BoolAsync(ui, t, lines.Count == 0 ? "Add an input good?" : "Add another input?");
            if (add is null) return FormOutcome.Cancelled;
            if (add == false) break;

            var goodId = await FormPrompts.RefAsync(ui, t, "Input good:", goods);
            if (goodId is null) return FormOutcome.Cancelled;
            var qty = await FormPrompts.NumberAsync(ui, t, "Input quantity:");
            if (qty is null) return FormOutcome.Cancelled;
            lines.Add(new RecipeLine(new GoodId(goodId.Value), qty.Value, RecipeLineKind.Input));
        }

        // Outputs (at least one).
        var outputCount = 0;
        while (true)
        {
            if (outputCount > 0)
            {
                var more = await FormPrompts.BoolAsync(ui, t, "Add another output?");
                if (more is null) return FormOutcome.Cancelled;
                if (more == false) break;
            }

            var goodId = await FormPrompts.RefAsync(ui, t, "Output good:", goods);
            if (goodId is null) return FormOutcome.Cancelled;
            var qty = await FormPrompts.NumberAsync(ui, t, "Output quantity:");
            if (qty is null) return FormOutcome.Cancelled;
            lines.Add(new RecipeLine(new GoodId(goodId.Value), qty.Value, RecipeLineKind.Output));
            outputCount++;
        }

        var laborCost = await FormPrompts.NumberAsync(ui, t, "Labor cost (in copper, 0 = none):", 0);
        if (laborCost is null) return FormOutcome.Cancelled;

        var ticks = await FormPrompts.NumberAsync(ui, t, "Ticks to produce one batch (1440 = 1 day):", 1440);
        if (ticks is null) return FormOutcome.Cancelled;

        var result = Recipe.Create(ctx.World.Id, name, facility.Value, lines, laborCost.Value, ticks.Value);
        if (result.IsError) return FormOutcome.Fail(result.Errors[0].Description);

        ctx.Db.Recipes.Add(result.Value);
        await ctx.Db.SaveChangesAsync();
        return FormOutcome.Ok($"Created recipe '{name}'.");
    }
}

/// <summary>Create a <see cref="ProductionNode"/> (a facility running a recipe at a settlement). One
/// node makes ~one batch/day; add several nodes for more throughput.</summary>
public sealed class ProductionNodeForm : IEntityForm
{
    public string Label => "Production node (factory)";
    public string? ResourceName => "factories";

    public async Task<FormOutcome> RunAsync(TuiContext ctx, IUserInteraction ui)
    {
        const string t = "New Production Node";

        var settlements = await FormRefs.SettlementsAsync(ctx);
        if (settlements.Count == 0)
            return FormOutcome.Fail("Create a settlement first.");
        var recipes = await FormRefs.RecipesAsync(ctx);
        if (recipes.Count == 0)
            return FormOutcome.Fail("Create a recipe first.");

        var settlementId = await FormPrompts.RefAsync(ui, t, "Settlement:", settlements);
        if (settlementId is null) return FormOutcome.Cancelled;

        var recipeId = await FormPrompts.RefAsync(ui, t, "Recipe:", recipes);
        if (recipeId is null) return FormOutcome.Cancelled;

        var cap = await FormPrompts.NumberAsync(ui, t, "Throughput cap (concurrent batches; 1 is typical):", 1);
        if (cap is null) return FormOutcome.Cancelled;

        // Facility is taken from the chosen recipe (mirrors the seeder).
        var recipe = await ctx.Db.Recipes.FirstAsync(r => r.Id == new RecipeId(recipeId.Value));

        var result = ProductionNode.Create(ctx.World.Id, new SettlementId(settlementId.Value),
            recipe.Id, recipe.Facility, cap.Value);
        if (result.IsError) return FormOutcome.Fail(result.Errors[0].Description);

        ctx.Db.ProductionNodes.Add(result.Value);
        await ctx.Db.SaveChangesAsync();
        return FormOutcome.Ok($"Created {recipe.Facility} producing '{recipe.Name}'.");
    }
}
