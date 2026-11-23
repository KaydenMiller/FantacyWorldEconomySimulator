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
    private const int ExpectedGeneratorVersion = 5;

    private static readonly (WorldGenStage Stage, ulong Hash)[] Golden =
    [
        (WorldGenStage.Plates, 15461327187818838491UL),
        (WorldGenStage.Elevation, 3080992284226982191UL),
        (WorldGenStage.SeaLevel, 6917040920465372478UL),
        (WorldGenStage.Currents, 2193152278278954746UL),
        (WorldGenStage.Winds, 12983842637910938549UL),
        (WorldGenStage.Climate, 4014776759878084955UL),
        (WorldGenStage.TileGrid, 0UL),
        (WorldGenStage.TileElevation, 6748854441168672647UL),
        (WorldGenStage.TileSeaLevel, 2897217092163579793UL),
        (WorldGenStage.TileClimate, 17207401778292905099UL),
        (WorldGenStage.TileHydrology, 7473861701537284876UL),
        (WorldGenStage.TileLandform, 9246537001816030756UL),
        (WorldGenStage.Summary, 16761013873505122623UL),
        (WorldGenStage.Areas, 13051592550779283734UL),
    ];

    private static readonly (WorldGenStage Stage, ulong Hash)[] GoldenGlobe =
    [
        (WorldGenStage.Plates, 4886939027779998393UL),
        (WorldGenStage.Elevation, 15041891890802781804UL),
        (WorldGenStage.SeaLevel, 13613541121370462862UL),
        (WorldGenStage.Currents, 7876328726986954892UL),
        (WorldGenStage.Winds, 13894721113808039364UL),
        (WorldGenStage.Climate, 6143986598395424875UL),
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
