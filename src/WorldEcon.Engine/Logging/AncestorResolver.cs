using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Geography;
using WorldEcon.Domain.Logging;
using WorldEcon.Persistence;

namespace WorldEcon.Engine.Logging;

/// <summary>Resolves a settlement's scope chain (Settlement → Region → Country? → Continent(s)) for
/// the emitter, cached per instance (one instance per advance / per service call).</summary>
public sealed class AncestorResolver
{
    private readonly WorldDbContext _db;
    private readonly WorldId _worldId;
    private readonly Dictionary<Guid, IReadOnlyList<(LogScopeKind Kind, Guid Id)>> _cache = new();

    public AncestorResolver(WorldDbContext db, WorldId worldId)
    {
        _db = db;
        _worldId = worldId;
    }

    public async Task<IReadOnlyList<(LogScopeKind Kind, Guid Id)>> AncestorsOf(SettlementId settlementId)
    {
        if (_cache.TryGetValue(settlementId.Value, out var cached))
            return cached;

        var chain = new List<(LogScopeKind, Guid)> { (LogScopeKind.Settlement, settlementId.Value) };

        var settlement = await _db.Settlements.FirstOrDefaultAsync(x => x.Id == settlementId);
        if (settlement is not null)
        {
            var region = await _db.Regions.FirstOrDefaultAsync(r => r.Id == settlement.RegionId);
            if (region is not null)
            {
                chain.Add((LogScopeKind.Region, region.Id.Value));
                if (region.CountryId is { } cid)
                    chain.Add((LogScopeKind.Country, cid.Value));

                // A region may span several continents (geography v2 m2m).
                var continentIds = (await _db.RegionContinents
                        .Where(rc => rc.WorldId == _worldId && rc.RegionId == region.Id)
                        .ToListAsync())
                    .Select(rc => rc.ContinentId.Value)
                    .OrderBy(g => g);
                foreach (var contId in continentIds)
                    chain.Add((LogScopeKind.Continent, contId));
            }
        }

        _cache[settlementId.Value] = chain;
        return chain;
    }
}
