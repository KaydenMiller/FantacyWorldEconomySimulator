using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Economy;

namespace WorldEcon.Tui.Resources;

public sealed class CaravansResource : IResource
{
    public string Name => "caravans";
    public IReadOnlyList<string> Aliases { get; } = ["caravan"];

    public async Task<ResourceTable> LoadAsync(TuiContext ctx)
    {
        var settlementNames = await Lookups.SettlementNamesAsync(ctx);
        var goodNames = await Lookups.GoodNamesAsync(ctx);

        var caravans = (await ctx.Db.Caravans
                .Where(c => c.WorldId == ctx.World.Id)
                .ToListAsync())
            .OrderBy(c => c.ArriveTick.Value)
            .ThenBy(c => c.Id.Value)
            .ToList();

        var rows = caravans
            .Select(c => new ResourceRow(c.Id.Value.ToString(),
            [
                settlementNames.Resolve(c.OriginId.Value),
                settlementNames.Resolve(c.DestinationId.Value),
                goodNames.Resolve(c.GoodId.Value),
                c.Quantity.ToString(),
                c.DepartTick.Value.ToString(),
                c.ArriveTick.Value.ToString(),
                c.Delivered.ToString(),
            ]))
            .ToList();

        return new ResourceTable(
            ["Origin", "Dest", "Good", "Qty", "Depart", "Arrive", "Delivered"], rows);
    }

    public async Task<IReadOnlyList<string>> DetailsAsync(string key, TuiContext ctx)
    {
        var id = new CaravanId(Guid.Parse(key));
        var c = await ctx.Db.Caravans.FirstOrDefaultAsync(x => x.Id == id);
        if (c is null)
            return [$"Caravan {key} not found."];

        var settlementNames = await Lookups.SettlementNamesAsync(ctx);
        var goodNames = await Lookups.GoodNamesAsync(ctx);

        return
        [
            $"Origin: {settlementNames.Resolve(c.OriginId.Value)}",
            $"Destination: {settlementNames.Resolve(c.DestinationId.Value)}",
            $"Good: {goodNames.Resolve(c.GoodId.Value)}",
            $"Quantity: {c.Quantity}",
            $"UnitCostBasis: {c.UnitCostBasis.Units}",
            $"DepartTick: {c.DepartTick.Value}",
            $"ArriveTick: {c.ArriveTick.Value}",
            $"Delivered: {c.Delivered}",
            $"Id: {c.Id.Value}",
        ];
    }
}
