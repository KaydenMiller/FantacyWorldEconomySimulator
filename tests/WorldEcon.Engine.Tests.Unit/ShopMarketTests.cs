using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Economy;
using WorldEcon.Engine;
using WorldEcon.SharedKernel;

namespace WorldEcon.Engine.Tests.Unit;

public class ShopMarketTests
{
    [Test]
    public async Task PublicMarketShop_IsCreatedOnceAndReused()
    {
        var s = await LogTestWorld.CreateAsync();
        try
        {
            var sim = await SimulationContext.LoadAsync(s.Db, s.World.Id);
            var a = await ShopMarket.GetOrCreatePublicMarketShop(sim, s.Settlement.Id);
            var b = await ShopMarket.GetOrCreatePublicMarketShop(sim, s.Settlement.Id);
            a.Id.Should().Be(b.Id);
            a.Kind.Should().Be(ShopKind.PublicMarket);
        }
        finally { await LogTestWorld.DisposeAsync(s); }
    }

    [Test]
    public async Task WithdrawAcrossShops_DepletesInOrder_AndReportsTaken()
    {
        var s = await LogTestWorld.CreateAsync();
        try
        {
            var good = Good.Create(s.World.Id, "Grain", GoodCategory.Food, new Money(10), "sack",
                SizeClass.Medium, 0, true, 0).Value;
            s.Db.Goods.Add(good);
            // Two shops in the settlement, 30 + 30 of the good.
            var shopA = Shop.Create(s.World.Id, s.Settlement.Id, "A", 0, Money.Zero).Value;
            var shopB = Shop.Create(s.World.Id, s.Settlement.Id, "B", 0, Money.Zero).Value;
            s.Db.Shops.AddRange(shopA, shopB);
            s.Db.Stockpiles.Add(Stockpile.CreateForShop(s.World.Id, shopA.Id, good.Id, 30, new Money(10)).Value);
            s.Db.Stockpiles.Add(Stockpile.CreateForShop(s.World.Id, shopB.Id, good.Id, 30, new Money(10)).Value);
            await s.Db.SaveChangesAsync();

            var sim = await SimulationContext.LoadAsync(s.Db, s.World.Id);
            long taken = await ShopMarket.WithdrawAcrossShops(sim, s.Settlement.Id, good.Id, 50);
            taken.Should().Be(50);
            (await ShopMarket.TotalSupply(sim, s.Settlement.Id, good.Id)).Should().Be(10);
        }
        finally { await LogTestWorld.DisposeAsync(s); }
    }
}
