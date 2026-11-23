using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Geography;

namespace WorldEcon.Persistence.Repositories;

public sealed class RouteRepository(WorldDbContext context) : IRouteRepository
{
    public async Task<IReadOnlyList<Route>> ListByWorldAsync(WorldId worldId)
    {
        var list = await context.Routes.Where(r => r.WorldId == worldId).ToListAsync();
        return list.OrderBy(r => r.Id.Value).ToList();
    }

    public async Task AddRangeAsync(IEnumerable<RouteNode> nodes, IEnumerable<Route> routes, IEnumerable<RouteTile> tiles, IEnumerable<Road> roads)
    {
        await context.RouteNodes.AddRangeAsync(nodes);
        await context.Routes.AddRangeAsync(routes);
        await context.RouteTiles.AddRangeAsync(tiles);
        await context.Roads.AddRangeAsync(roads);
    }

    public async Task<IReadOnlyList<RouteId>> RoutesOnTileAsync(WorldId worldId, int tileId)
    {
        var ids = await (from t in context.RouteTiles
                         join r in context.Routes on t.RouteId equals r.Id
                         where t.TileId == tileId && r.WorldId == worldId
                         select t.RouteId).Distinct().ToListAsync();
        return ids.OrderBy(i => i.Value).ToList();
    }
}
