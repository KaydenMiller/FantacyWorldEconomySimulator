using FluentAssertions;

namespace WorldEcon.SharedKernel.Tests.Unit;

public class MoneyTests
{
    [Test]
    public void Zero_IsZeroUnits()
        => Money.Zero.Units.Should().Be(0);

    [Test]
    public void Add_SumsUnits()
        => (new Money(150) + new Money(25)).Units.Should().Be(175);

    [Test]
    public void Subtract_DiffsUnits()
        => (new Money(150) - new Money(25)).Units.Should().Be(125);

    [Test]
    public void MultiplyByQuantity_ScalesUnits()
        => (new Money(40) * 3L).Units.Should().Be(120);

    [Test]
    public void Negate_FlipsSign()
        => (-new Money(40)).Units.Should().Be(-40);

    [Test]
    public void IsNegative_TrueForBelowZero()
        => new Money(-1).IsNegative.Should().BeTrue();
}
