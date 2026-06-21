using FluentAssertions;
using WorldEcon.Domain.Economy;
using WorldEcon.SharedKernel;

namespace WorldEcon.Domain.Tests.Unit.Economy;

public class WeightedAverageValuationTests
{
    private static readonly ICostBasisValuation Sut = new WeightedAverageValuation();

    [Test]
    public void Blend_IntoEmpty_TakesIncomingBasis()
        => Sut.Blend(0, Money.Zero, 10, new Money(100)).Should().Be(new Money(100));

    [Test]
    public void Blend_EqualQuantities_AveragesBasis()
        => Sut.Blend(10, new Money(100), 10, new Money(200)).Should().Be(new Money(150));

    [Test]
    public void Blend_WeightsByQuantity()
        // (90*100 + 10*200) / 100 = 110
        => Sut.Blend(90, new Money(100), 10, new Money(200)).Should().Be(new Money(110));

    [Test]
    public void Blend_RoundsHalfToEven()
        // (1*100 + 1*101)/2 = 100.5 -> 100 (even)
        => Sut.Blend(1, new Money(100), 1, new Money(101)).Should().Be(new Money(100));

    [Test]
    public void Blend_RejectsNonPositiveIncoming()
    {
        var act = () => Sut.Blend(10, new Money(100), 0, new Money(50));
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Test]
    public void Blend_Throws_OnOverflow()
    {
        // Isolates the totalCost line: totalQty (=3) does NOT overflow, but
        // 2*long.MaxValue + 1 does — so this fails without the checked() guard.
        var act = () => Sut.Blend(2, new Money(long.MaxValue), 1, new Money(1));
        act.Should().Throw<OverflowException>();
    }
}
