using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Economy;

namespace WorldEcon.Tui.Resources;

public sealed class MerchantsResource : IResource
{
    public string Name => "merchants";
    public IReadOnlyList<string> Aliases { get; } = ["merchant"];

    public async Task<ResourceTable> LoadAsync(TuiContext ctx)
    {
        var settlementNames = await Lookups.SettlementNamesAsync(ctx);

        var merchants = (await ctx.Db.Merchants
                .Where(m => m.WorldId == ctx.World.Id)
                .ToListAsync())
            .OrderBy(m => settlementNames.Resolve(m.Seat.Value), StringComparer.Ordinal)
            .ThenBy(m => m.Id.Value)
            .ToList();

        var rows = merchants
            .Select(m => new ResourceRow(m.Id.Value.ToString(),
            [
                settlementNames.Resolve(m.Seat.Value),
                m.Capital.Units.ToString(),
                m.CargoCapacity.ToString(),
                m.Reach.ToString(),
            ]))
            .ToList();

        return new ResourceTable(["Seat", "Capital", "Capacity", "Reach"], rows);
    }

    public async Task<IReadOnlyList<string>> DetailsAsync(string key, TuiContext ctx)
    {
        var id = new MerchantId(Guid.Parse(key));
        var m = await ctx.Db.Merchants.FirstOrDefaultAsync(x => x.Id == id);
        if (m is null)
            return [$"Merchant {key} not found."];

        var settlementNames = await Lookups.SettlementNamesAsync(ctx);

        return
        [
            $"Seat: {settlementNames.Resolve(m.Seat.Value)}",
            $"Capital: {m.Capital.Units}",
            $"CargoCapacity: {m.CargoCapacity}",
            $"Reach: {m.Reach}",
            $"Id: {m.Id.Value}",
        ];
    }
}
