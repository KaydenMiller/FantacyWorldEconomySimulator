using FluentAssertions;
using WorldEcon.Domain.Geography;
using WorldEcon.SharedKernel.Calendar;
using WorldEcon.SharedKernel.Measure;

namespace WorldEcon.Domain.Tests.Unit;

public class WorldTransportTuningTests
{
    private static World NewWorld() =>
        World.Create("Aerth", 1UL, CalendarDefinition.Default, "1.0.0").Value;

    [Test]
    public void Defaults()
    {
        var w = NewWorld();
        w.VolumetricDivisor.Should().Be(5000);
        w.TransportRate.Should().Be(1);
        w.DisplayUnitSystem.Should().Be(UnitSystem.Metric);
    }

    [Test]
    public void SetTransportTuning_Validates()
    {
        var w = NewWorld();
        w.SetTransportTuning(6000, 3);
        w.VolumetricDivisor.Should().Be(6000);
        w.TransportRate.Should().Be(3);
        Action bad = () => w.SetTransportTuning(0, 1);
        bad.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Test]
    public void SetDisplayUnitSystem()
    {
        var w = NewWorld();
        w.SetDisplayUnitSystem(UnitSystem.Imperial);
        w.DisplayUnitSystem.Should().Be(UnitSystem.Imperial);
    }
}
