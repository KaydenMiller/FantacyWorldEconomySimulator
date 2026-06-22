using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Geography;
using WorldEcon.Domain.Logging;
using WorldEcon.Persistence;
using WorldEcon.SharedKernel;

namespace WorldEcon.Application.Logging;

/// <summary>Computes an on-demand <see cref="ScopeSummary"/> from the surviving log. A window older
/// than retention shows only what survived (documented behavior).</summary>
public sealed class SummaryService
{
    private readonly WorldDbContext _db;

    public SummaryService(WorldDbContext db) => _db = db;

    public async Task<ScopeSummary> SummarizeAsync(
        WorldId worldId, LogScopeKind scopeKind, Guid scopeId, Tick from, Tick to)
    {
        var scopeSeqs = (await _db.LogEventScopes
                .Where(x => x.WorldId == worldId && x.ScopeKind == scopeKind && x.ScopeId == scopeId)
                .Select(x => x.Sequence)
                .ToListAsync())
            .ToHashSet();

        // Tick range is value-converted → filter in memory.
        var events = (await _db.LogEvents
                .Where(e => e.WorldId == worldId && scopeSeqs.Contains(e.Sequence))
                .ToListAsync())
            .Where(e => e.OccurredTick.Value >= from.Value && e.OccurredTick.Value <= to.Value)
            .ToList();

        var countByType = events
            .GroupBy(e => e.Type)
            .ToDictionary(g => g.Key, g => g.Count());

        var notable = events
            .Where(e => (int)e.Magnitude >= (int)LogMagnitude.Major)
            .OrderByDescending(e => e.Sequence)
            .ToList();

        return new ScopeSummary(scopeKind, scopeId, from.Value, to.Value, events.Count, countByType, notable);
    }
}
