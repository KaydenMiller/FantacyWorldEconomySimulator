using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Logging;
using WorldEcon.Engine.Logging;
using WorldEcon.SharedKernel;

namespace WorldEcon.Engine.Tests.Unit;

public class LogEventEmitterTests
{
    [Test]
    public async Task Emit_RoutineTrade_WritesOnlyOriginScope()
    {
        var s = await LogTestWorld.CreateAsync();
        try
        {
            var emitter = new LogEventEmitter(s.Db, s.World.Id);
            await emitter.EmitAsync(LogEventType.Trade, "Sold 3 potions", new Tick(10),
                LogScopeKind.Shop, Guid.NewGuid(), s.Settlement.Id);
            await s.Db.SaveChangesAsync();

            (await s.Db.LogEvents.CountAsync()).Should().Be(1);
            // Routine clears no ancestor floor → exactly one scope row (the shop).
            (await s.Db.LogEventScopes.CountAsync()).Should().Be(1);
            (await s.Db.LogEventScopes.FirstAsync()).ScopeKind.Should().Be(LogScopeKind.Shop);
        }
        finally { await LogTestWorld.DisposeAsync(s); }
    }

    [Test]
    public async Task Emit_HistoricSettlementRuined_FansOutToContinentAndWorld()
    {
        var s = await LogTestWorld.CreateAsync();
        try
        {
            var emitter = new LogEventEmitter(s.Db, s.World.Id);
            await emitter.EmitAsync(LogEventType.SettlementRuined, "Hammerfell fell to ruin", new Tick(20),
                LogScopeKind.Settlement, s.Settlement.Id.Value, s.Settlement.Id);
            await s.Db.SaveChangesAsync();

            var kinds = (await s.Db.LogEventScopes.ToListAsync()).Select(x => x.ScopeKind).ToHashSet();
            kinds.Should().Contain(new[]
            {
                LogScopeKind.Settlement, LogScopeKind.Region, LogScopeKind.Country,
                LogScopeKind.Continent, LogScopeKind.World,
            });
        }
        finally { await LogTestWorld.DisposeAsync(s); }
    }

    [Test]
    public async Task Emit_AssignsMonotonicSequenceContinuingFromMax()
    {
        var s = await LogTestWorld.CreateAsync();
        try
        {
            var emitter = new LogEventEmitter(s.Db, s.World.Id);
            await emitter.EmitAsync(LogEventType.Trade, "a", new Tick(1), LogScopeKind.Shop, Guid.NewGuid(), s.Settlement.Id);
            await emitter.EmitAsync(LogEventType.Trade, "b", new Tick(2), LogScopeKind.Shop, Guid.NewGuid(), s.Settlement.Id);
            await s.Db.SaveChangesAsync();

            var seqs = (await s.Db.LogEvents.ToListAsync()).Select(e => e.Sequence).OrderBy(x => x).ToList();
            seqs.Should().Equal(0, 1);
        }
        finally { await LogTestWorld.DisposeAsync(s); }
    }
}
