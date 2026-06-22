using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Logging;
using WorldEcon.Engine.Logging;
using WorldEcon.SharedKernel;

namespace WorldEcon.Engine.Tests.Unit;

public class LogRetentionTests
{
    [Test]
    public async Task Prune_DropsOldRoutine_KeepsMajorAndPlayer()
    {
        var s = await LogTestWorld.CreateAsync();
        try
        {
            var origin = Guid.NewGuid();
            var emitter = new LogEventEmitter(s.Db, s.World.Id);
            await emitter.EmitAsync(LogEventType.Trade, "old routine", new Tick(0),
                LogScopeKind.Shop, origin, s.Settlement.Id);                                   // Routine
            await emitter.EmitAsync(LogEventType.SettlementRuined, "historic", new Tick(0),
                LogScopeKind.Settlement, s.Settlement.Id.Value, s.Settlement.Id);             // Historic (never)
            await emitter.EmitAsync(LogEventType.PartyAction, "player", new Tick(0),
                LogScopeKind.Settlement, s.Settlement.Id.Value, s.Settlement.Id,
                magnitude: LogMagnitude.Routine, isPlayerAction: true);                        // player → never
            await s.Db.SaveChangesAsync();

            // Now well past the Routine age (90 in-world days).
            long now = 200 * Tick.DefaultMinutesPerDay;
            await LogRetention.PruneAsync(s.Db, s.World.Id, new Tick(now));
            await s.Db.SaveChangesAsync();

            var types = (await s.Db.LogEvents.ToListAsync()).Select(e => e.Type).ToHashSet();
            types.Should().NotContain(LogEventType.Trade);          // pruned
            types.Should().Contain(LogEventType.SettlementRuined);  // historic kept
            types.Should().Contain(LogEventType.PartyAction);       // player kept
            // Scope rows for the pruned event are gone too.
            (await s.Db.LogEventScopes.CountAsync()).Should().Be(
                await s.Db.LogEventScopes.CountAsync(x => x.ScopeId != origin || x.ScopeKind != LogScopeKind.Shop));
        }
        finally { await LogTestWorld.DisposeAsync(s); }
    }

    [Test]
    public async Task Prune_IsGranularityIndependent()
    {
        async Task<int> SurvivorsAfter(int chunks, long perChunk)
        {
            var s = await LogTestWorld.CreateAsync();
            try
            {
                var sim = await SimulationContext.LoadAsync(s.Db, s.World.Id);
                var engine = new TickEngine(StandardPhases.All());
                for (int i = 0; i < chunks; i++)
                    await engine.AdvanceAsync(sim, perChunk);
                return await s.Db.LogEvents.CountAsync();
            }
            finally { await LogTestWorld.DisposeAsync(s); }
        }

        long oneYear = 360 * Tick.DefaultMinutesPerDay;
        int single = await SurvivorsAfter(1, oneYear);
        int split = await SurvivorsAfter(2, oneYear / 2);
        split.Should().Be(single);
    }
}
