using ErrorOr;
using WorldEcon.Domain.Geography;
using WorldEcon.SharedKernel;
using WorldEcon.SharedKernel.Domain;

namespace WorldEcon.Domain.Economy;

/// <summary>One channel's currency total for a single <see cref="MoneyLedgerSnapshot"/> period.
/// Linked to its snapshot by id (no navigation), mirroring <c>LogEventScope</c>.</summary>
public sealed class MoneyLedgerLine : AggregateRoot<MoneyLedgerLineId>
{
    public WorldId WorldId { get; }
    public MoneyLedgerSnapshotId SnapshotId { get; private set; }
    public MoneyChannel Channel { get; private set; }
    public MoneyFlowKind Kind { get; private set; }
    public Money Amount { get; private set; }

    private MoneyLedgerLine() : base(default) { } // EF

    private MoneyLedgerLine(MoneyLedgerLineId id, WorldId worldId, MoneyLedgerSnapshotId snapshotId,
        MoneyChannel channel, MoneyFlowKind kind, Money amount) : base(id)
    {
        WorldId = worldId;
        SnapshotId = snapshotId;
        Channel = channel;
        Kind = kind;
        Amount = amount;
    }

    public static ErrorOr<MoneyLedgerLine> Create(WorldId worldId, MoneyLedgerSnapshotId snapshotId,
        MoneyChannel channel, Money amount)
        => new MoneyLedgerLine(MoneyLedgerLineId.New(), worldId, snapshotId, channel,
            MoneyChannels.KindOf(channel), amount);
}
