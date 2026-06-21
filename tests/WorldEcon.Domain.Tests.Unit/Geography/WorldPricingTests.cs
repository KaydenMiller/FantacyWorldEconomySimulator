using FluentAssertions;
using WorldEcon.Domain.Geography;
using WorldEcon.SharedKernel.Calendar;

namespace WorldEcon.Domain.Tests.Unit.Geography;

public class WorldPricingTests
{
    private static World NewWorld() =>
        World.Create("Aerth", 1UL, CalendarDefinition.Default, "1.0.0").Value;

    [Test]
    public void Create_HasDefaultPricingParameters()
    {
        var w = NewWorld();
        w.ElasticityExponent.Should().Be(1);
        w.MinPriceMultBp.Should().Be(1_000);   // 0.1x
        w.MaxPriceMultBp.Should().Be(100_000); // 10x
    }

    [Test]
    public void SetPricingParameters_UpdatesValues()
    {
        var w = NewWorld();
        w.SetPricingParameters(2, 5_000, 50_000);
        w.ElasticityExponent.Should().Be(2);
        w.MinPriceMultBp.Should().Be(5_000);
        w.MaxPriceMultBp.Should().Be(50_000);
    }

    [Test]
    public void SetPricingParameters_RejectsNegativeExponent()
    {
        var w = NewWorld();
        var act = () => w.SetPricingParameters(-1, 1_000, 100_000);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Test]
    public void SetPricingParameters_RejectsNonPositiveMin()
    {
        var w = NewWorld();
        var act = () => w.SetPricingParameters(1, 0, 100_000);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Test]
    public void SetPricingParameters_RejectsMinGreaterThanMax()
    {
        var w = NewWorld();
        var act = () => w.SetPricingParameters(1, 200_000, 100_000);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
