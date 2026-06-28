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
}
