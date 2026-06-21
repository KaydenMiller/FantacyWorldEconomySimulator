using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Geography;

namespace WorldEcon.Persistence.Repositories;

public sealed class WorldRepository(WorldDbContext context) : IWorldRepository
{
    public Task<World?> GetAsync(WorldId id) => context.Worlds.FirstOrDefaultAsync(w => w.Id == id);
    public async Task AddAsync(World world) => await context.Worlds.AddAsync(world);
}
