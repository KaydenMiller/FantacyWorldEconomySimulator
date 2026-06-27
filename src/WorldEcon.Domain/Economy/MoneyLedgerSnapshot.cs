using ErrorOr;
using WorldEcon.Domain.Geography;
using WorldEcon.SharedKernel;
using WorldEcon.SharedKernel.Domain;

namespace WorldEcon.Domain.Economy;

/// <summary>
/// A point-in-time record of the world's money supply and the currency that flowed (per channel)
/// since the previous snapshot. Written monthly and at the end of each advance. Per-channel detail
/// lives in <see cref="MoneyLedgerLine"/> rows linked by <see cref="MoneyLedgerSnapshotId"/>
/// (parent/lines, like <c>LogEvent</c>/<c>LogEventScope</c>).
/// </summary>
public sealed class MoneyLedgerSnapshot : AggregateRoot<MoneyLedgerSnapshotId>
{
    public WorldId WorldId { get; }
    public long Sequence { get; private set; }      // monotonic per world; deterministic ordering
    public Tick Tick { get; private set; }          // when the snapshot was taken
    public Money TotalSupply { get; private set; }  // derived: Σ consumer budgets + shop tills + merchant capital
    public Money NetDelta { get; private set; }     // faucets − sinks for the period
    public Money Discrepancy { get; private set; }  // (TotalSupply − prevTotalSupply) − NetDelta; 0 = conserved

    private MoneyLedgerSnapshot() : base(default) { } // EF

    private MoneyLedgerSnapshot(MoneyLedgerSnapshotId id, WorldId worldId, long sequence, Tick tick,
        Money totalSupply, Money netDelta, Money discrepancy) : base(id)
    {
        WorldId = worldId;
        Sequence = sequence;
        Tick = tick;
        TotalSupply = totalSupply;
        NetDelta = netDelta;
        Discrepancy = discrepancy;
    }

    public static ErrorOr<MoneyLedgerSnapshot> Create(WorldId worldId, long sequence, Tick tick,
        Money totalSupply, Money netDelta, Money discrepancy)
    {
        if (sequence < 0)
            return Error.Validation("moneyledger.sequence.negative", "Sequence must not be negative.");
        return new MoneyLedgerSnapshot(MoneyLedgerSnapshotId.New(), worldId, sequence, tick,
            totalSupply, netDelta, discrepancy);
    }
}
