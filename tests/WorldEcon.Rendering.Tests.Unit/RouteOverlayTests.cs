using FluentAssertions;
using SkiaSharp;
using WorldEcon.WorldGen.Grid;
using WorldEcon.WorldGen.Pipeline;
using WorldEcon.WorldGen.Roads;

namespace WorldEcon.Rendering.Tests.Unit;

/// <summary>Route lines in the overlay: each category in its own colour, hidden behind the globe and by the toggle.</summary>
public class RouteOverlayTests
{
    private static readonly WorldGenSettings Settings = new() { Grid = new GridSettings { Frequency = 8, RegionMultiplier = 3, TileMultiplier = 4 } };
    private static readonly Lazy<GeneratedWorld> Gen = new(() => WorldGenerator.Generate(42, Settings));

    private static (MapWorld World, int[] Path) Setup()
    {
        var world = MapWorld.FromGenerated(Gen.Value, Settings, withImagery: false);
        var tiles = world.Tiles!;
        var from = tiles.Nearest(new Vector3d(0, 0, 1));
        var to = tiles.Nearest(new Vector3d(0.3, 0.1, 1).Normalize());
        return (world, RoadSearch.GreatCircle(tiles, from, to));
    }

    private static int Count(MapWorld world, MapViewState view, MapDrawOptions options, SKColor colour)
    {
        using var r = new MapRenderer(world);
        using var bmp = new SKBitmap(new SKImageInfo((int)view.Width, (int)view.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bmp))
        {
            canvas.Clear(SKColors.Transparent);
            r.DrawOverlay(canvas, view, options);
        }

        // Same hue as the colour (alpha ignored: sea lines are translucent and overlap themselves where dashes meet).
        var n = 0;
        for (var y = 0; y < bmp.Height; y++)
            for (var x = 0; x < bmp.Width; x++)
            {
                var p = bmp.GetPixel(x, y);
                if (p.Alpha >= 40 && Math.Abs(p.Red - colour.Red) <= 2 && Math.Abs(p.Green - colour.Green) <= 2 && Math.Abs(p.Blue - colour.Blue) <= 2) n++;
            }
        return n;
    }

    private static MapDrawOptions Options(params MapRoute[] routes) => new()
    {
        Settlements = false, Equator = false, Arrows = false, RouteLines = routes,
    };

    [Test]
    public void Each_category_draws_in_its_colour()
    {
        var (world, path) = Setup();
        var view = world.View(new Vector3d(0.15, 0.05, 1).Normalize(), 1200, 800, 600);
        foreach (var (kind, colour) in new[] { (MapRouteKind.Land, MapRenderer.LandRouteColor), (MapRouteKind.Sea, MapRenderer.SeaRouteColor), (MapRouteKind.Air, MapRenderer.AirRouteColor) })
        {
            Count(world, view, Options(new MapRoute(kind, path)), colour).Should().BeGreaterThan(20, kind.ToString());
            var others = new[] { MapRenderer.LandRouteColor, MapRenderer.SeaRouteColor, MapRenderer.AirRouteColor }.Where(c => c != colour);
            foreach (var other in others)
                Count(world, view, Options(new MapRoute(kind, path)), other).Should().Be(0);
        }
    }

    [Test]
    public void Toggle_off_and_far_side_draw_nothing()
    {
        var (world, path) = Setup();
        var route = new MapRoute(MapRouteKind.Land, path);
        var front = world.View(new Vector3d(0.15, 0.05, 1).Normalize(), 1200, 800, 600);
        Count(world, front, Options(route) with { Routes = false }, MapRenderer.LandRouteColor).Should().Be(0);
        var back = world.View(new Vector3d(0, 0, -1), 300, 800, 600);
        Count(world, back, Options(route), MapRenderer.LandRouteColor).Should().Be(0);
    }

    [Test]
    public void Region_view_draws_routes_too()
    {
        var (world, path) = Setup();
        var view = world.View(world.Tiles!.Centre(path[path.Length / 2]), world.ScaleForRegionPx(40), 800, 600);
        view.Level.Should().NotBe(MapLevel.Globe);
        Count(world, view, Options(new MapRoute(MapRouteKind.Land, path)), MapRenderer.LandRouteColor).Should().BeGreaterThan(20);
    }

    [Test]
    public void Each_category_has_its_own_toggle()
    {
        var (world, path) = Setup();
        var view = world.View(new Vector3d(0.15, 0.05, 1).Normalize(), 1200, 800, 600);
        MapRoute[] all = [new(MapRouteKind.Land, path), new(MapRouteKind.Sea, path), new(MapRouteKind.Air, path)];
        var noSea = Options(all) with { SeaRoutes = false };
        Count(world, view, noSea, MapRenderer.SeaRouteColor).Should().Be(0);
        Count(world, view, noSea, MapRenderer.AirRouteColor).Should().BeGreaterThan(20);
        var onlySea = Options(all) with { LandRoutes = false, AirRoutes = false };
        Count(world, view, onlySea, MapRenderer.LandRouteColor).Should().Be(0);
        Count(world, view, onlySea, MapRenderer.AirRouteColor).Should().Be(0);
        Count(world, view, onlySea, MapRenderer.SeaRouteColor).Should().BeGreaterThan(20);
    }

    [Test]
    public void Ports_get_a_ring_and_other_markers_do_not()
    {
        var (world, path) = Setup();
        var view = world.View(world.Tiles!.Centre(path[0]), world.ScaleForRegionPx(40), 800, 600);
        var cell = GridHierarchy.Parent(world.Tiles!, world.GlobeTiles, path[0]);
        MapDrawOptions With(MapPortKind port) => new()
        {
            Equator = false, Arrows = false, Labels = false, Markers = [new MapMarker(cell, path[0], "", port)],
        };
        Count(world, view, With(MapPortKind.None), MapRenderer.PortRingColor).Should().Be(0);
        var river = Count(world, view, With(MapPortKind.River), MapRenderer.PortRingColor);
        var coastal = Count(world, view, With(MapPortKind.Coastal), MapRenderer.PortRingColor);
        river.Should().BeGreaterThan(0);
        coastal.Should().BeGreaterThan(river, "a lake or ocean port gets the thicker ring");
    }
}
