using FluentAssertions;
using WorldEcon.Domain.Economy;
using WorldEcon.Domain.Geography;
using WorldEcon.SharedKernel;
using WorldEcon.SharedKernel.Measure;

namespace WorldEcon.Domain.Tests.Unit.Economy;

public class TravelKnowledgeTests
{
    private static readonly SettlementId A = SettlementId.New(), B = SettlementId.New();

    [Test]
    public void Report_replaces_estimate_and_only_newer_reports_replace_reports()
    {
        var row = TravelKnowledge.Estimate(WorldId.New(), B, A, B, "Wagon", 1440, 30_000).Value;
        row.Offer(new TripRecord(A, B, "Wagon", 1800, 30_000, 5000)).Should().BeTrue();
        (row.Ticks, row.Source, row.UpdatedTick).Should().Be((1800L, KnowledgeSource.Reported, 5000L));
        row.Offer(new TripRecord(A, B, "Wagon", 1000, 30_000, 4000)).Should().BeFalse("older than what the row holds");
        row.Offer(new TripRecord(A, B, "Wagon", 1600, 30_000, 6000)).Should().BeTrue();
        row.Ticks.Should().Be(1600);
    }

    [Test]
    public void Offer_for_another_pair_throws()
    {
        var row = TravelKnowledge.Estimate(WorldId.New(), B, A, B, "Wagon", 1440, 30_000).Value;
        var act = () => row.Offer(new TripRecord(B, A, "Wagon", 1, 1, 1));
        act.Should().Throw<ArgumentException>();
    }

    [Test]
    public void Merchant_remembers_last_two_trips()
    {
        var m = RepresentativeMerchant.Create(WorldId.New(), A, Money.Zero, new Mass(1), new Volume(1), 10, riskAversion: 30).Value;
        m.RiskAversion.Should().Be(30);
        m.Remember(new TripRecord(A, B, "Wagon", 10, 1, 100));
        m.Remember(new TripRecord(A, B, "Longboat", 20, 1, 200));
        m.Remember(new TripRecord(A, B, "Airship", 30, 1, 300));
        m.Trips.Select(t => (t.Slot, t.VehicleClass)).Should().Equal((0, "Airship"), (1, "Longboat"));
        RepresentativeMerchant.Create(WorldId.New(), A, Money.Zero, new Mass(1), new Volume(1), 10, riskAversion: 101)
            .FirstError.Code.Should().Be("merchant.risk.range");
    }

    [Test]
    public void Caravan_returns_home_after_as_long_again()
    {
        var c = Caravan.Create(WorldId.New(), MerchantId.New(), A, B, GoodId.New(), 1, Money.Zero, "Wagon",
            [new CaravanLeg(0, RouteId.New(), TravelDirection.AtoB, new Tick(100), new Tick(400), 9_000)]).Value;
        c.ReturnTick.Should().Be(new Tick(700));
        c.LengthMetres.Should().Be(9_000);
        var early = () => c.MarkHomeReported();
        early.Should().Throw<InvalidOperationException>();
        c.MarkDelivered();
        c.MarkHomeReported();
        c.HomeReported.Should().BeTrue();
    }
}

public class PortAndVehicleTests
{
    [Test]
    public void Vehicle_defaults_carry_cost_and_capacity()
    {
        var d = VehicleClass.Defaults(WorldId.New()).ToDictionary(v => v.Name);
        (d["HandCart"].MaxMassGrams, d["HandCart"].MaxVolumeCm3, d["HandCart"].VolumetricDivisor, d["HandCart"].RatePct).Should().Be((200_000L, 500_000L, 4_000L, 100));
        (d["Wagon"].MaxMassGrams, d["Wagon"].MaxVolumeCm3, d["Wagon"].VolumetricDivisor, d["Wagon"].RatePct).Should().Be((1_000_000L, 3_000_000L, 5_000L, 100));
        (d["Longboat"].MaxMassGrams, d["Longboat"].MaxVolumeCm3, d["Longboat"].VolumetricDivisor, d["Longboat"].RatePct).Should().Be((3_000_000L, 10_000_000L, 6_000L, 60));
        (d["MerchantVessel"].MaxMassGrams, d["MerchantVessel"].MaxVolumeCm3, d["MerchantVessel"].VolumetricDivisor, d["MerchantVessel"].RatePct).Should().Be((100_000_000L, 200_000_000L, 10_000L, 30));
        (d["Airship"].MaxMassGrams, d["Airship"].MaxVolumeCm3, d["Airship"].VolumetricDivisor, d["Airship"].RatePct).Should().Be((500_000L, 1_500_000L, 2_000L, 400));
    }

    [Test]
    public void Ports_offer_boats_by_kind()
    {
        var d = VehicleClass.Defaults(WorldId.New()).ToDictionary(v => v.Name);
        d["Longboat"].OfferedAt(PortKind.River).Should().BeTrue();
        d["MerchantVessel"].OfferedAt(PortKind.River).Should().BeFalse();
        d["MerchantVessel"].OfferedAt(PortKind.Coastal).Should().BeTrue();
        d["Longboat"].OfferedAt(PortKind.None).Should().BeFalse();
        d["Wagon"].OfferedAt(PortKind.None).Should().BeTrue();
        d["Airship"].OfferedAt(PortKind.None).Should().BeTrue();
    }

    [Test]
    public void Only_settlement_nodes_are_ports()
        => RouteNode.Create(WorldId.New(), RouteNodeKind.Junction, 1, null, null, PortKind.River).FirstError.Code
            .Should().Be("routenode.port.settlementOnly");
}
