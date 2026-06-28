using FluentAssertions;
using WorldEcon.Domain.Economy;
using WorldEcon.Domain.Geography;
using WorldEcon.SharedKernel;
using WorldEcon.SharedKernel.Measure;

namespace WorldEcon.Domain.Tests.Unit;

public class GoodPhysicalTests
{
    [Test]
    public void Defaults_DeriveFromSizeClass()
    {
        Good.DefaultMassForSize(SizeClass.Tiny).Grams.Should().Be(50);
        Good.DefaultMassForSize(SizeClass.Small).Grams.Should().Be(1_000);
        Good.DefaultMassForSize(SizeClass.Medium).Grams.Should().Be(10_000);
        Good.DefaultMassForSize(SizeClass.Large).Grams.Should().Be(50_000);
        Good.DefaultMassForSize(SizeClass.Bulky).Grams.Should().Be(200_000);
        Good.DefaultVolumeForSize(SizeClass.Tiny).CubicCentimeters.Should().Be(50);
        Good.DefaultVolumeForSize(SizeClass.Small).CubicCentimeters.Should().Be(1_000);
        Good.DefaultVolumeForSize(SizeClass.Medium).CubicCentimeters.Should().Be(20_000);
        Good.DefaultVolumeForSize(SizeClass.Large).CubicCentimeters.Should().Be(100_000);
        Good.DefaultVolumeForSize(SizeClass.Bulky).CubicCentimeters.Should().Be(500_000);
    }

    [Test]
    public void Create_UsesSizeDefaults_WhenOmitted()
    {
        var g = Good.Create(WorldId.New(), "Sack", GoodCategory.Food, new Money(10), "sack",
            SizeClass.Medium, 0, true).Value;
        g.MassPerUnit.Grams.Should().Be(10_000);
        g.VolumePerUnit.CubicCentimeters.Should().Be(20_000);
    }

    [Test]
    public void Create_AcceptsExplicitOverride()
    {
        var g = Good.Create(WorldId.New(), "Ingot", GoodCategory.Material, new Money(200), "ingot",
            SizeClass.Small, 0, false,
            massPerUnit: new Mass(30_000), volumePerUnit: new Volume(4_000)).Value;
        g.MassPerUnit.Grams.Should().Be(30_000);
        g.VolumePerUnit.CubicCentimeters.Should().Be(4_000);
    }

    [Test]
    public void Create_RejectsNonPositivePhysicals()
    {
        Good.Create(WorldId.New(), "Bad", GoodCategory.Misc, new Money(1), "u", SizeClass.Small, 0, true,
            massPerUnit: new Mass(0)).IsError.Should().BeTrue();
        Good.Create(WorldId.New(), "Bad", GoodCategory.Misc, new Money(1), "u", SizeClass.Small, 0, true,
            volumePerUnit: new Volume(0)).IsError.Should().BeTrue();
        Good.Create(WorldId.New(), "Bad", GoodCategory.Misc, new Money(1), "u", SizeClass.Small, 0, true,
            massPerUnit: new Mass(-500)).IsError.Should().BeTrue();
        Good.Create(WorldId.New(), "Bad", GoodCategory.Misc, new Money(1), "u", SizeClass.Small, 0, true,
            volumePerUnit: new Volume(-1)).IsError.Should().BeTrue();
    }

    [Test]
    public void Setters_Validate()
    {
        var g = Good.Create(WorldId.New(), "X", GoodCategory.Misc, new Money(1), "u", SizeClass.Small, 0, true).Value;
        g.SetMassPerUnit(new Mass(5000)).IsError.Should().BeFalse();
        g.MassPerUnit.Grams.Should().Be(5000);
        g.SetMassPerUnit(new Mass(0)).IsError.Should().BeTrue();
        g.SetVolumePerUnit(new Volume(8000)).IsError.Should().BeFalse();
        g.VolumePerUnit.CubicCentimeters.Should().Be(8000);
        g.SetVolumePerUnit(new Volume(0)).IsError.Should().BeTrue();
    }
}
