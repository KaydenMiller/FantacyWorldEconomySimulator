using ErrorOr;
using WorldEcon.Domain.Geography;
using WorldEcon.SharedKernel;
using WorldEcon.SharedKernel.Domain;

namespace WorldEcon.Domain.Logging;

/// <summary>An append-only, scoped record of something that happened in the world. Visibility at each
/// level is materialized separately as <see cref="LogEventScope"/> rows.</summary>
public sealed class LogEvent : AggregateRoot<LogEventId>
{
    public WorldId WorldId { get; }
    public long Sequence { get; private set; }            // monotonic per world; deterministic ordering
    public Tick OccurredTick { get; private set; }
    public LogEventType Type { get; private set; }
    public LogMagnitude Magnitude { get; private set; }
    public LogScopeKind OriginKind { get; private set; }
    public Guid OriginId { get; private set; }            // raw id of the originating entity
    public bool IsPlayerAction { get; private set; }      // DM/party origin; never pruned
    public string PayloadJson { get; private set; }       // structured details (who/qty/price/...)
    public string Message { get; private set; }           // human-readable
    public DateTimeOffset RecordedAtUtc { get; private set; }

    private LogEvent() : base(default) { PayloadJson = null!; Message = null!; } // EF

    private LogEvent(LogEventId id, WorldId worldId, long sequence, Tick occurredTick, LogEventType type,
        LogMagnitude magnitude, LogScopeKind originKind, Guid originId, bool isPlayerAction,
        string payloadJson, string message, DateTimeOffset recordedAtUtc) : base(id)
    {
        WorldId = worldId;
        Sequence = sequence;
        OccurredTick = occurredTick;
        Type = type;
        Magnitude = magnitude;
        OriginKind = originKind;
        OriginId = originId;
        IsPlayerAction = isPlayerAction;
        PayloadJson = payloadJson;
        Message = message;
        RecordedAtUtc = recordedAtUtc;
    }

    public static ErrorOr<LogEvent> Create(WorldId worldId, long sequence, Tick occurredTick,
        LogEventType type, LogMagnitude magnitude, LogScopeKind originKind, Guid originId,
        bool isPlayerAction, string payloadJson, string message)
        => Create(worldId, sequence, occurredTick, type, magnitude, originKind, originId,
            isPlayerAction, payloadJson, message, DateTimeOffset.UtcNow);

    public static ErrorOr<LogEvent> Create(WorldId worldId, long sequence, Tick occurredTick,
        LogEventType type, LogMagnitude magnitude, LogScopeKind originKind, Guid originId,
        bool isPlayerAction, string payloadJson, string message, DateTimeOffset recordedAtUtc)
    {
        if (sequence < 0)
            return Error.Validation("logevent.sequence.negative", "Sequence must not be negative.");
        if (payloadJson is null)
            return Error.Validation("logevent.payload.null", "Payload JSON must not be null.");
        if (string.IsNullOrWhiteSpace(message))
            return Error.Validation("logevent.message.blank", "Message must not be blank.");

        return new LogEvent(LogEventId.New(), worldId, sequence, occurredTick, type, magnitude,
            originKind, originId, isPlayerAction, payloadJson, message.Trim(), recordedAtUtc);
    }
}
