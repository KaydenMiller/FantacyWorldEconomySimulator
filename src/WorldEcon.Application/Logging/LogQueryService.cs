using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Geography;
using WorldEcon.Domain.Logging;
using WorldEcon.Persistence;

namespace WorldEcon.Application.Logging;

/// <summary>Reads the log visible at a scope, newest first. The scope read is a single indexed lookup
/// on (ScopeKind, ScopeId) ordered by the denormalized long Sequence; regex filtering (v1) is applied
/// in memory on the fetched page.</summary>
public sealed class LogQueryService
{
    private readonly WorldDbContext _db;

    public LogQueryService(WorldDbContext db) => _db = db;

    public async Task<IReadOnlyList<LogEvent>> QueryAsync(
        WorldId worldId, LogScopeKind scopeKind, Guid scopeId, string? regex, int limit)
    {
        // Pull more than `limit` when a regex will thin the page, so we still return up to `limit` matches.
        int fetch = regex is null ? limit : Math.Max(limit * 8, limit);

        var scopeRows = await _db.LogEventScopes
            .Where(x => x.WorldId == worldId && x.ScopeKind == scopeKind && x.ScopeId == scopeId)
            .OrderByDescending(x => x.Sequence)
            .Take(fetch)
            .ToListAsync();

        if (scopeRows.Count == 0)
            return [];

        var seqs = scopeRows.Select(x => x.Sequence).ToHashSet();
        var events = (await _db.LogEvents
                .Where(e => e.WorldId == worldId && seqs.Contains(e.Sequence))
                .ToListAsync())
            .OrderByDescending(e => e.Sequence)
            .AsEnumerable();

        if (regex is not null)
        {
            var rx = new Regex(regex, RegexOptions.IgnoreCase);
            events = events.Where(e => rx.IsMatch(e.Message));
        }

        return events.Take(limit).ToList();
    }
}
