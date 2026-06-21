using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Economy;
using WorldEcon.Domain.Geography;
using WorldEcon.Persistence;

namespace WorldEcon.Tui.Resources;

/// <summary>Name-resolution helpers shared by resources. Each loader is world-scoped and falls back
/// to the id string when a name can't be resolved (handled at the call site).</summary>
internal static class Lookups
{
    public static async Task<Dictionary<Guid, string>> GoodNamesAsync(TuiContext ctx)
        => (await ctx.Db.Goods.Where(g => g.WorldId == ctx.World.Id).ToListAsync())
            .ToDictionary(g => g.Id.Value, g => g.Name);

    public static async Task<Dictionary<Guid, string>> SettlementNamesAsync(TuiContext ctx)
        => (await ctx.Db.Settlements.Where(s => s.WorldId == ctx.World.Id).ToListAsync())
            .ToDictionary(s => s.Id.Value, s => s.Name);

    public static async Task<Dictionary<Guid, string>> RegionNamesAsync(TuiContext ctx)
        => (await ctx.Db.Regions.Where(r => r.WorldId == ctx.World.Id).ToListAsync())
            .ToDictionary(r => r.Id.Value, r => r.Name);

    public static async Task<Dictionary<Guid, string>> RecipeNamesAsync(TuiContext ctx)
        => (await ctx.Db.Recipes.Where(r => r.WorldId == ctx.World.Id).ToListAsync())
            .ToDictionary(r => r.Id.Value, r => r.Name);

    public static string Resolve(this IReadOnlyDictionary<Guid, string> names, Guid id)
        => names.TryGetValue(id, out var n) ? n : id.ToString();

    /// <summary>Resolves a stockpile owner's display name by kind + owner id.</summary>
    public static string ResolveOwner(
        StockpileOwnerKind kind, Guid ownerId,
        IReadOnlyDictionary<Guid, string> settlementNames,
        IReadOnlyDictionary<Guid, string> shopNames)
        => kind switch
        {
            StockpileOwnerKind.Shop => shopNames.TryGetValue(ownerId, out var s) ? s : ownerId.ToString(),
            StockpileOwnerKind.SettlementMarket =>
                settlementNames.TryGetValue(ownerId, out var s) ? s : ownerId.ToString(),
            _ => ownerId.ToString(),
        };
}
