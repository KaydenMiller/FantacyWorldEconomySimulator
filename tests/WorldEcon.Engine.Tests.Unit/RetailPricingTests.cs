using FluentAssertions;
using WorldEcon.Domain.Economy;
using WorldEcon.Domain.Geography;
using WorldEcon.Engine.Demand;
using WorldEcon.SharedKernel;
using WorldEcon.SharedKernel.Calendar;

namespace WorldEcon.Engine.Tests.Unit;

public class RetailPricingTests
{
    private static World NewWorld() => World.Create("T", 1UL, CalendarDefinition.Default, "1.0.0").Value;

    [Test]
    public void ScarceGood_PricesHigherThanGlut()
    {
        var w = NewWorld();
        long scarce = RetailPricing.ScarcityMultBp(demand: 1000, supply: 10, w);   // demand >> supply
        long glut = RetailPricing.ScarcityMultBp(demand: 10, supply: 1000, w);     // supply >> demand
        scarce.Should().BeGreaterThan(glut);

        var costed = new Money(100);
        var scarcePrice = RetailPricing.RetailPrice(costed, markupBp: 2000, scarce);
        var glutPrice = RetailPricing.RetailPrice(costed, markupBp: 2000, glut);
        scarcePrice.Units.Should().BeGreaterThan(glutPrice.Units);
        glutPrice.Units.Should().BeGreaterThanOrEqualTo(costed.Units); // retail never below cost
    }

    [Test]
    public void AllowanceIncome_ScalesWithSize()
    {
        var income = new AllowanceIncome(perCapitaAllowance: 5);
        income.GrantFor(size: 1000).Should().Be(new Money(5000));
    }
}
