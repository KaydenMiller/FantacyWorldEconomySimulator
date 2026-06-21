using FluentAssertions;
using WorldEcon.Domain.Economy;
using WorldEcon.Domain.Geography;
using WorldEcon.SharedKernel;

namespace WorldEcon.Domain.Tests.Unit.Economy;

public class ShopTests
{
    [Test]
    public void Create_SetsFields()
    {
        var s = Shop.Create(WorldId.New(), SettlementId.New(), "The Sundries", markupBp: 2000, till: new Money(10_000)).Value;
        s.Name.Should().Be("The Sundries");
        s.MarkupBp.Should().Be(2000);
        s.Till.Should().Be(new Money(10_000));
    }

    [Test]
    public void Create_RejectsNegativeMarkup()
        => Shop.Create(WorldId.New(), SettlementId.New(), "X", -1, Money.Zero).IsError.Should().BeTrue();

    [Test]
    public void Quote_AppliesMarkupOverCost()
    {
        var s = Shop.Create(WorldId.New(), SettlementId.New(), "X", markupBp: 2000, Money.Zero).Value; // 20%
        var q = s.Quote(new Money(100));
        q.SalePrice.Should().Be(new Money(120));
        q.MarginAbs.Should().Be(new Money(20));
        q.MarginBp.Should().Be(2000);
    }

    [Test]
    public void Quote_ZeroMarkup_SalePriceEqualsCost()
    {
        var s = Shop.Create(WorldId.New(), SettlementId.New(), "X", 0, Money.Zero).Value;
        var q = s.Quote(new Money(100));
        q.SalePrice.Should().Be(new Money(100));
        q.MarginAbs.Should().Be(Money.Zero);
    }
}
