using FluentAssertions;
using WorldEcon.WorldGen.Pipeline;

namespace WorldEcon.WorldGen.Tests.Unit.Tiles;

/// <summary>
/// Regression: worlds whose tile hydrology hit "Drainage cycle inside basin" (two tiles draining into each other) with
/// the version 6 terrain. Seed 42 at 16 × 2 × 3 is the Persistence tile-world fixture; seed 42 at 64 × 3 × 2 is the
/// m64 pipeline fixture. The absorbed-pit entry case (seed 42, default preset) is covered by TileCalibrationTests, which
/// generates that world.
/// </summary>
public class DrainageCycleTests
{
    [Test]
    public void Seed42_16x2x3_world_generates()
    {
        var act = () => WorldGenerator.Generate(42, new WorldGenSettings { Grid = new() { Frequency = 16, RegionMultiplier = 2, TileMultiplier = 3 } });
        act.Should().NotThrow();
    }

    [Test]
    public void Seed42_64x3x2_world_generates()
    {
        var act = () => WorldGenerator.Generate(42, new WorldGenSettings { Grid = new() { RegionMultiplier = 3, TileMultiplier = 2 } });
        act.Should().NotThrow();
    }
}
