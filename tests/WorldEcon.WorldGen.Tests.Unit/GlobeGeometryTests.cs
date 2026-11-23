using FluentAssertions;
using WorldEcon.WorldGen.Grid;

namespace WorldEcon.WorldGen.Tests.Unit;

public class GlobeGeometryTests
{
    [Test]
    public void Centres_are_unit_and_corners_are_unit()
    {
        var g = GlobeGrid.Build(8);
        var geo = GlobeGeometry.Compute(g);
        for (int c = 0; c < g.CellCount; c++) geo.Centre(c).Length.Should().BeApproximately(1, 1e-12);
        for (int t = 0; t < g.TriangleCount; t++) geo.Corner(t).Length.Should().BeApproximately(1, 1e-12);
    }

    [Test]
    public void Every_small_triangle_is_counter_clockwise_seen_from_outside()
    {
        var g = GlobeGrid.Build(8);
        var geo = GlobeGeometry.Compute(g);
        for (int t = 0; t < g.TriangleCount; t++)
        {
            var (a, b, c) = g.Triangle(t);
            Vector3d A = geo.Centre(a), B = geo.Centre(b), C = geo.Centre(c);
            Vector3d.Dot(Vector3d.Cross(B - A, C - A), A + B + C).Should().BePositive($"triangle {t}");
        }
    }

    [Test]
    public void LatLon_roundtrips_through_NearestCell()
    {
        var g = GlobeGrid.Build(16);
        var geo = GlobeGeometry.Compute(g);
        for (int c = 0; c < g.CellCount; c++)
        {
            var (lat, lon) = geo.LatLon(c);
            geo.NearestCell(lat, lon).Should().Be(c);
        }
    }

    [Test]
    [Arguments(90.0, 0.0)]
    [Arguments(-90.0, 0.0)]
    [Arguments(0.0, 180.0)]
    [Arguments(0.0, -180.0)]
    [Arguments(45.0, 179.999)]
    public void NearestCell_handles_poles_and_antimeridian(double lat, double lon)
    {
        var g = GlobeGrid.Build(16);
        var geo = GlobeGeometry.Compute(g);
        int cell = geo.NearestCell(lat, lon);
        cell.Should().BeInRange(0, g.CellCount - 1);

        // Within one cell spacing: the unit-sphere angle to the query point is at most the
        // icosahedron edge angle (~63.4 degrees) divided by m.
        double la = lat * Math.PI / 180, lo = lon * Math.PI / 180;
        var q = new Vector3d(Math.Cos(la) * Math.Sin(lo), Math.Sin(la), Math.Cos(la) * Math.Cos(lo));
        var v = geo.Centre(cell);
        double angle = Math.Atan2(Vector3d.Cross(v, q).Length, Vector3d.Dot(v, q));
        angle.Should().BeLessThan(1.1071487177940904 / g.Frequency);
    }

    [Test]
    public void Cell_areas_sum_to_sphere_area()
    {
        var g = GlobeGrid.Build(8);
        var geo = GlobeGeometry.Compute(g);
        const double r = 6371;
        double sum = 0;
        for (int c = 0; c < g.CellCount; c++) sum += geo.CellAreaKm2(c, r);
        sum.Should().BeApproximately(4 * Math.PI * r * r, 4 * Math.PI * r * r * 0.001);
    }
}
