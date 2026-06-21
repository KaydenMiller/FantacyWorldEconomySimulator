using ErrorOr;
using WorldEcon.Domain.Geography;
using WorldEcon.SharedKernel;
using WorldEcon.SharedKernel.Domain;

namespace WorldEcon.Domain.Actions;

/// <summary>An append-only audit record of a party/DM effect applied to a world.</summary>
public sealed class DmAction : AggregateRoot<DmActionId>
{
    public WorldId WorldId { get; }
    public long Sequence { get; private set; }          // monotonic per world; assigned by the caller
    public Tick AppliedTick { get; private set; }       // in-world tick when applied
    public DmActionKind Kind { get; private set; }
    public string ArgsJson { get; private set; }        // free-form JSON payload the service interprets
    public string Description { get; private set; }     // human-readable
    public DateTimeOffset RecordedAtUtc { get; private set; } // real-world timestamp, supplied by caller

    private DmAction() : base(default) { ArgsJson = null!; Description = null!; } // EF

    private DmAction(DmActionId id, WorldId worldId, long sequence, Tick appliedTick,
        DmActionKind kind, string argsJson, string description, DateTimeOffset recordedAtUtc) : base(id)
    {
        WorldId = worldId;
        Sequence = sequence;
        AppliedTick = appliedTick;
        Kind = kind;
        ArgsJson = argsJson;
        Description = description;
        RecordedAtUtc = recordedAtUtc;
    }

    public static ErrorOr<DmAction> Create(WorldId worldId, long sequence, Tick appliedTick,
        DmActionKind kind, string argsJson, string description, DateTimeOffset recordedAtUtc)
    {
        if (sequence < 0)
            return Error.Validation("dmaction.sequence.negative", "Sequence must not be negative.");
        if (argsJson is null)
            return Error.Validation("dmaction.argsjson.null", "Args JSON must not be null.");
        if (string.IsNullOrWhiteSpace(description))
            return Error.Validation("dmaction.description.blank", "Description must not be blank.");

        return new DmAction(DmActionId.New(), worldId, sequence, appliedTick, kind, argsJson, description, recordedAtUtc);
    }
}
