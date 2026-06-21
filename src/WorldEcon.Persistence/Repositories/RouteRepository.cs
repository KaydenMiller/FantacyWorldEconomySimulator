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

    public async Task AddAsync(Route route) => await context.Routes.AddAsync(route);
}
