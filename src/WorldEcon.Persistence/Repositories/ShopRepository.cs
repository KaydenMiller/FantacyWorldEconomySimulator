using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Economy;
using WorldEcon.Domain.Geography;

namespace WorldEcon.Persistence.Repositories;

public sealed class ShopRepository(WorldDbContext context) : IShopRepository
{
    public Task<Shop?> GetAsync(ShopId id) => context.Shops.FirstOrDefaultAsync(s => s.Id == id);

    public async Task<IReadOnlyList<Shop>> ListBySettlementAsync(SettlementId settlementId)
    {
        var list = await context.Shops.Where(s => s.SettlementId == settlementId).ToListAsync();
        return list.OrderBy(s => s.Id.Value).ToList();
    }

    public async Task AddAsync(Shop shop) => await context.Shops.AddAsync(shop);
}
