using FluentAssertions;
using WorldEcon.WorldGen.Grid;
using WorldEcon.WorldGen.Noise;

namespace WorldEcon.WorldGen.Tests.Unit;

public class SphereNoiseTests
{
    private static Vector3d Point(int i)
    {
        var rng = new WorldEcon.Simulation.Random.Xoshiro256StarStar((ulong)i + 1);
        double U() => (rng.NextULong() >> 11) / (double)(1UL << 53) - 0.5;
        return new Vector3d(U(), U(), U()).Normalize();
    }

    [Test]
    public void SphereNoise_is_deterministic_and_bounded()
    {
        var differs = false;
        for (var i = 0; i < 10_000; i++)
        {
            var p = Point(i);
            var a = SphereNoise.Sample(p, 7, 4, 3.0);
            a.Should().Be(SphereNoise.Sample(p, 7, 4, 3.0));
            a.Should().BeInRange(-1.0, 1.0);
            if (a != SphereNoise.Sample(p, 8, 4, 3.0)) differs = true;
        }
        differs.Should().BeTrue();
    }

    [Test]
    public void SphereNoise_varies_across_space()
    {
        var values = new HashSet<double>();
        double min = double.MaxValue, max = double.MinValue;
        for (var i = 0; i < 2_000; i++)
        {
            var v = SphereNoise.Sample(Point(i), 7, 4, 3.0);
            values.Add(v);
            min = Math.Min(min, v);
            max = Math.Max(max, v);
        }
        values.Count.Should().BeGreaterThan(1_500);
        (max - min).Should().BeGreaterThan(0.5);
    }
}
