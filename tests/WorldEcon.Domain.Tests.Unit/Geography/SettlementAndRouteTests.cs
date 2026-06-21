using FluentAssertions;
using WorldEcon.Domain.Geography;
using WorldEcon.SharedKernel.Domain;

namespace WorldEcon.Domain.Tests.Unit.Geography;

public class SettlementAndRouteTests
{
    [Test]
    public void Settlement_Create_SetsFields_DefaultProvenanceAuthored()
    {
        var s = Settlement.Create(WorldId.New(), RegionId.New(), "Hammerfell",
            SettlementType.City, x: 10, y: 20, population: 50_000).Value;
        s.Name.Should().Be("Hammerfell");
        s.Type.Should().Be(SettlementType.City);
        s.X.Should().Be(10);
        s.Y.Should().Be(20);
        s.Population.Should().Be(50_000);
        s.Provenance.Should().Be(Provenance.Authored);
    }

    [Test]
    public void Settlement_Create_RejectsNegativePopulation()
        => Settlement.Create(WorldId.New(), RegionId.New(), "X", SettlementType.Town, 0, 0, -1)
            .IsError.Should().BeTrue();

    [Test]
    public void Route_Create_SetsDirectedEdge()
    {
        var from = SettlementId.New();
        var to = SettlementId.New();
        var r = Route.Create(WorldId.New(), from, to, distance: 120, Terrain.Plains, danger: 3, RouteCategory.Land).Value;
        r.FromSettlementId.Should().Be(from);
        r.ToSettlementId.Should().Be(to);
        r.Distance.Should().Be(120);
        r.Danger.Should().Be(3);
        r.Category.Should().Be(RouteCategory.Land);
    }

    [Test]
    public void Route_Create_RejectsNonPositiveDistance()
        => Route.Create(WorldId.New(), SettlementId.New(), SettlementId.New(), 0, Terrain.Plains, 0, RouteCategory.Land)
            .IsError.Should().BeTrue();

    [Test]
    public void Route_Create_RejectsSelfLoop()
    {
        var s = SettlementId.New();
        Route.Create(WorldId.New(), s, s, 10, Terrain.Plains, 0, RouteCategory.Land).IsError.Should().BeTrue();
    }
}
