using System.Numerics;
using FluentAssertions;
using SkiaSharp;
using WorldEcon.WorldGen.Grid;
using WorldEcon.WorldGen.Pipeline;

namespace WorldEcon.Rendering.Tests.Unit;

/// <summary>Caravans, junctions and sites in the overlay, and picking them by the positions drawn.</summary>
public class ObjectOverlayTests
{
    private static readonly WorldGenSettings Settings = new() { Grid = new GridSettings { Frequency = 8, RegionMultiplier = 3, TileMultiplier = 4 } };
    private static readonly Lazy<GeneratedWorld> Gen = new(() => WorldGenerator.Generate(42, Settings));
    private static readonly Vector3d Centre = new Vector3d(0.1, 0.05, 1).Normalize();

    private static MapWorld World() => MapWorld.FromGenerated(Gen.Value, Settings, withImagery: false);

    private static MapViewState Detail(MapWorld w) => w.View(Centre, 1, 800, 600).WithScale(w.ScaleForTilePx(26));

    private static MapViewState Globe(MapWorld w) { var v = w.View(Centre, 1, 800, 600); return v.WithScale(v.MinScale); }

    private static MapDrawOptions Options() => new() { Settlements = false, Equator = false, Arrows = false, Labels = false };

    private static int TileAt(MapWorld w, Vector3d p) => w.Tiles!.Nearest(p);

    private static MapCaravan Caravan(MapWorld w, MapRouteKind kind = MapRouteKind.Land, Guid? id = null)
    {
        var p = w.Tiles!.Centre(TileAt(w, Centre));
        return new MapCaravan(id ?? Guid.NewGuid(), p, Vector3d.Cross(new Vector3d(0, 1, 0), p).Normalize(), kind);
    }

    private static (float X, float Y) Screen(MapViewState v, Vector3d p)
    {
        v.ProjectFast(new Vector3((float)p.X, (float)p.Y, (float)p.Z), out var x, out var y).Should().BeTrue();
        return (x, y);
    }

    private static (int Count, MapRenderer Renderer) Draw(MapWorld w, MapViewState view, MapDrawOptions o, SKColor colour)
    {
        var r = new MapRenderer(w);
        using var bmp = new SKBitmap(new SKImageInfo((int)view.Width, (int)view.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bmp))
        {
            canvas.Clear(SKColors.Transparent);
            r.DrawOverlay(canvas, view, o);
        }

        var n = 0;
        for (var y = 0; y < bmp.Height; y++)
            for (var x = 0; x < bmp.Width; x++)
            {
                var p = bmp.GetPixel(x, y);
                if (p.Alpha >= 200 && Math.Abs(p.Red - colour.Red) <= 2 && Math.Abs(p.Green - colour.Green) <= 2 && Math.Abs(p.Blue - colour.Blue) <= 2) n++;
            }
        return (n, r);
    }

    private static SKColor Opaque(SKColor c) => c.WithAlpha(255);

    [Test]
    public void Caravan_Drawn_AndPicked_AtItsPosition()
    {
        var w = World();
        var view = Detail(w);
        var c = Caravan(w);
        var (n, r) = Draw(w, view, Options() with { CaravanMarkers = [c] }, MapRenderer.LandRouteColor);
        using (r)
        {
            n.Should().BeGreaterThan(10);
            var (x, y) = Screen(view, c.Position);
            r.PickObjects(x, y).Should().Equal(new MapObject(MapObjectKind.Caravan, c.Id));
        }
    }

    [Test]
    public void Caravans_Within6px_Merge_PickReturnsAll()
    {
        var w = World();
        var view = Detail(w);
        var a = Caravan(w);
        var b = Caravan(w);
        var (_, r) = Draw(w, view, Options() with { CaravanMarkers = [a, b] }, MapRenderer.LandRouteColor);
        using (r)
        {
            var (x, y) = Screen(view, a.Position);
            r.PickObjects(x, y).Should().BeEquivalentTo([new MapObject(MapObjectKind.Caravan, a.Id), new MapObject(MapObjectKind.Caravan, b.Id)]);
        }
    }

    [Test]
    public void PickObjects_Radius_7Hits_9Misses()
    {
        var w = World();
        var view = Detail(w);
        var c = Caravan(w);
        var (_, r) = Draw(w, view, Options() with { CaravanMarkers = [c] }, MapRenderer.LandRouteColor);
        using (r)
        {
            var (x, y) = Screen(view, c.Position);
            r.PickObjects(x + 7, y).Should().ContainSingle();
            r.PickObjects(x + 9, y).Should().BeEmpty();
        }
    }

    [Test]
    public void Caravan_HiddenWhenItsCategoryIsOff_OrRoutesOff()
    {
        var w = World();
        var view = Detail(w);
        var c = Caravan(w, MapRouteKind.Sea);
        var (n, r) = Draw(w, view, Options() with { CaravanMarkers = [c], SeaRoutes = false }, Opaque(MapRenderer.SeaRouteColor));
        using (r)
        {
            n.Should().Be(0);
            var (x, y) = Screen(view, c.Position);
            r.PickObjects(x, y).Should().BeEmpty();
        }

        var (n2, r2) = Draw(w, view, Options() with { CaravanMarkers = [Caravan(w)], Routes = false }, MapRenderer.LandRouteColor);
        using (r2) n2.Should().Be(0);
        var (n3, r3) = Draw(w, view, Options() with { CaravanMarkers = [Caravan(w)], Caravans = false }, MapRenderer.LandRouteColor);
        using (r3) n3.Should().Be(0);
    }

    [Test]
    public void Defaults_CaravansOn_JunctionsOff_SitesOn()
    {
        var o = new MapDrawOptions();
        (o.Caravans, o.Junctions, o.Sites).Should().Be((true, false, true));
    }

    [Test]
    public void Junction_DrawnAtDetail_NotAtGlobe()
    {
        var w = World();
        var tile = TileAt(w, Centre);
        var node = new MapNode(Guid.NewGuid(), MapNodeKind.Junction, w.GlobeTiles.Nearest(w.Tiles!.Centre(tile)), tile, null);
        var o = Options() with { NodeMarkers = [node], Junctions = true };
        var (atDetail, r1) = Draw(w, Detail(w), o, MapRenderer.LandRouteColor);
        var (atGlobe, r2) = Draw(w, Globe(w), o, MapRenderer.LandRouteColor);
        using (r1) using (r2)
        {
            atDetail.Should().BeGreaterThan(5);
            atGlobe.Should().Be(0);
            var (x, y) = Screen(Detail(w), w.Tiles!.Centre(tile));
            r1.PickObjects(x, y).Should().Equal(new MapObject(MapObjectKind.Node, node.Id));
        }

        var (off, r3) = Draw(w, Detail(w), o with { Junctions = false }, MapRenderer.LandRouteColor);
        using (r3) off.Should().Be(0);
    }

    [Test]
    public void Site_DrawnAtGlobe()
    {
        var w = World();
        var tile = TileAt(w, Centre);
        var node = new MapNode(Guid.NewGuid(), MapNodeKind.Site, w.GlobeTiles.Nearest(w.Tiles!.Centre(tile)), tile, "Old Quarry");
        var (n, r) = Draw(w, Globe(w), Options() with { NodeMarkers = [node] }, MapRenderer.SiteColor);
        using (r) n.Should().BeGreaterThan(5);
        var (off, r2) = Draw(w, Globe(w), Options() with { NodeMarkers = [node], Sites = false }, MapRenderer.SiteColor);
        using (r2) off.Should().Be(0);
    }

    [Test]
    public void PickObjects_Order_CaravanBeforeSettlementBeforeNode()
    {
        var w = World();
        var view = Detail(w);
        var tile = TileAt(w, Centre);
        var cell = w.GlobeTiles.Nearest(w.Tiles!.Centre(tile));
        var town = new MapMarker(cell, tile, "Town", Id: Guid.NewGuid());
        var site = new MapNode(Guid.NewGuid(), MapNodeKind.Site, cell, tile, "Quarry");
        var c = Caravan(w);
        var o = Options() with { Settlements = true, Markers = [town], NodeMarkers = [site] };
        var (x, y) = Screen(view, w.Tiles!.Centre(tile));

        var (_, r1) = Draw(w, view, o with { CaravanMarkers = [c] }, SKColors.Yellow);
        using (r1) r1.PickObjects(x, y).Should().Equal(new MapObject(MapObjectKind.Caravan, c.Id));
        var (_, r2) = Draw(w, view, o, SKColors.Yellow);
        using (r2) r2.PickObjects(x, y).Should().Equal(new MapObject(MapObjectKind.Settlement, town.Id));
        var (_, r3) = Draw(w, view, o with { Settlements = false }, SKColors.Yellow);
        using (r3) r3.PickObjects(x, y).Should().Equal(new MapObject(MapObjectKind.Node, site.Id));
    }

    [Test]
    public void PickObjects_WithBorders_UsesDrawnClusterPosition()
    {
        var w = World();
        var view = w.View(Centre, 1, 800, 600).WithScale(w.ScaleForRegionPx(45));
        var c = Caravan(w);
        var (_, r) = Draw(w, view, Options() with { CaravanMarkers = [c], Borders = true }, MapRenderer.LandRouteColor);
        using (r)
        {
            var hits = r.LastObjectPoints;
            hits.Should().ContainSingle();
            r.PickObjects(hits[0].X, hits[0].Y).Should().Equal(new MapObject(MapObjectKind.Caravan, c.Id));
        }
    }

    [Test]
    public void PickObjects_OfKind_FindsSettlementUnderACaravan()
    {
        var w = World();
        var view = Detail(w);
        var tile = TileAt(w, Centre);
        var cell = w.GlobeTiles.Nearest(w.Tiles!.Centre(tile));
        var town = new MapMarker(cell, tile, "Town", Id: Guid.NewGuid());
        var c = Caravan(w);
        var (_, r) = Draw(w, view, Options() with { Settlements = true, Markers = [town], CaravanMarkers = [c] }, SKColors.Yellow);
        using (r)
        {
            var (x, y) = Screen(view, w.Tiles!.Centre(tile));
            r.PickObjects(x, y).Should().Equal(new MapObject(MapObjectKind.Caravan, c.Id));
            r.PickObjects(x, y, MapObjectKind.Settlement).Should().Equal(new MapObject(MapObjectKind.Settlement, town.Id));
            r.PickObjects(x, y, MapObjectKind.Node).Should().BeEmpty();
        }
    }
}
