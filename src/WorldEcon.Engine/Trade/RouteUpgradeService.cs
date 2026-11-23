using ErrorOr;
using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Economy;
using WorldEcon.Domain.Geography;
using WorldEcon.Domain.Logging;
using WorldEcon.Engine.Logging;
using WorldEcon.Persistence;
using WorldEcon.SharedKernel;

namespace WorldEcon.Engine.Trade;

/// <summary>
/// Changes a route's surface and re-times the undelivered caravans still to finish a leg on it: covered distance keeps
/// its old timing, the rest of the trip is timed under the current surfaces (<see cref="LegSchedule.Rebase"/>).
/// The caller saves.
/// </summary>
public static class RouteUpgradeService
{
    public static async Task<ErrorOr<int>> UpgradeAsync(WorldDbContext db, World world, RouteId routeId,
        RouteSurface newSurface, Tick tick, LogEventEmitter log)
    {
        var route = await db.Routes.FirstOrDefaultAsync(r => r.Id == routeId && r.WorldId == world.Id);
        if (route is null)
            return Error.NotFound("route.notFound", $"Route {routeId.Value} does not exist in this world.");
        var old = route.ChangeSurface(newSurface);
        if (old.IsError)
            return old.Errors;
        db.RouteUpgrades.Add(new RouteUpgrade(routeId, tick, old.Value, newSurface));

        var caravans = (await db.Caravans.Where(c => c.WorldId == world.Id && !c.Delivered).ToListAsync())
            .Where(c => c.Legs.Any(l => l.RouteId == routeId && l.ExitTick.Value > tick.Value))
            .OrderBy(c => c.Id.Value)
            .ToList();
        var vehicles = (await db.VehicleClasses.Where(v => v.WorldId == world.Id).ToListAsync())
            .ToDictionary(v => v.Name, StringComparer.Ordinal);
        var legRouteIds = caravans.SelectMany(c => c.Legs.Select(l => l.RouteId)).Distinct().ToList();
        var routes = await db.Routes.Where(r => legRouteIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id);
        routes[route.Id] = route;

        foreach (var c in caravans)
        {
            if (!vehicles.TryGetValue(c.VehicleClassName, out var vehicle))
                return Error.Validation("route.upgrade.vehicle", $"Caravan {c.Id.Value} uses unknown vehicle class '{c.VehicleClassName}'.");
            var legs = LegSchedule.Rebase(c.Legs, tick, (leg, metres) =>
            {
                var r = routes[leg.RouteId];
                return TravelTime.Ticks(metres, world.SurfaceSpeed(r.Surface), vehicle.SpeedFactor,
                    r.SpeedFactor(leg.Direction), r.Difficulty, Tick.DefaultMinutesPerDay);
            });
            var replaced = c.ReplaceLegs(legs);
            if (replaced.IsError) return replaced.Errors;
        }

        await log.EmitAsync(LogEventType.RouteUpgraded,
            $"Route {routeId.Value.ToString()[..8]} upgraded from {old.Value} to {newSurface}; {caravans.Count} caravans rescheduled",
            tick, LogScopeKind.World, world.Id.Value);
        return caravans.Count;
    }
}
