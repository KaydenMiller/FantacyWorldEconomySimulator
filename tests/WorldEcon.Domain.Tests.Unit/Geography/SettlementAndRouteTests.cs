using FluentAssertions;
using WorldEcon.Domain.Geography;
using WorldEcon.SharedKernel.Domain;

namespace WorldEcon.Domain.Tests.Unit.Geography;

public class SettlementAndRouteTests
{
    [Test]
    public void Settlement_Create_SetsFields_DefaultProvenanceAuthored()
    {
        var s = Settlement.Create(WorldId.New(), PoliticalRegionId.New(), "Hammerfell",
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
        => Settlement.Create(WorldId.New(), PoliticalRegionId.New(), "X", SettlementType.Town, 0, 0, -1)
            .IsError.Should().BeTrue();
}
