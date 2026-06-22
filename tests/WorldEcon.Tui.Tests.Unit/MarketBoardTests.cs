using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using WorldEcon.Tui;
using WorldEcon.Tui.Navigation;

namespace WorldEcon.Tui.Tests.Unit;

public class MarketBoardTests
{
    [Test]
    public async Task MarketBoard_ListsEachShopOffer_WithMinPriceAndPrice()
    {
        var path = await TestWorld.SeedTempDbAsync();
        try
        {
            await using var ctx = TestWorld.NewContext(path);
            var tui = await TuiContext.LoadAsync(ctx);
            var hammerfell = await ctx.Settlements.SingleAsync(s => s.Name == "Hammerfell");

            var nav = new Navigator();
            var view = await nav.MarketBoardAsync(hammerfell.Id, tui);

            view.Columns.Should().ContainInOrder("Good", "Category", "Shop", "Qty", "Min Price", "Price");
            // One row per shop offer (Hammerfell seed has shops with potions/bread).
            view.Rows.Should().NotBeEmpty();
        }
        finally { File.Delete(path); }
    }
}
