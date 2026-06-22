using FluentAssertions;
using WorldEcon.SharedKernel;
using WorldEcon.SharedKernel.Currency;

namespace WorldEcon.SharedKernel.Tests.Unit;

public class CurrencyFormatTests
{
    private static readonly CurrencyDefinition Def = CurrencyDefinition.Default;

    [Test]
    public void Format_321_Returns3g2s1c()
        => Def.Format(new Money(321)).Should().Be("3g 2s 1c");

    [Test]
    public void Format_300_Returns3g()
        => Def.Format(new Money(300)).Should().Be("3g");

    [Test]
    public void Format_1234_Returns1p2g3s4c()
        => Def.Format(new Money(1234)).Should().Be("1p 2g 3s 4c");

    [Test]
    public void Format_5_Returns5c()
        => Def.Format(new Money(5)).Should().Be("5c");

    [Test]
    public void Format_0_Returns0c()
        => Def.Format(new Money(0)).Should().Be("0c");

    [Test]
    public void Format_Negative321_ReturnsMinus3g2s1c()
        => Def.Format(new Money(-321)).Should().Be("-3g 2s 1c");

    [Test]
    public void Format_Negative5_ReturnsMinus5c()
        => Def.Format(new Money(-5)).Should().Be("-5c");

    [Test]
    public void Default_HasFourDenominations()
        => Def.Denominations.Should().HaveCount(4);

    [Test]
    public void Default_BaseUnitIsCopperWithUnits1()
    {
        var copper = Def.Denominations[0];
        copper.Name.Should().Be("Copper");
        copper.Symbol.Should().Be("c");
        copper.Units.Should().Be(1);
    }

    [Test]
    public void Default_PlatinumIsHighest_1000Units()
    {
        var plat = Def.Denominations[^1];
        plat.Name.Should().Be("Platinum");
        plat.Symbol.Should().Be("p");
        plat.Units.Should().Be(1000);
    }
}
