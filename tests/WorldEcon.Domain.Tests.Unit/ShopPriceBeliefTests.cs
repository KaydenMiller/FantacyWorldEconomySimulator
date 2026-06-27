using FluentAssertions;
using WorldEcon.Domain.Economy;
using WorldEcon.Domain.Geography;
using WorldEcon.SharedKernel;

namespace WorldEcon.Domain.Tests.Unit;

public class ShopPriceBeliefTests
{
    private static ShopPriceBelief Band(long low, long high)
    {
        var b = ShopPriceBelief.Bootstrap(WorldId.New(), ShopId.New(), GoodId.New(), new Money(100));
        // Bootstrap gives [80,120]; nudge to the requested band via the public update methods is awkward,
        // so re-bootstrap on a base that yields the wanted band when possible. For these tests we just use
        // Bootstrap's [80,120] and assert the deltas from there.
        return b;
    }

    [Test]
    public void Bootstrap_BandsAroundBaseValue()
    {
        var b = ShopPriceBelief.Bootstrap(WorldId.New(), ShopId.New(), GoodId.New(), new Money(100));
        b.Low.Units.Should().Be(80);
        b.High.Units.Should().Be(120);
    }

    [Test]
    public void RecordSale_NarrowsTowardCenter()
    {
        var b = ShopPriceBelief.Bootstrap(WorldId.New(), ShopId.New(), GoodId.New(), new Money(100)); // [80,120]
        b.RecordSale(1000); // 10% toward centre 100
        b.Low.Units.Should().Be(82);
        b.High.Units.Should().Be(118);
    }

    [Test]
    public void RecordMiss_WidensAndShiftsTowardClearingPrice()
    {
        var b = ShopPriceBelief.Bootstrap(WorldId.New(), ShopId.New(), GoodId.New(), new Money(100)); // [80,120]
        b.RecordMiss(1000, 2000, new Money(60), new Money(100)); // widen 10%, shift 20% toward 60
        b.Low.Units.Should().Be(76);
        b.High.Units.Should().Be(112);
    }

    [Test]
    public void RecordMiss_NothingCleared_ShiftsTowardBaseValue()
    {
        var b = ShopPriceBelief.Bootstrap(WorldId.New(), ShopId.New(), GoodId.New(), new Money(200)); // [160,240]
        var lowBefore = b.Low.Units;
        b.RecordMiss(1000, 2000, clearingPrice: null, baseValue: new Money(100)); // shift down toward base 100
        b.Low.Units.Should().BeLessThan(lowBefore);
    }

    [Test]
    public void Band_NeverInverts_OrGoesBelowOne()
    {
        var b = ShopPriceBelief.Bootstrap(WorldId.New(), ShopId.New(), GoodId.New(), new Money(1));
        for (int i = 0; i < 50; i++)
            b.RecordMiss(1000, 2000, new Money(0), new Money(0));
        b.Low.Units.Should().BeGreaterThanOrEqualTo(1);
        b.High.Units.Should().BeGreaterThanOrEqualTo(b.Low.Units);
    }
}

public class GoodWillingnessTests
{
    [Test]
    public void DefaultPeakWillingness_ByTier()
    {
        Good.DefaultPeakWillingnessForTier(NeedTier.Essential).Should().Be(40_000);
        Good.DefaultPeakWillingnessForTier(NeedTier.Standard).Should().Be(18_000);
        Good.DefaultPeakWillingnessForTier(NeedTier.Comfort).Should().Be(13_000);
    }

    [Test]
    public void Create_DefaultsPeakFromTier()
    {
        var good = Good.Create(WorldId.New(), "Wine", GoodCategory.Luxury, new Money(40), "bottle",
            SizeClass.Small, 0, false, 10, NeedTier.Comfort).Value;
        good.PeakWillingnessMultipleBasisPoints.Should().Be(13_000);
    }

    [Test]
    public void Create_AcceptsExplicitPeak_RejectsBelowBase()
    {
        Good.Create(WorldId.New(), "Spice", GoodCategory.Luxury, new Money(40), "pinch",
            SizeClass.Tiny, 0, true, 5, NeedTier.Comfort, peakWillingnessMultipleBasisPoints: 25_000)
            .Value.PeakWillingnessMultipleBasisPoints.Should().Be(25_000);

        Good.Create(WorldId.New(), "Bad", GoodCategory.Misc, new Money(40), "unit",
            SizeClass.Tiny, 0, true, 0, NeedTier.Comfort, peakWillingnessMultipleBasisPoints: 9_000)
            .IsError.Should().BeTrue();
    }
}
