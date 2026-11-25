using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Economy;
using WorldEcon.Engine;
using WorldEcon.SharedKernel;
using WorldEcon.Tui.Actions;

namespace WorldEcon.Tui.Tests.Unit;

public class AdvanceActionTests
{
    [Test]
    public async Task FailedAdvance_ReportsAndLeavesWorldUnchanged()
    {
        var path = await TestWorld.SeedTempDbAsync();
        try
        {
            Guid shopId;
            long tillBefore;
            await using (var ctx = TestWorld.NewContext(path))
            {
                var tui = await TuiContext.LoadAsync(ctx);
                var startTick = tui.World.CurrentTick.Value;
                var shop = await ctx.Shops.OrderBy(s => s.Name).FirstAsync();
                shopId = shop.Id.Value;
                tillBefore = shop.Till.Units;

                // Day 1: credit the till; day 2: fail.
                var engine = new TickEngine([new TillPhase(shopId), new FailingPhase(startTick + 2 * Tick.DefaultMinutesPerDay)]);
                var ui = new FakeUserInteraction().EnqueueText("3d");

                await new AdvanceAction(() => engine).ExecuteAsync(tui, ui);

                ui.Messages.Should().ContainSingle();
                ui.Messages[0].Title.Should().Be("Advance failed");
                ui.Messages[0].Lines.Should().Equal(FailingPhase.Message, "Nothing was saved.");
                tui.World.CurrentTick.Value.Should().Be(startTick);

                // A later save of the shared context writes nothing from the abandoned run.
                await ctx.SaveChangesAsync();
            }

            await using var fresh = TestWorld.NewContext(path);
            var sid = new ShopId(shopId);
            (await fresh.Shops.SingleAsync(s => s.Id == sid)).Till.Units.Should().Be(tillBefore);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }

    private sealed class TillPhase(Guid shopId) : ISimulationPhase
    {
        public string Name => "till";
        public int Order => 0;
        public long CadenceTicks => Tick.DefaultMinutesPerDay;

        public async Task ExecuteAsync(SimulationContext ctx, Tick tick)
        {
            var sid = new ShopId(shopId);
            var shop = await ctx.Db.Shops.SingleAsync(s => s.Id == sid);
            shop.CreditTill(new Money(1_000));
        }
    }

    private sealed class FailingPhase(long failAt) : ISimulationPhase
    {
        public const string Message = "Phase broke on purpose.";
        public string Name => "failing";
        public int Order => 1;
        public long CadenceTicks => Tick.DefaultMinutesPerDay;

        public Task ExecuteAsync(SimulationContext ctx, Tick tick)
            => tick.Value >= failAt ? throw new InvalidOperationException(Message) : Task.CompletedTask;
    }
}
