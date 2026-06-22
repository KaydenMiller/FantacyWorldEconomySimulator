using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using WorldEcon.Application.Logging;
using WorldEcon.Domain.Geography;
using WorldEcon.Domain.Logging;
using WorldEcon.Persistence;
using WorldEcon.SharedKernel;

namespace WorldEcon.Application.Tests.Unit;

public class SummaryServiceTests
{
    [Test]
    public async Task Summarize_CountsByTypeInWindow()
    {
        var path = Path.Combine(Path.GetTempPath(), $"logsum-{Guid.NewGuid():N}.db");
        try
        {
            await using var db = new WorldDbContext(
                new DbContextOptionsBuilder<WorldDbContext>().UseSqlite($"Data Source={path}").Options);
            await db.Database.MigrateAsync();

            var worldId = new WorldId(Guid.NewGuid());
            var settle = Guid.NewGuid();
            void Add(long seq, LogEventType type, long tick)
            {
                var ev = LogEvent.Create(worldId, seq, new Tick(tick), type, LogMagnitude.Notable,
                    LogScopeKind.Settlement, settle, false, "{}", $"{type} @ {tick}").Value;
                db.LogEvents.Add(ev);
                db.LogEventScopes.Add(LogEventScope.Create(worldId, ev.Id, LogScopeKind.Settlement, settle, seq).Value);
            }
            Add(0, LogEventType.Stockout, 10);
            Add(1, LogEventType.Stockout, 20);
            Add(2, LogEventType.ProductionChanged, 30);
            Add(3, LogEventType.Stockout, 5000); // outside window
            await db.SaveChangesAsync();

            var svc = new SummaryService(db);
            var summary = await svc.SummarizeAsync(worldId, LogScopeKind.Settlement, settle,
                from: new Tick(0), to: new Tick(100));

            summary.TotalEvents.Should().Be(3);
            summary.CountByType[LogEventType.Stockout].Should().Be(2);
            summary.CountByType[LogEventType.ProductionChanged].Should().Be(1);
        }
        finally { File.Delete(path); }
    }
}
