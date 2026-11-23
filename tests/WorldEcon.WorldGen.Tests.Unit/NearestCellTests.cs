using FluentAssertions;
using WorldEcon.Simulation.Random;
using WorldEcon.WorldGen.Grid;

namespace WorldEcon.WorldGen.Tests.Unit;

public class NearestCellTests
{
    [Test]
    [Arguments(16)]
    [Arguments(64)]
    public void NearestCell_matches_brute_force_argmax_of_dot_product(int m)
    {
        var grid = GlobeGrid.Build(m);
        var geo = GlobeGeometry.Compute(grid);
        var rng = new Xoshiro256StarStar(12345);
        var mismatches = new List<string>();
        for (int i = 0; i < 500; i++)
        {
            // Uniform on the sphere: lon uniform, sin(lat) uniform.
            double lon = rng.NextInt(360_000) / 1000.0 - 180;
            double lat = Math.Asin(rng.NextInt(2_000_001) / 1_000_000.0 - 1) * 180 / Math.PI;
            double la = lat * Math.PI / 180, lo = lon * Math.PI / 180;
            var t = new Vector3d(Math.Cos(la) * Math.Sin(lo), Math.Sin(la), Math.Cos(la) * Math.Cos(lo));
            int best = 0;
            double bestDot = double.NegativeInfinity;
            for (int c = 0; c < grid.CellCount; c++)
            {
                double d = Vector3d.Dot(geo.Centre(c), t);
                if (d > bestDot) { bestDot = d; best = c; }
            }
            int got = geo.NearestCell(lat, lon);
            if (got != best) mismatches.Add($"({lat:F3},{lon:F3}) got {got} want {best}");
        }
        mismatches.Should().BeEmpty($"m={m}: {mismatches.Count} mismatches");
    }
}
