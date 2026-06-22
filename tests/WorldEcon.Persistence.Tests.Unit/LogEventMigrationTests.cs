using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
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

    [Test]
    public async Task Migrate_CopiesLegacyDmActionsIntoLogEvents()
    {
        var path = Path.Combine(Path.GetTempPath(), $"logmigcopy-{Guid.NewGuid():N}.db");
        try
        {
            await using var db = new WorldDbContext(
                new DbContextOptionsBuilder<WorldDbContext>().UseSqlite($"Data Source={path}").Options);

            // Step up to GeographyV2 so dm_actions still exists.
            var migrator = db.GetInfrastructure().GetRequiredService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>();
            await migrator.MigrateAsync("20260622010208_GeographyV2");

            // Insert one legacy dm_actions row.
            var id = Guid.NewGuid();
            var worldId = Guid.NewGuid();
            var recordedAt = DateTimeOffset.UtcNow.ToString("O");
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO dm_actions (Id, WorldId, Sequence, AppliedTick, Kind, ArgsJson, Description, RecordedAtUtc) " +
                "VALUES ({0}, {1}, {2}, {3}, {4}, {5}, {6}, {7})",
                id.ToString(), worldId.ToString(), 0L, 5L, "BuyFromShops", "{}", "Party bought 3 potions", recordedAt);

            // Run the AddLogEvents migration.
            await migrator.MigrateAsync("20260622051136_AddLogEvents");

            // Verify the log_event was created with correct field values.
            var logEvent = await db.LogEvents.SingleAsync();
            logEvent.IsPlayerAction.Should().BeTrue();
            logEvent.Type.Should().Be(LogEventType.PartyAction);
            logEvent.Magnitude.Should().Be(LogMagnitude.Major);
            logEvent.Message.Should().Be("Party bought 3 potions");
            logEvent.Sequence.Should().Be(0);

            // Verify the log_event_scope was created at World scope.
            var scope = await db.LogEventScopes.SingleAsync();
            scope.ScopeKind.Should().Be(LogScopeKind.World);
        }
        finally { File.Delete(path); }
    }
}
