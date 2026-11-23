using FluentAssertions;
using WorldEcon.WorldGen.Grid;

namespace WorldEcon.WorldGen.Tests.Unit;

public class CellChartTests
{
    [Test]
    public void Every_corner_lies_inside_the_chart_square_and_one_touches_its_edge()
    {
        var g = GlobeGrid.Build(8);
        var geo = GlobeGeometry.Compute(g);
        for (var c = 0; c < g.CellCount; c++)
        {
            var chart = CellChart.For(g, geo, c);
            var touches = false;
            foreach (var t in g.CornerTriangles(c))
            {
                var (u, v) = chart.ToPlane(geo.Corner(t));
                Math.Abs(u).Should().BeLessThanOrEqualTo(chart.HalfExtent + 1e-12);
                Math.Abs(v).Should().BeLessThanOrEqualTo(chart.HalfExtent + 1e-12);
                touches |= Math.Abs(Math.Max(Math.Abs(u), Math.Abs(v)) - chart.HalfExtent) < 1e-12;
            }

            touches.Should().BeTrue($"cell {c}");
        }
    }

    [Test]
    public void Texel_centres_round_trip_through_texel_coordinates()
    {
        var g = GlobeGrid.Build(8);
        var geo = GlobeGeometry.Compute(g);
        foreach (var c in new[] { 0, 1, 5, 100, g.CellCount - 1 })
        {
            var chart = CellChart.For(g, geo, c);
            for (var i = 0; i < 16; i += 5)
                for (var j = 0; j < 16; j += 3)
                {
                    var p = chart.TexelCentre(i, j, 16);
                    p.Length.Should().BeApproximately(1, 1e-12);
                    var (ti, tj) = chart.ToTexel(p, 16);
                    ti.Should().BeApproximately(i + 0.5, 1e-9);
                    tj.Should().BeApproximately(j + 0.5, 1e-9);
                }
        }
    }

    [Test]
    public void Charts_near_the_poles_are_well_formed()
    {
        var g = GlobeGrid.Build(8);
        var geo = GlobeGeometry.Compute(g);
        var pole = Enumerable.Range(0, g.CellCount).MaxBy(c => geo.Centre(c).Y);
        var chart = CellChart.For(g, geo, pole);
        Vector3d.Dot(chart.E1, chart.Centre).Should().BeApproximately(0, 1e-12);
        Vector3d.Dot(chart.E2, chart.Centre).Should().BeApproximately(0, 1e-12);
        chart.E1.Length.Should().BeApproximately(1, 1e-12);
        chart.E2.Length.Should().BeApproximately(1, 1e-12);
        chart.HalfExtent.Should().BeGreaterThan(0);
    }
}
