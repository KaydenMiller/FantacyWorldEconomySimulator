using FluentAssertions;
using WorldEcon.WorldGen.Pipeline;

namespace WorldEcon.WorldGen.Tests.Unit;

/// <summary>
/// Determinism pins for seed 42. Full pipeline (<see cref="WorldGenerator.Generate"/>): globe Frequency 16, region ×3,
/// tile ×4 (368,642 tiles). Climate-only helper (<see cref="WorldGenerator.GenerateGlobe"/>, stages 1–6): frequency
/// 32 (10,242 cells); its hashes are the generator-version-3 values (those stages did not change). The constants were recorded
/// from a Linux run. Run this class on Windows and compare: a mismatch there indicates floating-point drift across
/// operating systems (spec section 11), not a logic bug. Changing any stage algorithm changes these hashes and requires
/// bumping <see cref="WorldGenerator.GeneratorVersion"/> and re-recording the constants.
/// </summary>
public class GoldenSeedTests
{
    private const int ExpectedGeneratorVersion = 6;

    private static readonly (WorldGenStage Stage, ulong Hash)[] Golden =
    [
        (WorldGenStage.Tectonics, 9377539388380863061UL),
        (WorldGenStage.SeaLevel, 11255876333845877843UL),
        (WorldGenStage.Currents, 532743064411830595UL),
        (WorldGenStage.Winds, 7032149197387826730UL),
        (WorldGenStage.Climate, 12272396955960600774UL),
        (WorldGenStage.TileGrid, 0UL),
        (WorldGenStage.TileElevation, 1759501930387671347UL),
        (WorldGenStage.TileSeaLevel, 10149557267568397831UL),
        (WorldGenStage.TileClimate, 9076257588571890062UL),
        (WorldGenStage.TileHydrology, 7372405873056323825UL),
        (WorldGenStage.TileLandform, 8744367443217559826UL),
        (WorldGenStage.Summary, 13043148366052014586UL),
        (WorldGenStage.Areas, 17875372424145839914UL),
    ];

    private static readonly (WorldGenStage Stage, ulong Hash)[] GoldenGlobe =
    [
        (WorldGenStage.Tectonics, 12605790800125933539UL),
        (WorldGenStage.SeaLevel, 2340789401102290603UL),
        (WorldGenStage.Currents, 6873463776063210102UL),
        (WorldGenStage.Winds, 13439950535715908344UL),
        (WorldGenStage.Climate, 8328911130752445880UL),
    ];

    private static readonly WorldGenSettings Settings = new() { Grid = new() { Frequency = 16, RegionMultiplier = 3, TileMultiplier = 4 } };
    private static readonly WorldGenSettings GlobeSettings = new() { Grid = new() { Frequency = 32 } };

    [Test]
    public void Seed42_small_tile_world_matches_the_recorded_stage_hashes()
    {
        var world = WorldGenerator.Generate(42, Settings);

        world.GeneratorVersion.Should().Be(ExpectedGeneratorVersion,
            "the golden hashes belong to this generator version; re-record them when it changes");
        var actual = world.Stages.Select(s => (s.Stage, s.Hash)).ToArray();
        actual.Should().Equal(Golden);
    }

    [Test]
    public void Seed42_globe_climate_stages_at_frequency32_matches_the_recorded_stage_hashes()
    {
        var world = WorldGenerator.GenerateGlobe(42, GlobeSettings);
        world.Stages.Select(s => (s.Stage, s.Hash)).ToArray().Should().Equal(GoldenGlobe);
        world.Tiles.Should().BeNull();
        world.GeneratorVersion.Should().Be(0, "GenerateGlobe output is not a generated world");
    }

    [Test]
    public void Two_generate_calls_in_one_process_produce_identical_hashes()
    {
        var first = WorldGenerator.Generate(42, Settings);
        var second = WorldGenerator.Generate(42, Settings);

        second.Stages.Select(s => (s.Stage, s.Hash))
            .Should().Equal(first.Stages.Select(s => (s.Stage, s.Hash)));
    }
}
