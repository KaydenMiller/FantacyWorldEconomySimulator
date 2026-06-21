using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Economy;

namespace WorldEcon.Persistence.Repositories;

public sealed class StockpileRepository(WorldDbContext context) : IStockpileRepository
{
    public Task<Stockpile?> GetByOwnerAndGoodAsync(StockpileOwnerKind ownerKind, Guid ownerId, GoodId goodId)
        => context.Stockpiles.FirstOrDefaultAsync(
            s => s.OwnerKind == ownerKind && s.OwnerId == ownerId && s.GoodId == goodId);

    public async Task AddAsync(Stockpile stockpile) => await context.Stockpiles.AddAsync(stockpile);
}
