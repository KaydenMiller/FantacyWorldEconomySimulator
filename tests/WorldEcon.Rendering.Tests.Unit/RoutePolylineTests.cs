using FluentAssertions;
using WorldEcon.Rendering;
using WorldEcon.WorldGen.Grid;

namespace WorldEcon.Rendering.Tests.Unit;

public class RoutePolylineTests
{
    private static Vector3d Dir(double lonDeg) => new(Math.Sin(lonDeg * Math.PI / 180), 0, Math.Cos(lonDeg * Math.PI / 180));

    private static double AngleDeg(Vector3d a, Vector3d b) => Math.Acos(Math.Clamp(Vector3d.Dot(a.Normalize(), b.Normalize()), -1, 1)) * 180 / Math.PI;

    [Test]
    public void PointAt_0_and_1_AreEnds()
    {
        var p = new RoutePolyline([Dir(0), Dir(1), Dir(3)]);
        AngleDeg(p.PointAt(0), Dir(0)).Should().BeLessThan(1e-9);
        AngleDeg(p.PointAt(1), Dir(3)).Should().BeLessThan(1e-9);
    }

    [Test]
    public void PointAt_Half_IsMidLength_UnequalSegments()
    {
        // Segments of 1° and 3°: half of the 4° total is 1° into the second segment, at 2°.
        var p = new RoutePolyline([Dir(0), Dir(1), Dir(4)]);
        AngleDeg(p.PointAt(0.5), Dir(2)).Should().BeLessThan(1e-6);
        AngleDeg(p.PointAt(0.125), Dir(0.5)).Should().BeLessThan(1e-6);
    }

    [Test]
    public void PointAt_ClampsOutOfRange()
    {
        var p = new RoutePolyline([Dir(0), Dir(2)]);
        AngleDeg(p.PointAt(-0.5), Dir(0)).Should().BeLessThan(1e-9);
        AngleDeg(p.PointAt(7), Dir(2)).Should().BeLessThan(1e-9);
        AngleDeg(p.PointAt(double.NaN), Dir(0)).Should().BeLessThan(1e-9);
    }

    [Test]
    public void PointAt_ZeroLengthPolyline_NoNaN()
    {
        var p = new RoutePolyline([Dir(5), Dir(5), Dir(5)]);
        var v = p.PointAt(0.5);
        double.IsNaN(v.X + v.Y + v.Z).Should().BeFalse();
        AngleDeg(v, Dir(5)).Should().BeLessThan(1e-9);
        var d = p.DirectionAt(0.5);
        double.IsNaN(d.X + d.Y + d.Z).Should().BeFalse();
    }

    [Test]
    public void DirectionAt_PointsTowardBEnd_TangentToSphere()
    {
        var p = new RoutePolyline([Dir(0), Dir(10)]);
        var at = p.PointAt(0.3);
        var d = p.DirectionAt(0.3);
        Math.Abs(d.Length - 1).Should().BeLessThan(1e-9);
        Math.Abs(Vector3d.Dot(d, at.Normalize())).Should().BeLessThan(1e-6);
        Vector3d.Dot(d, Dir(10) - Dir(0)).Should().BePositive();
    }

    [Test]
    public void SinglePoint_Polyline_ReturnsThePoint()
    {
        var p = new RoutePolyline([Dir(7)]);
        AngleDeg(p.PointAt(0.4), Dir(7)).Should().BeLessThan(1e-9);
    }
}
