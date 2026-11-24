using FluentAssertions;
using WorldEcon.WorldGen.Pipeline;
using WorldEcon.WorldGen.Pipeline.Checkpoints;

namespace WorldEcon.WorldGen.Tests.Unit.Checkpoints;

public class StageFingerprintTests
{
    [Test]
    public void SectionHash_changes_only_with_its_own_section()
    {
        var a = new WorldGenSettings();
        var b = a with { Climate = a.Climate with { EquatorDeciC = a.Climate.EquatorDeciC + 1 } };
        StageFingerprint.SectionHash(a, SettingsSection.Climate).Should().NotBe(StageFingerprint.SectionHash(b, SettingsSection.Climate));
        StageFingerprint.SectionHash(a, SettingsSection.Plates).Should().Be(StageFingerprint.SectionHash(b, SettingsSection.Plates));
    }

    [Test]
    public void GridGlobe_ignores_tile_and_region_multipliers()
    {
        var a = WorldGenPresets.Get(WorldGenPresets.Earth5Km);
        var b = WorldGenPresets.Get(WorldGenPresets.Earth10Km);
        StageFingerprint.SectionHash(a, SettingsSection.GridGlobe).Should().Be(StageFingerprint.SectionHash(b, SettingsSection.GridGlobe));
        var region = a with { Grid = a.Grid with { RegionMultiplier = 2 } };
        StageFingerprint.SectionHash(a, SettingsSection.GridGlobe).Should().Be(StageFingerprint.SectionHash(region, SettingsSection.GridGlobe));
        var small = WorldGenPresets.Get(WorldGenPresets.Small5Km);
        StageFingerprint.SectionHash(a, SettingsSection.GridGlobe).Should().NotBe(StageFingerprint.SectionHash(small, SettingsSection.GridGlobe));
    }

    [Test]
    public void Sections_with_equal_values_hash_differently_by_section()
    {
        // Two sections whose leaves happen to be equal must not collide: the section is part of the hash.
        var s = new WorldGenSettings();
        StageFingerprint.SectionHash(s, SettingsSection.Currents).Should().NotBe(StageFingerprint.SectionHash(s, SettingsSection.Winds));
    }

    [Test]
    public void Declared_reads_match_the_spec()
    {
        WorldGenerator.GlobeStages.ToDictionary(e => e.Stage, e => e.Reads.ToArray()).Should().BeEquivalentTo(new Dictionary<WorldGenStage, SettingsSection[]>
        {
            [WorldGenStage.Tectonics] = [SettingsSection.GridGlobe, SettingsSection.Plates, SettingsSection.Elevation, SettingsSection.Tectonics, SettingsSection.SeaLevel],
            [WorldGenStage.SeaLevel] = [SettingsSection.GridGlobe, SettingsSection.SeaLevel],
            [WorldGenStage.Currents] = [SettingsSection.GridGlobe, SettingsSection.Currents],
            [WorldGenStage.Winds] = [SettingsSection.GridGlobe, SettingsSection.Winds],
            [WorldGenStage.Climate] = [SettingsSection.GridGlobe, SettingsSection.Climate],
        });
        WorldGenerator.GlobeStages.Should().OnlyContain(e => e.CodeVersion == 1);
    }

    [Test]
    public void For_lists_stages_up_to_and_including_the_given_stage()
    {
        StageFingerprint.For(new WorldGenSettings(), WorldGenStage.SeaLevel).Select(p => p.Stage)
            .Should().Equal(WorldGenStage.Tectonics, WorldGenStage.SeaLevel);
    }

    [Test]
    public void For_rejects_a_stage_that_is_not_a_globe_stage()
    {
        var act = () => StageFingerprint.For(new WorldGenSettings(), WorldGenStage.TileHydrology);
        act.Should().Throw<ArgumentException>().WithMessage("*globe stage*");
    }

    [Test]
    public void Differences_names_stage_and_section()
    {
        var a = new WorldGenSettings();
        var b = a with { SeaLevel = a.SeaLevel with { LandFractionBp = 3200 } };
        var diff = StageFingerprint.Differences(StageFingerprint.For(a, WorldGenStage.SeaLevel), 42, 5, StageFingerprint.For(b, WorldGenStage.SeaLevel), 42, 5);
        diff.Should().Equal("Tectonics: section SeaLevel differs", "SeaLevel: section SeaLevel differs");
        var climate = a with { Climate = a.Climate with { EquatorDeciC = 301 } };
        StageFingerprint.Differences(StageFingerprint.For(a, WorldGenStage.Winds), 42, 5, StageFingerprint.For(climate, WorldGenStage.Winds), 42, 5).Should().BeEmpty();
    }

    [Test]
    public void Differences_names_seed_version_and_code_version()
    {
        var p = StageFingerprint.For(new WorldGenSettings(), WorldGenStage.Tectonics);
        var bumped = p.Select(x => x with { CodeVersion = 2 }).ToList();
        StageFingerprint.Differences(p, 42, 5, bumped, 7, 6).Should().Equal(
            "seed differs (42 → 7)",
            "generator version differs (5 → 6)",
            "Tectonics: code version differs (1 → 2)");
    }

    [Test]
    public void Combine_changes_with_seed_and_prints()
    {
        var p = StageFingerprint.For(new WorldGenSettings(), WorldGenStage.Climate);
        var c = StageFingerprint.Combine(42, 5, p);
        c.Should().Be(StageFingerprint.Combine(42, 5, p));
        c.Should().NotBe(StageFingerprint.Combine(43, 5, p));
        c.Should().NotBe(StageFingerprint.Combine(42, 5, p.Take(4).ToList()));
    }
}
