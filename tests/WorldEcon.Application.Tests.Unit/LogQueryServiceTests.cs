using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using WorldEcon.Application.Logging;
using WorldEcon.Domain.Geography;
using WorldEcon.Domain.Logging;
using WorldEcon.Persistence;
using WorldEcon.SharedKernel;

namespace WorldEcon.Application.Tests.Unit;

public class LogQueryServiceTests
{
    [Test]
    public async Task Query_ReturnsScopeEvents_NewestFirst_WithRegex()
    {
        var path = Path.Combine(Path.GetTempPath(), $"logq-{Guid.NewGuid():N}.db");
        try
        {
            await using var db = new WorldDbContext(
                new DbContextOptionsBuilder<WorldDbContext>().UseSqlite($"Data Source={path}").Options);
            await db.Database.MigrateAsync();

            var worldId = new WorldId(Guid.NewGuid());
            var shop = Guid.NewGuid();
            void Add(long seq, string msg)
            {
                var ev = LogEvent.Create(worldId, seq, new Tick(seq), LogEventType.Trade,
                    LogMagnitude.Routine, LogScopeKind.Shop, shop, false, "{}", msg).Value;
                db.LogEvents.Add(ev);
                db.LogEventScopes.Add(LogEventScope.Create(worldId, ev.Id, LogScopeKind.Shop, shop, seq).Value);
            }
            Add(0, "Sold 3 potions to Bob");
            Add(1, "Bought 5 iron from Alice");
            Add(2, "Sold 1 potion to Carol");
            await db.SaveChangesAsync();

            var svc = new LogQueryService(db);
            var all = await svc.QueryAsync(worldId, LogScopeKind.Shop, shop, regex: null, limit: 10);
            all.Select(e => e.Sequence).Should().Equal(2, 1, 0); // newest first

            var potions = await svc.QueryAsync(worldId, LogScopeKind.Shop, shop, regex: "potion", limit: 10);
            potions.Should().OnlyContain(e => e.Message.Contains("potion"));
            potions.Should().HaveCount(2);
        }
        finally { File.Delete(path); }
    }
}
