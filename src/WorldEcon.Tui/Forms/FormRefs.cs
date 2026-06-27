using Microsoft.EntityFrameworkCore;

namespace WorldEcon.Tui.Forms;

/// <summary>Loads world-scoped (name → id) option lists for entity-reference form fields. Ordered by
/// name for a stable, readable chooser.</summary>
internal static class FormRefs
{
    public static async Task<List<(string Name, Guid Id)>> ContinentsAsync(TuiContext ctx)
        => (await ctx.Db.Continents.Where(x => x.WorldId == ctx.World.Id).ToListAsync())
            .OrderBy(x => x.Name).Select(x => (x.Name, x.Id.Value)).ToList();

    public static async Task<List<(string Name, Guid Id)>> CountriesAsync(TuiContext ctx)
        => (await ctx.Db.Countries.Where(x => x.WorldId == ctx.World.Id).ToListAsync())
            .OrderBy(x => x.Name).Select(x => (x.Name, x.Id.Value)).ToList();

    public static async Task<List<(string Name, Guid Id)>> RegionsAsync(TuiContext ctx)
        => (await ctx.Db.Regions.Where(x => x.WorldId == ctx.World.Id).ToListAsync())
            .OrderBy(x => x.Name).Select(x => (x.Name, x.Id.Value)).ToList();

    public static async Task<List<(string Name, Guid Id)>> SettlementsAsync(TuiContext ctx)
        => (await ctx.Db.Settlements.Where(x => x.WorldId == ctx.World.Id).ToListAsync())
            .OrderBy(x => x.Name).Select(x => (x.Name, x.Id.Value)).ToList();

    public static async Task<List<(string Name, Guid Id)>> GoodsAsync(TuiContext ctx)
        => (await ctx.Db.Goods.Where(x => x.WorldId == ctx.World.Id).ToListAsync())
            .OrderBy(x => x.Name).Select(x => (x.Name, x.Id.Value)).ToList();

    public static async Task<List<(string Name, Guid Id)>> ShopsAsync(TuiContext ctx)
        => (await ctx.Db.Shops.Where(x => x.WorldId == ctx.World.Id).ToListAsync())
            .OrderBy(x => x.Name).Select(x => (x.Name, x.Id.Value)).ToList();

    public static async Task<List<(string Name, Guid Id)>> RecipesAsync(TuiContext ctx)
        => (await ctx.Db.Recipes.Where(x => x.WorldId == ctx.World.Id).ToListAsync())
            .OrderBy(x => x.Name).Select(x => (x.Name, x.Id.Value)).ToList();
}
