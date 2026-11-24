using FluentAssertions;
using WorldEcon.Application.Globe;
using WorldEcon.Application.Routes;
using WorldEcon.Domain.Geography;
using WorldEcon.Gui.ViewModels;
using WorldEcon.Rendering;

namespace WorldEcon.Gui.Tests.Unit;

/// <summary>Route and node lookups of <see cref="WorldSession"/>, rename application, the current tick and DataChanged.</summary>
public class WorldSessionGraphTests
{
    /// <summary>The test globe with a settlement, an unnamed junction and a site joined by two land routes along a tile path.</summary>
    internal static (LoadedGlobe Loaded, Guid Junction, Guid Site, Guid RouteToJunction, Guid RouteToSite) Graph()
    {
        var loaded = TestWorlds.Loaded;
        var grid = loaded.TileGrid;
        var start = loaded.Settlements[0].TileId!.Value;
        Span<int> nb = stackalloc int[6];
        var path = new List<int> { start };
        for (var i = 0; i < 6; i++)
        {
            var n = grid.Neighbours(path[^1], nb);
            path.Add(nb[..n].ToArray().First(t => !path.Contains(t)));
        }

        Guid junction = Guid.NewGuid(), site = Guid.NewGuid(), r1 = Guid.NewGuid(), r2 = Guid.NewGuid(), town = Guid.NewGuid();
        var routes = new[]
        {
            new GlobeRoute(r1, RouteCategory.Land, RouteSurface.DirtRoad, 1, 99, 15_000, 3, "North Road", "Testburg", $"Junction @ tile {path[3]}", path[..4])
                { NodeAId = town, NodeBId = junction },
            new GlobeRoute(r2, RouteCategory.Land, RouteSurface.Track, 1, 99, 15_000, 1, "North Road", $"Junction @ tile {path[3]}", "Old Quarry", path[3..])
                { NodeAId = junction, NodeBId = site },
        };
        var nodes = new[]
        {
            new GlobeNode(junction, RouteNodeKind.Junction, path[3], null, [r1, r2]),
            new GlobeNode(site, RouteNodeKind.Site, path[^1], "Old Quarry", [r2]),
        };
        return (loaded with { Routes = routes, Nodes = nodes }, junction, site, r1, r2);
    }

    [Test]
    public void RouteLine_And_Route_ById()
    {
        var (loaded, _, _, r1, _) = Graph();
        using var s = new WorldSession(loaded, path: null);
        s.RouteLine(r1)!.RouteId.Should().Be(r1);
        s.RouteLine(r1)!.Tiles.Should().Equal(loaded.Routes[0].Tiles);
        s.Route(r1)!.RoadName.Should().Be("North Road");
        s.RouteLine(Guid.NewGuid()).Should().BeNull();
    }

    [Test]
    public void NodeMarkers_KindAndLabel()
    {
        var (loaded, junction, site, _, _) = Graph();
        using var s = new WorldSession(loaded, path: null);
        s.NodeMarkers.Should().HaveCount(2);
        s.NodeMarkers.Single(m => m.Id == junction).Should().BeEquivalentTo(new { Kind = MapNodeKind.Junction, Label = (string?)null, TileId = loaded.Nodes[0].TileId });
        s.NodeMarkers.Single(m => m.Id == site).Label.Should().Be("Old Quarry");
        s.NodeOnTile(loaded.Nodes[0].TileId)!.Id.Should().Be(junction);
        s.Node(site)!.Name.Should().Be("Old Quarry");
    }

    [Test]
    public void ApplyRename_UpdatesNodeMarkerAndRouteEndNames()
    {
        var (loaded, junction, _, r1, r2) = Graph();
        using var s = new WorldSession(loaded, path: null);
        s.ApplyRename(new RenamedNode(junction, RouteNodeKind.Junction, loaded.Nodes[0].TileId, "Old Ford"));
        s.Node(junction)!.Name.Should().Be("Old Ford");
        s.NodeMarkers.Single(m => m.Id == junction).Label.Should().Be("Old Ford");
        s.Route(r1)!.NodeB.Should().Be("Old Ford");
        s.Route(r2)!.NodeA.Should().Be("Old Ford");

        s.ApplyRename(new RenamedNode(junction, RouteNodeKind.Junction, loaded.Nodes[0].TileId, null));
        s.Route(r1)!.NodeB.Should().Be($"Junction @ tile {loaded.Nodes[0].TileId}");
        s.NodeMarkers.Single(m => m.Id == junction).Label.Should().BeNull();
    }

    [Test]
    public void CurrentTick_StartsAtWorldTick_DataChangedRaisedOnce()
    {
        using var s = TestWorlds.Session();
        s.CurrentTick.Should().Be(TestWorlds.Loaded.World.CurrentTick.Value);
        var raised = 0;
        s.DataChanged += (_, _) => raised++;
        s.RaiseDataChanged();
        raised.Should().Be(1);
    }
}
