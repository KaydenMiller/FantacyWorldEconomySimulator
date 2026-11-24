using FluentAssertions;
using WorldEcon.Application.Globe;
using WorldEcon.Domain.Geography;
using WorldEcon.TestFixtures;

namespace WorldEcon.Application.Tests.Unit;

public class GlobeNodesLoadTests
{
    [Test]
    public async Task LoadNodes_ReturnsJunctionsAndSites_WithRouteIds()
    {
        using var w = await EconomyTestWorld.CreateAsync();
        await using var ctx = EconomyTestWorld.Ctx(w.Path);
        var nodes = await GlobeWorldLoader.LoadNodesAsync(ctx, w.World.Id);
        nodes.Should().ContainSingle();
        var j = nodes[0];
        j.Should().BeEquivalentTo(new { Id = w.Junction.Id.Value, Kind = RouteNodeKind.Junction, TileId = w.TileJ, Name = (string?)null });
        j.RouteIds.Should().BeEquivalentTo([w.RouteAJ.Id.Value, w.RouteJB.Id.Value]);
    }

    [Test]
    public async Task LoadRoutes_UnnamedJunctionEnd_NamedByTile_AndNodeIds()
    {
        using var w = await EconomyTestWorld.CreateAsync();
        await using var ctx = EconomyTestWorld.Ctx(w.Path);
        var routes = await GlobeWorldLoader.LoadRoutesAsync(ctx, w.World.Id);
        var aj = routes.Single(r => r.Id == w.RouteAJ.Id.Value);
        (aj.NodeA, aj.NodeB).Should().Be(("Ashford", $"Junction @ tile {w.TileJ}"));
        (aj.NodeAId, aj.NodeBId).Should().Be((w.NodeA.Id.Value, w.Junction.Id.Value));
        aj.RoadName.Should().Be("King's Way");
    }
}
