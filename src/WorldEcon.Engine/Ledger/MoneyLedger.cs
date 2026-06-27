using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Economy;
using WorldEcon.Domain.Geography;
using WorldEcon.Persistence;
using WorldEcon.SharedKernel;

namespace WorldEcon.Engine.Ledger;

/// <summary>
/// Records every currency flow by channel and writes periodic <see cref="MoneyLedgerSnapshot"/>s
/// (total supply + per-channel faucet/sink/transfer totals + a conservation discrepancy). Sibling to
/// the log emitter on <see cref="SimulationContext"/>. In-memory accumulation survives the engine's
/// <c>ChangeTracker.Clear()</c> (it isn't tracked), exactly like the log emitter's sequence counter.
/// </summary>
public sealed class MoneyLedger
{
    private readonly WorldDbContext _db;
    private readonly WorldId _worldId;
    private readonly Dictionary<MoneyChannel, long> _period = new();
    private long? _lastSupply;
    private long _lastSnapshotTick = -1;
    private long _nextSequence;
    private bool _seqLoaded;

    public MoneyLedger(WorldDbContext db, WorldId worldId)
    {
        _db = db;
        _worldId = worldId;
    }

    /// <summary>Records a currency flow on a channel. Zero/negative is a no-op.</summary>
    public void Record(MoneyChannel channel, long amount)
    {
        if (amount <= 0) return;
        _period[channel] = _period.GetValueOrDefault(channel) + amount;
    }

    /// <summary>
    /// Writes a snapshot at <paramref name="tick"/>: the current derived total money supply, the
    /// period's per-channel totals, the net delta (faucets − sinks), and the conservation discrepancy
    /// (<c>Δsupply − netDelta</c>; 0 means conserved). Resets the period accumulator. No-op if a
    /// snapshot was already taken at this exact tick.
    /// </summary>
    public async Task SnapshotAsync(Tick tick)
    {
        if (tick.Value == _lastSnapshotTick)
            return;

        long totalSupply = await TotalSupplyAsync();

        long faucets = 0, sinks = 0;
        foreach (var (channel, amount) in _period)
        {
            switch (MoneyChannels.KindOf(channel))
            {
                case MoneyFlowKind.Faucet: faucets += amount; break;
                case MoneyFlowKind.Sink: sinks += amount; break;
            }
        }
        long netDelta = faucets - sinks;
        long discrepancy = _lastSupply is long prev ? (totalSupply - prev) - netDelta : 0;

        await EnsureSequenceLoaded();
        var snapshot = MoneyLedgerSnapshot.Create(_worldId, _nextSequence++, tick,
            new Money(totalSupply), new Money(netDelta), new Money(discrepancy)).Value;
        _db.MoneyLedgerSnapshots.Add(snapshot);

        foreach (var (channel, amount) in _period.OrderBy(kv => (int)kv.Key))
        {
            if (amount == 0) continue;
            _db.MoneyLedgerLines.Add(MoneyLedgerLine.Create(_worldId, snapshot.Id, channel, new Money(amount)).Value);
        }

        _lastSupply = totalSupply;
        _lastSnapshotTick = tick.Value;
        _period.Clear();
    }

    /// <summary>Derived total money supply = Σ consumer budgets + shop tills + merchant capital,
    /// with tracked (unsaved) instances overlaid on the DB rows so within-advance changes count.</summary>
    private async Task<long> TotalSupplyAsync()
    {
        var wid = _worldId;
        long consumers = MergedSum(
            await _db.Consumers.Where(c => c.WorldId == wid).ToListAsync(),
            _db.Consumers.Local.Where(c => c.WorldId == wid),
            c => c.Id.Value, c => c.Budget.Units);
        long shops = MergedSum(
            await _db.Shops.Where(s => s.WorldId == wid).ToListAsync(),
            _db.Shops.Local.Where(s => s.WorldId == wid),
            s => s.Id.Value, s => s.Till.Units);
        long merchants = MergedSum(
            await _db.Merchants.Where(m => m.WorldId == wid).ToListAsync(),
            _db.Merchants.Local.Where(m => m.WorldId == wid),
            m => m.Id.Value, m => m.Capital.Units);
        return consumers + shops + merchants;
    }

    private static long MergedSum<T>(List<T> dbRows, IEnumerable<T> localRows, Func<T, Guid> id, Func<T, long> val)
    {
        var byId = dbRows.ToDictionary(id);
        foreach (var local in localRows)
            byId[id(local)] = local;
        return byId.Values.Sum(val);
    }

    private async Task EnsureSequenceLoaded()
    {
        if (_seqLoaded)
            return;
        long max = await _db.MoneyLedgerSnapshots
            .Where(s => s.WorldId == _worldId)
            .Select(s => (long?)s.Sequence)
            .MaxAsync() ?? -1;
        _nextSequence = max + 1;
        _seqLoaded = true;
    }
}
