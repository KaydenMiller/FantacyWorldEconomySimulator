using ErrorOr;
using WorldEcon.Domain.Geography;
using WorldEcon.SharedKernel.Domain;

namespace WorldEcon.Domain.Logging;

/// <summary>One row per level a <see cref="LogEvent"/> is visible at (origin + qualifying ancestors).
/// <see cref="Sequence"/> is denormalized from the event so the hot read can ORDER BY a plain long.</summary>
public sealed class LogEventScope : AggregateRoot<LogEventScopeId>
{
    public WorldId WorldId { get; }
    public LogEventId LogEventId { get; private set; }
    public LogScopeKind ScopeKind { get; private set; }
    public Guid ScopeId { get; private set; }   // raw id of the entity this event is visible to
    public long Sequence { get; private set; }  // == owning event's Sequence

    private LogEventScope() : base(default) { } // EF

    private LogEventScope(LogEventScopeId id, WorldId worldId, LogEventId logEventId,
        LogScopeKind scopeKind, Guid scopeId, long sequence) : base(id)
    {
        WorldId = worldId;
        LogEventId = logEventId;
        ScopeKind = scopeKind;
        ScopeId = scopeId;
        Sequence = sequence;
    }

    public static ErrorOr<LogEventScope> Create(WorldId worldId, LogEventId logEventId,
        LogScopeKind scopeKind, Guid scopeId, long sequence)
    {
        if (sequence < 0)
            return Error.Validation("logeventscope.sequence.negative", "Sequence must not be negative.");

        return new LogEventScope(LogEventScopeId.New(), worldId, logEventId, scopeKind, scopeId, sequence);
    }
}
