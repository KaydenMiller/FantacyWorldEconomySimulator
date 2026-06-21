using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Geography;

namespace WorldEcon.Persistence.Repositories;

public sealed class SettlementRepository(WorldDbContext context) : ISettlementRepository
{
    public Task<Settlement?> GetAsync(SettlementId id)
        => context.Settlements.FirstOrDefaultAsync(s => s.Id == id);

    public async Task<IReadOnlyList<Settlement>> ListByWorldAsync(WorldId worldId)
    {
        var list = await context.Settlements.Where(s => s.WorldId == worldId).ToListAsync();
        // Deterministic, stable in-memory order; value-converted typed-ID members don't reliably
        // translate to SQL ORDER BY, and the set is fully loaded, so this is fully reproducible.
        return list.OrderBy(s => s.Id.Value).ToList();
    }

    public async Task AddAsync(Settlement settlement) => await context.Settlements.AddAsync(settlement);
}
