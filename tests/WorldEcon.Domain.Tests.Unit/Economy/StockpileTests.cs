using FluentAssertions;
using WorldEcon.Domain.Economy;
using WorldEcon.Domain.Geography;
using WorldEcon.SharedKernel;

namespace WorldEcon.Domain.Tests.Unit.Economy;

public class StockpileTests
{
    private static readonly ICostBasisValuation Valuation = new WeightedAverageValuation();

    private static Stockpile NewShopStockpile(out ShopId shop, out GoodId good)
    {
        shop = ShopId.New();
        good = GoodId.New();
        return Stockpile.CreateForShop(WorldId.New(), shop, good, quantity: 10, unitCostBasis: new Money(100)).Value;
    }

    [Test]
    public void CreateForShop_SetsOwnerAndFields()
    {
        var s = NewShopStockpile(out var shop, out var good);
        s.OwnerKind.Should().Be(StockpileOwnerKind.Shop);
        s.OwnerId.Should().Be(shop.Value);
        s.GoodId.Should().Be(good);
        s.Quantity.Should().Be(10);
        s.CostBasis.Should().Be(new Money(100));
    }

    [Test]
    public void Create_RejectsNegativeQuantity()
        => Stockpile.CreateForShop(WorldId.New(), ShopId.New(), GoodId.New(), -1, Money.Zero)
            .IsError.Should().BeTrue();

    [Test]
    public void Deposit_BlendsCostBasis_AndAddsQuantity()
    {
        var s = NewShopStockpile(out _, out _); // 10 @ 100
        s.Deposit(10, new Money(200), Valuation); // -> 20 @ 150
        s.Quantity.Should().Be(20);
        s.CostBasis.Should().Be(new Money(150));
    }

    [Test]
    public void Withdraw_ReducesQuantity_KeepsBasis()
    {
        var s = NewShopStockpile(out _, out _); // 10 @ 100
        var result = s.Withdraw(4);
        result.IsError.Should().BeFalse();
        s.Quantity.Should().Be(6);
        s.CostBasis.Should().Be(new Money(100));
    }

    [Test]
    public void Withdraw_MoreThanOnHand_IsError_AndDoesNotMutate()
    {
        var s = NewShopStockpile(out _, out _); // 10
        s.Withdraw(11).IsError.Should().BeTrue();
        s.Quantity.Should().Be(10);
    }
}
