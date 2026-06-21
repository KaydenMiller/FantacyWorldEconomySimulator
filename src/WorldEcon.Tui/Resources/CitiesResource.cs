using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Geography;

namespace WorldEcon.Tui.Resources;

public sealed class CitiesResource : IResource
{
    public string Name => "cities";
    public IReadOnlyList<string> Aliases { get; } = ["city", "settlements", "settlement"];

    public async Task<ResourceTable> LoadAsync(TuiContext ctx)
    {
        var regionNames = await Lookups.RegionNamesAsync(ctx);

        var settlements = (await ctx.Db.Settlements
                .Where(s => s.WorldId == ctx.World.Id)
                .ToListAsync())
            .OrderBy(s => s.Name, StringComparer.Ordinal)
            .ThenBy(s => s.Id.Value)
            .ToList();

        var rows = settlements
            .Select(s => new ResourceRow(s.Id.Value.ToString(),
            [
                s.Name,
                s.Type.ToString(),
                s.Population.ToString(),
                regionNames.Resolve(s.RegionId.Value),
            ]))
            .ToList();

        return new ResourceTable(["Name", "Type", "Population", "Region"], rows);
    }

    public async Task<IReadOnlyList<string>> DetailsAsync(string key, TuiContext ctx)
    {
        var id = new SettlementId(Guid.Parse(key));
        var s = await ctx.Db.Settlements.FirstOrDefaultAsync(x => x.Id == id);
        if (s is null)
            return [$"Settlement {key} not found."];

        var region = await ctx.Db.Regions.FirstOrDefaultAsync(r => r.Id == s.RegionId);
        var country = region is null
            ? null
            : await ctx.Db.Countries.FirstOrDefaultAsync(c => c.Id == region.CountryId);
        var continent = country is null
            ? null
            : await ctx.Db.Continents.FirstOrDefaultAsync(c => c.Id == country.ContinentId);

        var shopCount = await ctx.Db.Shops.CountAsync(x => x.SettlementId == id);
        var nodeCount = await ctx.Db.ProductionNodes.CountAsync(x => x.SettlementId == id);

        return
        [
            $"Name: {s.Name}",
            $"Type: {s.Type}",
            $"Population: {s.Population}",
            $"X: {s.X}",
            $"Y: {s.Y}",
            $"Provenance: {s.Provenance}",
            $"Region: {region?.Name ?? s.RegionId.Value.ToString()}",
            $"Country: {country?.Name ?? "(unknown)"}",
            $"Continent: {continent?.Name ?? "(unknown)"}",
            $"Shops: {shopCount}",
            $"Production nodes: {nodeCount}",
            $"Id: {s.Id.Value}",
        ];
    }
}
