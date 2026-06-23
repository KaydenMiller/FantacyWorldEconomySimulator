using FluentAssertions;
using WorldEcon.Domain.Economy;
using WorldEcon.Domain.Geography;
using WorldEcon.SharedKernel;

namespace WorldEcon.Domain.Tests.Unit;

public class ConsumerTests
{
    private static readonly WorldId World = WorldId.New();
    private static readonly SettlementId Seat = SettlementId.New();

    [Test]
    public void Create_Succeeds_AndSpendEarnGuard()
    {
        var c = RepresentativeConsumer.Create(World, Seat, 1000, new Money(500)).Value;
        c.Size.Should().Be(1000);
        c.Budget.Should().Be(new Money(500));

        c.Spend(new Money(200));
        c.Budget.Should().Be(new Money(300));
        c.Earn(new Money(100));
        c.Budget.Should().Be(new Money(400));

        var act = () => c.Spend(new Money(10_000));
        act.Should().Throw<InvalidOperationException>(); // cannot overspend
    }

    [Test]
    public void Create_RejectsZeroSize()
        => RepresentativeConsumer.Create(World, Seat, 0, Money.Zero).IsError.Should().BeTrue();

    [Test]
    public void Good_DefaultNeedTier_IsEssential()
    {
        var g = Good.Create(World, "Bread", GoodCategory.Food, new Money(30), "loaf", SizeClass.Small, 0, true, 50).Value;
        g.Need.Should().Be(NeedTier.Essential);
    }

    [Test]
    public void Good_NeedTier_CanBeSet()
    {
        var g = Good.Create(World, "Lute", GoodCategory.Luxury, new Money(900), "lute", SizeClass.Small, 0, false, 5, NeedTier.Comfort).Value;
        g.Need.Should().Be(NeedTier.Comfort);
    }

    [Test]
    public void Shop_CreditTill_AddsToTill()
    {
        var s = Shop.Create(World, Seat, "The Sundries", 2000, new Money(100)).Value;
        s.CreditTill(new Money(250));
        s.Till.Should().Be(new Money(350));
    }
}
