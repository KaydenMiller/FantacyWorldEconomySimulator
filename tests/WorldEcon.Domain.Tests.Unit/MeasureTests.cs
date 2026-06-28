using FluentAssertions;
using WorldEcon.SharedKernel.Measure;

namespace WorldEcon.Domain.Tests.Unit;

public class MassVolumeTests
{
    [Test]
    public void Mass_Arithmetic()
    {
        (new Mass(1000) + new Mass(500)).Grams.Should().Be(1500);
        (new Mass(1000) - new Mass(400)).Grams.Should().Be(600);
        (new Mass(250) * 4).Grams.Should().Be(1000);
        Mass.Zero.Grams.Should().Be(0);
    }

    [Test]
    public void Volume_Arithmetic()
    {
        (new Volume(1000) + new Volume(500)).CubicCentimeters.Should().Be(1500);
        (new Volume(1000) - new Volume(400)).CubicCentimeters.Should().Be(600);
        (new Volume(250) * 4).CubicCentimeters.Should().Be(1000);
        Volume.Zero.CubicCentimeters.Should().Be(0);
    }

    [Test]
    public void IsNegative_ReflectsSign()
    {
        new Mass(-1).IsNegative.Should().BeTrue();
        new Mass(0).IsNegative.Should().BeFalse();
        new Mass(5).IsNegative.Should().BeFalse();
        new Volume(-1).IsNegative.Should().BeTrue();
        new Volume(0).IsNegative.Should().BeFalse();
        new Volume(5).IsNegative.Should().BeFalse();
    }
}

public class MeasurementFormatTests
{
    [Test]
    public void FormatMass_Metric()
    {
        MeasurementFormat.FormatMass(new Mass(250), UnitSystem.Metric).Should().Be("250 g");
        MeasurementFormat.FormatMass(new Mass(1000), UnitSystem.Metric).Should().Be("1 kg");
        MeasurementFormat.FormatMass(new Mass(1500), UnitSystem.Metric).Should().Be("1.5 kg");
        MeasurementFormat.FormatMass(new Mass(30000), UnitSystem.Metric).Should().Be("30 kg");
        MeasurementFormat.FormatMass(new Mass(2_000_000), UnitSystem.Metric).Should().Be("2 t");
    }

    [Test]
    public void FormatVolume_Metric()
    {
        MeasurementFormat.FormatVolume(new Volume(50), UnitSystem.Metric).Should().Be("50 cm³");
        MeasurementFormat.FormatVolume(new Volume(1000), UnitSystem.Metric).Should().Be("1 L");
        MeasurementFormat.FormatVolume(new Volume(200000), UnitSystem.Metric).Should().Be("200 L");
        MeasurementFormat.FormatVolume(new Volume(1_000_000), UnitSystem.Metric).Should().Be("1 m³");
    }

    [Test]
    public void FormatMass_Imperial_IsApproximate()
    {
        MeasurementFormat.FormatMass(new Mass(1000), UnitSystem.Imperial).Should().Be("2.2 lb");
    }

    [Test]
    public void FormatVolume_Imperial_IsApproximate()
    {
        MeasurementFormat.FormatVolume(new Volume(28317), UnitSystem.Imperial).Should().Be("1 ft³");
        MeasurementFormat.FormatVolume(new Volume(16), UnitSystem.Imperial).Should().Be("0.98 in³");
    }

    [Test]
    public void ParseMass_IsSystemAgnostic()
    {
        MeasurementFormat.TryParseMass("5 kg", out var a).Should().BeTrue();
        a.Grams.Should().Be(5000);
        MeasurementFormat.TryParseMass("250g", out var b).Should().BeTrue();
        b.Grams.Should().Be(250);
        MeasurementFormat.TryParseMass("1.2 kg", out var c).Should().BeTrue();
        c.Grams.Should().Be(1200);
        MeasurementFormat.TryParseMass("8 oz", out var d).Should().BeTrue();
        d.Grams.Should().Be(227); // round(8 × 28.349523125)
        MeasurementFormat.TryParseMass("nonsense", out _).Should().BeFalse();
        MeasurementFormat.TryParseMass("5 furlongs", out _).Should().BeFalse();
    }

    [Test]
    public void ParseVolume_IsSystemAgnostic()
    {
        MeasurementFormat.TryParseVolume("200 L", out var a).Should().BeTrue();
        a.CubicCentimeters.Should().Be(200000);
        MeasurementFormat.TryParseVolume("0.2 m3", out var b).Should().BeTrue();
        b.CubicCentimeters.Should().Be(200000);
        MeasurementFormat.TryParseVolume("500 cm3", out var c).Should().BeTrue();
        c.CubicCentimeters.Should().Be(500);
        MeasurementFormat.TryParseVolume("1 ft3", out var d).Should().BeTrue();
        d.CubicCentimeters.Should().Be(28317); // round(28316.846592)
        MeasurementFormat.TryParseVolume("nope", out _).Should().BeFalse();
    }
}
