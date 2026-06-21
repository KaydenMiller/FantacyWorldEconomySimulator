using FluentAssertions;
using WorldEcon.Domain.Economy;
using WorldEcon.Domain.Geography;
using WorldEcon.SharedKernel;
using WorldEcon.SharedKernel.Domain;

namespace WorldEcon.Domain.Tests.Unit.Economy;

public class GoodTests
{
    [Test]
    public void Create_SetsFields_DefaultProvenanceAuthored()
    {
        var g = Good.Create(WorldId.New(), "Health Potion", GoodCategory.Potion,
            baseValue: new Money(5000), baseUnit: "vial", SizeClass.Small,
            shelfLifeTicks: 0, divisible: false).Value;
        g.Name.Should().Be("Health Potion");
        g.Category.Should().Be(GoodCategory.Potion);
        g.BaseValue.Should().Be(new Money(5000));
        g.BaseUnit.Should().Be("vial");
        g.Size.Should().Be(SizeClass.Small);
        g.ShelfLifeTicks.Should().Be(0);
        g.Divisible.Should().BeFalse();
        g.Provenance.Should().Be(Provenance.Authored);
    }

    [Test]
    public void Create_RejectsBlankName()
        => Good.Create(WorldId.New(), " ", GoodCategory.Misc, Money.Zero, "u", SizeClass.Small, 0, false)
            .IsError.Should().BeTrue();

    [Test]
    public void Create_RejectsNegativeBaseValue()
        => Good.Create(WorldId.New(), "X", GoodCategory.Misc, new Money(-1), "u", SizeClass.Small, 0, false)
            .IsError.Should().BeTrue();

    [Test]
    public void Create_RejectsNegativeShelfLife()
        => Good.Create(WorldId.New(), "X", GoodCategory.Misc, Money.Zero, "u", SizeClass.Small, -1, false)
            .IsError.Should().BeTrue();

    [Test]
    public void Create_SetsConsumptionPerCapitaBp()
    {
        var g = Good.Create(WorldId.New(), "Bread", GoodCategory.Food, new Money(30), "loaf",
            SizeClass.Small, shelfLifeTicks: 4320, divisible: true, consumptionPerCapitaBp: 500).Value;
        g.ConsumptionPerCapitaBp.Should().Be(500);
    }

    [Test]
    public void Create_DefaultsConsumptionPerCapitaBpToZero()
    {
        var g = Good.Create(WorldId.New(), "Iron", GoodCategory.Material, new Money(200), "ingot",
            SizeClass.Medium, 0, false).Value;
        g.ConsumptionPerCapitaBp.Should().Be(0);
    }

    [Test]
    public void Create_RejectsNegativeConsumptionPerCapitaBp()
        => Good.Create(WorldId.New(), "X", GoodCategory.Misc, Money.Zero, "u", SizeClass.Small, 0, false, consumptionPerCapitaBp: -1)
            .IsError.Should().BeTrue();
}
