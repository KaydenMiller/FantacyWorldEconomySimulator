using FluentAssertions;
using WorldEcon.Engine.Trade;
using WorldEcon.SharedKernel.Measure;

namespace WorldEcon.Engine.Tests.Unit;

public class HaulageMathTests
{
    [Test]
    public void DimensionalWeight_TakesTheBindingDimension()
    {
        Haulage.DimensionalWeightGrams(30_000, 4_000, 5_000).Should().Be(30_000);
        Haulage.DimensionalWeightGrams(2_000, 60_000, 5_000).Should().Be(12_000);
    }

    [Test]
    public void Cost_ScalesWithDimWeightDistanceAndRate()
    {
        Haulage.Cost(25_000, 120, 1).Should().Be(3);
        Haulage.Cost(25_000, 120, 4).Should().Be(12);
    }

    [Test]
    public void CargoFit_GatesByWeightForDense_ByVolumeForBulky()
    {
        CargoFit.MaxUnits(new Mass(600_000), new Volume(1_000_000), new Mass(30_000), new Volume(4_000)).Should().Be(20);
        CargoFit.MaxUnits(new Mass(600_000), new Volume(1_000_000), new Mass(2_000), new Volume(60_000)).Should().Be(16);
    }
}
