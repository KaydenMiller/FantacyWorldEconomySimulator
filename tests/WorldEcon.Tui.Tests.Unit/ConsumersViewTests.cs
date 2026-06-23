using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Economy;
using WorldEcon.Tui;
using WorldEcon.Tui.Navigation;

namespace WorldEcon.Tui.Tests.Unit;

public class ConsumersViewTests
{
    [Test]
    public async Task ConsumersRoot_ListsConsumers_WithSizeAndBudget()
    {
        var path = await TestWorld.SeedTempDbAsync();
        try
        {
            await using var ctx = TestWorld.NewContext(path);
            var tui = await TuiContext.LoadAsync(ctx);
            var settlement = await ctx.Settlements.FirstAsync();
            ctx.Consumers.Add(RepresentativeConsumer.Create(tui.World.Id, settlement.Id, 1000,
                new WorldEcon.SharedKernel.Money(500)).Value);
            await ctx.SaveChangesAsync();

            var nav = new Navigator();
            var view = await nav.RootAsync("consumers", tui);
            view.Columns.Should().ContainInOrder("Settlement", "Size", "Budget");
            view.Rows.Should().NotBeEmpty();
        }
        finally { File.Delete(path); }
    }
}
