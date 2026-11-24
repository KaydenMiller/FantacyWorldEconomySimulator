using FluentAssertions;
using WorldEcon.Application.Queries;
using WorldEcon.Domain.Geography;
using WorldEcon.Gui.ViewModels;
using WorldEcon.Rendering;
using WorldEcon.WorldGen.Grid;

namespace WorldEcon.Gui.Tests.Unit;

public class CaravanLayerTests
{
    private static CaravanOnMap Caravan(params CaravanLegInfo[] legs) => new(Guid.NewGuid(), Guid.NewGuid(), "Testburg", Guid.NewGuid(), "A",
        Guid.NewGuid(), "B", Guid.NewGuid(), "Grain", 10, 50, "Wagon", legs[0].EnterTick, legs[^1].ExitTick,
        2 * legs[^1].ExitTick - legs[0].EnterTick, legs);

    private static CaravanLegInfo Leg(int seq, Guid route, TravelDirection dir, long enter, long exit, long length = 15_000, long offset = 0)
        => new(seq, route, dir, enter, exit, length, offset);

    private static double AngleDeg(Vector3d a, Vector3d b) => Math.Acos(Math.Clamp(Vector3d.Dot(a.Normalize(), b.Normalize()), -1, 1)) * 180 / Math.PI;

    [Test]
    public void SetTick_PlacesOnRouteOnly()
    {
        var (loaded, _, _, r1, _) = WorldSessionGraphTests.Graph();
        using var s = new WorldSession(loaded, path: null);
        var layer = new CaravanLayer(s);
        var c = Caravan(Leg(0, r1, TravelDirection.AtoB, 100, 200));
        layer.Load(new CaravanPositions(150, [c]));
        layer.OnRoute.Should().ContainSingle().Which.Id.Should().Be(c.Id);
        layer.SetTick(99);
        layer.OnRoute.Should().BeEmpty();
        layer.SetTick(200);
        layer.OnRoute.Should().BeEmpty();
    }

    [Test]
    public void SetTick_MidLeg_IsMidLine_AndBtoA_Reverses()
    {
        var (loaded, _, _, r1, _) = WorldSessionGraphTests.Graph();
        using var s = new WorldSession(loaded, path: null);
        var line = new RoutePolyline(loaded.Routes[0].Tiles.Select(t => loaded.TileGrid.Centre(t)).ToArray());
        var layer = new CaravanLayer(s);
        var ab = Caravan(Leg(0, r1, TravelDirection.AtoB, 0, 100));
        var ba = Caravan(Leg(0, r1, TravelDirection.BtoA, 0, 100));
        layer.Load(new CaravanPositions(25, [ab, ba]));
        var pAb = layer.OnRoute.Single(x => x.Id == ab.Id);
        var pBa = layer.OnRoute.Single(x => x.Id == ba.Id);
        AngleDeg(pAb.Position, line.PointAt(0.25)).Should().BeLessThan(1e-6);
        AngleDeg(pBa.Position, line.PointAt(0.75)).Should().BeLessThan(1e-6);
        Vector3d.Dot(pAb.Heading, line.DirectionAt(0.25)).Should().BePositive();
        Vector3d.Dot(pBa.Heading, line.DirectionAt(0.75)).Should().BeNegative();
        pAb.Kind.Should().Be(MapRouteKind.Land);
    }

    [Test]
    public void SetTick_SplitLeg_UsesOffset()
    {
        var (loaded, _, _, r1, _) = WorldSessionGraphTests.Graph();
        using var s = new WorldSession(loaded, path: null);
        var line = new RoutePolyline(loaded.Routes[0].Tiles.Select(t => loaded.TileGrid.Centre(t)).ToArray());
        var layer = new CaravanLayer(s);
        // The remainder of a leg split by a route upgrade: starts 7.5 km along, covers the last 7.5 km.
        var c = Caravan(Leg(0, r1, TravelDirection.AtoB, 0, 50, 7_500), Leg(1, r1, TravelDirection.AtoB, 50, 100, 7_500, 7_500));
        layer.Load(new CaravanPositions(50, [c]));
        AngleDeg(layer.OnRoute.Single().Position, line.PointAt(0.5)).Should().BeLessThan(1e-6);
    }

    [Test]
    public void SetTick_RouteWithoutLine_Skipped_ZeroLength_NoNaN()
    {
        var (loaded, _, _, r1, _) = WorldSessionGraphTests.Graph();
        loaded = loaded with { Routes = [loaded.Routes[0] with { LengthMetres = 0 }, loaded.Routes[1]] };
        using var s = new WorldSession(loaded, path: null);
        var layer = new CaravanLayer(s);
        var unknown = Caravan(Leg(0, Guid.NewGuid(), TravelDirection.AtoB, 0, 100));
        var zero = Caravan(Leg(0, r1, TravelDirection.AtoB, 0, 100, 0));
        layer.Load(new CaravanPositions(50, [unknown, zero]));
        layer.OnRoute.Should().ContainSingle().Which.Id.Should().Be(zero.Id);
        var p = layer.OnRoute.Single().Position;
        double.IsNaN(p.X + p.Y + p.Z).Should().BeFalse();
        layer.Caravan(unknown.Id).Should().Be(unknown);
    }

    [Test]
    public void Load_SetsSessionTick_SetTick_RaisesChangedOnce()
    {
        var (loaded, _, _, r1, _) = WorldSessionGraphTests.Graph();
        using var s = new WorldSession(loaded, path: null);
        var layer = new CaravanLayer(s);
        var raised = 0;
        layer.Changed += (_, _) => raised++;
        layer.Load(new CaravanPositions(777, [Caravan(Leg(0, r1, TravelDirection.AtoB, 0, 1_000))]));
        s.CurrentTick.Should().Be(777);
        raised.Should().Be(1);
        layer.SetTick(800);
        raised.Should().Be(2);
    }

    [Test]
    public void SetTick_NoAllocationsPerCaravan()
    {
        var (loaded, _, _, r1, r2) = WorldSessionGraphTests.Graph();
        using var s = new WorldSession(loaded, path: null);
        var layer = new CaravanLayer(s);
        var caravans = Enumerable.Range(0, 1_000).Select(i => Caravan(
            Leg(0, r1, TravelDirection.AtoB, i, i + 500), Leg(1, r2, TravelDirection.AtoB, i + 500, i + 1_000))).ToArray();
        layer.Load(new CaravanPositions(0, caravans));
        layer.SetTick(600); // warm-up: route polylines cached
        var before = GC.GetAllocatedBytesForCurrentThread();
        layer.SetTick(700);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        allocated.Should().BeLessThan(1_024);
        layer.OnRoute.Should().HaveCount(701); // caravan i leaves at tick i
    }
}
