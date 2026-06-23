using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Economy;
using WorldEcon.SharedKernel;
using WorldEcon.Tui;
using WorldEcon.Tui.Navigation;

namespace WorldEcon.Tui.Tests.Unit;

public class MerchantsViewTests
{
    [Test]
    public async Task MerchantsRoot_NamesMerchantsBySeat_WithRomanOrdinalForSiblings()
    {
        var path = await TestWorld.SeedTempDbAsync();
        try
        {
            await using var ctx = TestWorld.NewContext(path);
            var tui = await TuiContext.LoadAsync(ctx);
            var seat = await ctx.Settlements.FirstAsync();

            // Two merchants seated at the same settlement → "<Seat> Caravaneer" and "... Caravaneer II".
            ctx.Merchants.Add(RepresentativeMerchant.Create(tui.World.Id, seat.Id, new Money(100), 50, 1000).Value);
            ctx.Merchants.Add(RepresentativeMerchant.Create(tui.World.Id, seat.Id, new Money(100), 50, 1000).Value);
            await ctx.SaveChangesAsync();

            var view = await new Navigator().RootAsync("merchants", tui);

            view.Columns[0].Should().Be("Merchant");
            var names = view.Rows.Select(r => r.Cells[0]).ToList();
            names.Should().Contain($"{seat.Name} Caravaneer");
            names.Should().Contain($"{seat.Name} Caravaneer II");
            // No row is the bare city name (the disambiguation goal).
            names.Should().NotContain(seat.Name);
        }
        finally { File.Delete(path); }
    }
}
