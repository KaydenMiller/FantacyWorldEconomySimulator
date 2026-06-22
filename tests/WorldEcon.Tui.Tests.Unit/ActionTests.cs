using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Economy;
using WorldEcon.Domain.Logging;
using WorldEcon.Tui.Actions;

namespace WorldEcon.Tui.Tests.Unit;

public class ActionTests
{
    [Test]
    public async Task AdvanceAction_AdvancesWorldClock()
    {
        var path = await TestWorld.SeedTempDbAsync();
        try
        {
            await using var ctx = TestWorld.NewContext(path);
            var tui = await TuiContext.LoadAsync(ctx);
            var startTick = tui.World.CurrentTick.Value;

            var ui = new FakeUserInteraction().EnqueueText("1440");
            await new AdvanceAction().ExecuteAsync(tui, ui);

            tui.World.CurrentTick.Value.Should().Be(startTick + 1440);
            ui.Messages.Should().ContainSingle();

            // Persisted: a fresh context sees the advanced clock too.
            await using var fresh = TestWorld.NewContext(path);
            (await fresh.Worlds.FirstAsync()).CurrentTick.Value.Should().Be(startTick + 1440);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task BuyOutAction_ReducesShopStock_AndLogsAction()
    {
        var path = await TestWorld.SeedTempDbAsync();
        try
        {
            string hammerfellKey;
            GoodId potionId;
            long stockBefore;

            await using (var ctx = TestWorld.NewContext(path))
            {
                var tui = await TuiContext.LoadAsync(ctx);

                var hammerfell = await ctx.Settlements.SingleAsync(s => s.Name == "Hammerfell");
                hammerfellKey = hammerfell.Id.Value.ToString();

                var potion = await ctx.Goods.SingleAsync(g => g.Name == "Health Potion");
                potionId = potion.Id;
                stockBefore = await ShopStockOf(ctx, potionId);
                stockBefore.Should().BeGreaterThan(0);

                var ui = new FakeUserInteraction()
                    .EnqueueText("Health Potion")
                    .EnqueueNumber(10);

                await new BuyOutAction().ExecuteAsync(hammerfellKey, tui, ui);
                ui.Messages.Should().ContainSingle();
            }

            await using (var fresh = TestWorld.NewContext(path))
            {
                (await ShopStockOf(fresh, potionId)).Should().Be(stockBefore - 10);
                (await fresh.LogEvents.CountAsync(e => e.IsPlayerAction)).Should().Be(1);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static async Task<long> ShopStockOf(Persistence.WorldDbContext db, GoodId goodId)
        => (await db.Stockpiles
                .Where(s => s.OwnerKind == StockpileOwnerKind.Shop && s.GoodId == goodId)
                .ToListAsync())
            .Sum(s => s.Quantity);
}
