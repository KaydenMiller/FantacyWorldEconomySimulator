using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Logging;
using WorldEcon.Tui;
using WorldEcon.Tui.Navigation;

namespace WorldEcon.Tui.Tests.Unit;

public class LogNavTests
{
    [Test]
    public async Task LogViewForScope_ListsEventsNewestFirst()
    {
        var path = await TestWorld.SeedTempDbAsync();
        try
        {
            await using var ctx = TestWorld.NewContext(path);
            var tui = await TuiContext.LoadAsync(ctx);

            var hammerfell = await ctx.Settlements.SingleAsync(s => s.Name == "Hammerfell");
            void Add(long seq, string msg)
            {
                var ev = LogEvent.Create(tui.World.Id, seq, new WorldEcon.SharedKernel.Tick(seq),
                    LogEventType.Trade, LogMagnitude.Routine, LogScopeKind.Settlement,
                    hammerfell.Id.Value, false, "{}", msg).Value;
                ctx.LogEvents.Add(ev);
                ctx.LogEventScopes.Add(LogEventScope.Create(tui.World.Id, ev.Id,
                    LogScopeKind.Settlement, hammerfell.Id.Value, seq).Value);
            }
            Add(0, "first"); Add(1, "second");
            await ctx.SaveChangesAsync();

            var nav = new Navigator();
            var view = await nav.LogViewForScopeAsync(LogScopeKind.Settlement, hammerfell.Id.Value, "Hammerfell", null, tui);

            view.Rows.Should().HaveCount(2);
            view.Rows[0].Cells.Last().Should().Be("second"); // newest first
        }
        finally { File.Delete(path); }
    }
}
