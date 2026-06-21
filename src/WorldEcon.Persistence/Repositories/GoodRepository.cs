using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Economy;
using WorldEcon.Domain.Geography;

namespace WorldEcon.Persistence.Repositories;

public sealed class GoodRepository(WorldDbContext context) : IGoodRepository
{
    public Task<Good?> GetAsync(GoodId id) => context.Goods.FirstOrDefaultAsync(g => g.Id == id);

    public async Task<IReadOnlyList<Good>> ListByWorldAsync(WorldId worldId)
    {
        var list = await context.Goods.Where(g => g.WorldId == worldId).ToListAsync();
        return list.OrderBy(g => g.Id.Value).ToList();
    }

    public async Task AddAsync(Good good) => await context.Goods.AddAsync(good);
}
