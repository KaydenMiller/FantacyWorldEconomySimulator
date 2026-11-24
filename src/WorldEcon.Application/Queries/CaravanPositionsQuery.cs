using ErrorOr;
using Microsoft.EntityFrameworkCore;
using WorldEcon.Application.Globe;
using WorldEcon.Domain.Geography;
using WorldEcon.Persistence;

namespace WorldEcon.Application.Queries;

/// <summary>One leg of a caravan's trip (see <c>CaravanLeg</c>): ticks and metres as plain numbers.</summary>
public sealed record CaravanLegInfo(int Sequence, Guid RouteId, TravelDirection Direction, long EnterTick, long ExitTick, long LengthMetres,
    long OffsetMetres);

/// <summary>A caravan that is not delivered, with names resolved and its legs, for drawing on the map at any tick.</summary>
public sealed record CaravanOnMap(
    Guid Id, Guid OwnerId, string SeatName, Guid OriginId, string OriginName, Guid DestinationId, string DestinationName,
    Guid GoodId, string GoodName, long Quantity, long UnitCostBasis, string VehicleClass, long DepartTick, long ArriveTick, long ReturnTick,
    IReadOnlyList<CaravanLegInfo> Legs);

/// <summary>The world's current tick and its undelivered caravans, ordered by id, read together.</summary>
public sealed record CaravanPositions(long CurrentTick, IReadOnlyList<CaravanOnMap> Caravans);

/// <summary>
/// Undelivered caravans of the first world with their legs, and the world's current tick. Positions are not computed
/// here: a viewer computes them for any tick with <c>LegSchedule.PositionAt</c>, so the clock can move without a database read.
/// </summary>
public static class CaravanPositionsQuery
{
    /// <summary>Opens <paramref name="dbPath"/> (which must exist) read-only for this query.</summary>
    public static async Task<ErrorOr<CaravanPositions>> RunAsync(string dbPath, CancellationToken ct = default)
    {
        await using var ctx = GlobeWorldLoader.Open(dbPath);
        return await RunAsync(ctx, ct);
    }

    public static async Task<ErrorOr<CaravanPositions>> RunAsync(WorldDbContext ctx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var world = await ctx.Worlds.AsNoTracking().FirstOrDefaultAsync(ct);
        if (world is null) return Error.NotFound("world.notFound", "The database contains no world.");
        var caravans = await ctx.Caravans.AsNoTracking().Where(c => c.WorldId == world.Id && !c.Delivered).ToListAsync(ct);
        var settlements = await ctx.Settlements.AsNoTracking().Where(s => s.WorldId == world.Id).ToDictionaryAsync(s => s.Id, s => s.Name, ct);
        var goods = await ctx.Goods.AsNoTracking().Where(g => g.WorldId == world.Id).ToDictionaryAsync(g => g.Id, g => g.Name, ct);
        var seats = await ctx.Merchants.AsNoTracking().Where(m => m.WorldId == world.Id).ToDictionaryAsync(m => m.Id, m => m.Seat, ct);
        string Name(SettlementId id) => settlements.GetValueOrDefault(id, "?");

        var list = caravans.OrderBy(c => c.Id.Value).Select(c => new CaravanOnMap(
                c.Id.Value, c.OwnerId.Value, seats.TryGetValue(c.OwnerId, out var seat) ? Name(seat) : "?",
                c.OriginId.Value, Name(c.OriginId), c.DestinationId.Value, Name(c.DestinationId),
                c.GoodId.Value, goods.GetValueOrDefault(c.GoodId, "?"), c.Quantity, c.UnitCostBasis.Units, c.VehicleClassName,
                c.DepartTick.Value, c.ArriveTick.Value, c.ReturnTick.Value,
                c.Legs.OrderBy(l => l.Sequence).Select(l => new CaravanLegInfo(l.Sequence, l.RouteId.Value, l.Direction, l.EnterTick.Value,
                    l.ExitTick.Value, l.LengthMetres, l.OffsetMetres)).ToArray()))
            .ToList();
        return new CaravanPositions(world.CurrentTick.Value, list);
    }
}
