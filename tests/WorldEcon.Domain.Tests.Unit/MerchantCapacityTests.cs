using FluentAssertions;
using WorldEcon.Domain.Economy;
using WorldEcon.Domain.Geography;
using WorldEcon.SharedKernel;
using WorldEcon.SharedKernel.Measure;

namespace WorldEcon.Domain.Tests.Unit;

public class MerchantCapacityTests
{
    [Test]
    public void Create_StoresWeightAndVolumeCapacity()
    {
        var m = RepresentativeMerchant.Create(WorldId.New(), SettlementId.New(), new Money(50_000),
            new Mass(600_000), new Volume(1_000_000), 1000).Value;
        m.WeightCapacity.Grams.Should().Be(600_000);
        m.VolumeCapacity.CubicCentimeters.Should().Be(1_000_000);
        m.Reach.Should().Be(1000);
    }

    [Test]
    public void Create_RejectsNonPositiveCapacity()
    {
        RepresentativeMerchant.Create(WorldId.New(), SettlementId.New(), new Money(1),
            new Mass(0), new Volume(1000), 1000).IsError.Should().BeTrue();
        RepresentativeMerchant.Create(WorldId.New(), SettlementId.New(), new Money(1),
            new Mass(1000), new Volume(0), 1000).IsError.Should().BeTrue();
    }

    [Test]
    public void SetCapacity_UpdatesOnSuccess_RejectsNonPositive()
    {
        var m = RepresentativeMerchant.Create(WorldId.New(), SettlementId.New(), new Money(1),
            new Mass(100_000), new Volume(200_000), 1000).Value;

        m.SetCapacity(new Mass(800_000), new Volume(1_500_000)).IsError.Should().BeFalse();
        m.WeightCapacity.Grams.Should().Be(800_000);
        m.VolumeCapacity.CubicCentimeters.Should().Be(1_500_000);

        m.SetCapacity(new Mass(0), new Volume(1000)).IsError.Should().BeTrue();
        m.SetCapacity(new Mass(1000), new Volume(0)).IsError.Should().BeTrue();
    }
}
