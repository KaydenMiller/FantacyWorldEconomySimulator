using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Logging;
using WorldEcon.Persistence;

namespace WorldEcon.Persistence.Tests.Unit;

public class LogEventMigrationTests
{
    [Test]
    public async Task Migrate_CreatesLogTables_AndRoundTripsAnEvent()
    {
        var path = Path.Combine(Path.GetTempPath(), $"logmig-{Guid.NewGuid():N}.db");
        try
        {
            await using var db = new WorldDbContext(
                new DbContextOptionsBuilder<WorldDbContext>().UseSqlite($"Data Source={path}").Options);
            await db.Database.MigrateAsync();

            var worldId = new WorldEcon.Domain.Geography.WorldId(Guid.NewGuid());
            var ev = LogEvent.Create(worldId, 0, new WorldEcon.SharedKernel.Tick(5),
                LogEventType.Trade, LogMagnitude.Routine, LogScopeKind.Shop, Guid.NewGuid(),
                false, "{}", "round trip").Value;
            db.LogEvents.Add(ev);
            db.LogEventScopes.Add(LogEventScope.Create(worldId, ev.Id, LogScopeKind.Shop, ev.OriginId, 0).Value);
            await db.SaveChangesAsync();

            (await db.LogEvents.CountAsync()).Should().Be(1);
            (await db.LogEventScopes.CountAsync()).Should().Be(1);
        }
        finally { File.Delete(path); }
    }
}
