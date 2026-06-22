using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Geography;
using WorldEcon.Domain.Logging;
using WorldEcon.Persistence;
using WorldEcon.SharedKernel;

namespace WorldEcon.Engine.Logging;

/// <summary>The only writer of <see cref="LogEvent"/>s. Assigns a monotonic per-world sequence,
/// computes target scopes (origin + qualifying ancestors per <see cref="LogMagnitudePolicy"/>), and
/// adds the tracked rows. The caller saves (the engine once per advance; the party service immediately).</summary>
public sealed class LogEventEmitter
{
    private readonly WorldDbContext _db;
    private readonly WorldId _worldId;
    private readonly AncestorResolver _resolver;
    private long _nextSequence;
    private bool _seqLoaded;

    public LogEventEmitter(WorldDbContext db, WorldId worldId)
    {
        _db = db;
        _worldId = worldId;
        _resolver = new AncestorResolver(db, worldId);
    }

    public async Task<LogEvent> EmitAsync(LogEventType type, string message, Tick tick,
        LogScopeKind originKind, Guid originId, SettlementId? settlement = null,
        LogMagnitude? magnitude = null, bool isPlayerAction = false, string payloadJson = "{}")
    {
        var mag = magnitude ?? LogMagnitudePolicy.DefaultMagnitude(type);
        await EnsureSequenceLoaded();
        long seq = _nextSequence++;

        var ev = LogEvent.Create(_worldId, seq, tick, type, mag, originKind, originId,
            isPlayerAction, payloadJson, message).Value;
        _db.LogEvents.Add(ev);

        // Origin scope is always written.
        AddScope(ev, originKind, originId, seq);

        // Ancestor scopes that clear the magnitude floor (or are forced by a per-type override).
        if (settlement is { } sid)
        {
            foreach (var (akind, aid) in await _resolver.AncestorsOf(sid))
            {
                if (akind == originKind && aid == originId)
                    continue; // origin already added
                if (LogMagnitudePolicy.Visible(type, mag, akind))
                    AddScope(ev, akind, aid, seq);
            }
        }

        // World scope, when the event is visible at World and didn't originate there.
        if (originKind != LogScopeKind.World && LogMagnitudePolicy.Visible(type, mag, LogScopeKind.World))
            AddScope(ev, LogScopeKind.World, _worldId.Value, seq);

        return ev;
    }

    private void AddScope(LogEvent ev, LogScopeKind kind, Guid id, long seq)
        => _db.LogEventScopes.Add(LogEventScope.Create(_worldId, ev.Id, kind, id, seq).Value);

    private async Task EnsureSequenceLoaded()
    {
        if (_seqLoaded)
            return;
        long max = await _db.LogEvents
            .Where(e => e.WorldId == _worldId)
            .Select(e => (long?)e.Sequence)
            .MaxAsync() ?? -1;
        _nextSequence = max + 1;
        _seqLoaded = true;
    }
}
