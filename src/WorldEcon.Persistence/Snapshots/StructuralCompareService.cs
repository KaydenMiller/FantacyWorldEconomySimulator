using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Geography;

namespace WorldEcon.Persistence.Snapshots;

/// <summary>In-C# structural diff over settlements (portable; sqldiff/changeset generalization deferred).</summary>
public sealed class StructuralCompareService : ICompareService
{
    public async Task<WorldDiff> CompareAsync(string baselineDbPath, string candidateDbPath, WorldId worldId)
    {
        var baseline = await LoadSettlementsAsync(baselineDbPath, worldId);
        var candidate = await LoadSettlementsAsync(candidateDbPath, worldId);

        var added = candidate.Keys.Except(baseline.Keys)
            .Select(id => candidate[id].Name).OrderBy(n => n).ToList();
        var removed = baseline.Keys.Except(candidate.Keys)
            .Select(id => baseline[id].Name).OrderBy(n => n).ToList();
        var changed = candidate.Keys.Intersect(baseline.Keys)
            .Where(id => !SameContent(baseline[id], candidate[id]))
            .Select(id => candidate[id].Name).OrderBy(n => n).ToList();

        return new WorldDiff(added, removed, changed);
    }

    private static async Task<Dictionary<SettlementId, Settlement>> LoadSettlementsAsync(string path, WorldId worldId)
    {
        await using var ctx = new WorldDbContext(
            new DbContextOptionsBuilder<WorldDbContext>().UseSqlite($"Data Source={path}").Options);
        var list = await ctx.Settlements.Where(s => s.WorldId == worldId).ToListAsync();
        return list.ToDictionary(s => s.Id);
    }

    private static bool SameContent(Settlement a, Settlement b)
        => a.Name == b.Name && a.Type == b.Type && a.X == b.X && a.Y == b.Y
           && a.Population == b.Population && a.RegionId.Equals(b.RegionId);
}
