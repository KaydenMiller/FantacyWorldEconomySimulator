using System.Reflection;
using FluentAssertions;
using WorldEcon.WorldGen.Pipeline;
using WorldEcon.WorldGen.Pipeline.Checkpoints;

namespace WorldEcon.WorldGen.Tests.Unit.Checkpoints;

public class ResumeTests
{
    private static readonly WorldGenSettings Small = CheckpointCodecTests.Small;

    public static IEnumerable<WorldGenStage> GlobeStages() =>
        [WorldGenStage.Tectonics, WorldGenStage.SeaLevel, WorldGenStage.Currents, WorldGenStage.Winds, WorldGenStage.Climate];

    private static WorldGenCheckpoint RoundTrip(WorldGenCheckpoint c) => CheckpointCodec.Decode(CheckpointCodec.Encode(c));

    [Test]
    [MethodDataSource(nameof(GlobeStages))]
    public void Resumed_run_matches_fresh_run(WorldGenStage after)
    {
        var fresh = WorldGenerator.Generate(42, Small);
        var (ckpt, _) = CheckpointCodecTests.Make(after);
        var obs = new RecordingObserver();
        var resumed = WorldGenerator.Generate(42, Small, new WorldGenOptions(Observer: obs, Resume: RoundTrip(ckpt)));

        resumed.Stages.Select(r => (r.Stage, r.Hash)).Should().Equal(fresh.Stages.Select(r => (r.Stage, r.Hash)));
        var covered = GlobeStages().TakeWhile(s => s != after).Append(after).ToHashSet();
        foreach (var r in resumed.Stages)
        {
            r.Loaded.Should().Be(covered.Contains(r.Stage), $"{r.Stage}");
            if (r.Loaded) r.Duration.Should().Be(TimeSpan.Zero);
        }

        var finals = obs.Snapshots.Where(s => s.FrameIndex == s.FrameCount - 1).ToList();
        finals.Select(s => s.Stage).Should().Equal(GlobeStages());
        finals.Select(s => s.Loaded).Should().Equal(GlobeStages().Select(covered.Contains));
        obs.Worlds.Should().ContainSingle();
    }

    [Test]
    public void Resume_does_not_change_the_checkpoint_layers()
    {
        var (ckpt, _) = CheckpointCodecTests.Make(WorldGenStage.Tectonics);
        var before = LayerHash.Of(ckpt.Layers.PlateOf);
        WorldGenerator.GenerateGlobe(42, Small, new WorldGenOptions(Resume: ckpt));
        ckpt.Layers.CurrentDir.Should().BeEmpty();
        LayerHash.Of(ckpt.Layers.PlateOf).Should().Be(before);
    }

    [Test]
    public void Resume_replays_frames_of_loaded_stages()
    {
        var obs = new RecordingObserver();
        WorldGenerator.GenerateGlobe(42, Small, new WorldGenOptions(Observer: obs), [FakeStages.ThreeFrames()]);
        var ckpt = WorldGenCheckpoint.FromSnapshots(42, Small, obs.Snapshots, WorldGenStage.Tectonics);
        var replay = new RecordingObserver();
        WorldGenerator.GenerateGlobe(42, Small, new WorldGenOptions(Observer: replay, Resume: ckpt), [FakeStages.ThreeFrames()]);
        replay.Snapshots.Select(s => (s.FrameIndex, s.FrameCount, s.FrameLabel, s.Loaded)).Should().Equal(
            (0, 3, "0 Myr", true), (1, 3, "10 Myr", true), (2, 3, (string?)null, true));
        replay.Snapshots[2].Hash.Should().Be(ckpt.Stages.Single().OutputHash);
    }

    [Test]
    public void Undeclared_section_does_not_change_a_stage_hash()
    {
        var baseline = WorldGenerator.GenerateGlobe(42, Small).Stages.ToDictionary(r => r.Stage, r => r.Hash);
        var reads = WorldGenerator.GlobeStages.ToDictionary(e => e.Stage, e => e.Reads.ToHashSet());
        var failures = new List<string>();
        var tried = 0;

        foreach (var sectionProp in typeof(WorldGenSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var section = sectionProp.GetValue(Small)!;
            foreach (var leaf in section.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.PropertyType == typeof(int)))
            {
                var declaredAs = Declared(sectionProp.Name, leaf.Name);
                // Stages that do not declare this leaf: changing it must leave their hashes unchanged.
                var stagesToCheck = GlobeStages().TakeWhile(s => declaredAs is null || !reads[s].Contains(declaredAs.Value)).ToList();
                if (stagesToCheck.Count == 0) continue;
                var changed = Perturb(sectionProp.Name, leaf.Name, (int)leaf.GetValue(section)!);
                if (changed is null) continue;
                tried++;
                var hashes = WorldGenerator.GenerateGlobe(42, changed).Stages.ToDictionary(r => r.Stage, r => r.Hash);
                foreach (var s in stagesToCheck)
                    if (hashes[s] != baseline[s]) failures.Add($"{s} changed when {sectionProp.Name}.{leaf.Name} changed");
            }
        }

        tried.Should().BeGreaterThan(50, "most leaves have a valid neighbouring value");
        failures.Should().BeEmpty();
    }

    // The fingerprint section a settings leaf belongs to, or null for a leaf no globe stage may read.
    private static SettingsSection? Declared(string section, string leaf) => section switch
    {
        "Grid" => leaf is "Frequency" or "PlanetRadiusKm" ? SettingsSection.GridGlobe : null,
        _ => Enum.TryParse<SettingsSection>(section, out var s) ? s : null,
    };

    private static WorldGenSettings? Perturb(string section, string leaf, int value)
    {
        foreach (var candidate in new[] { value + 1, value - 1 })
        {
            var s = WorldGenSettingsJson.Deserialize($"{{\"{section}\":{{\"{leaf}\":{candidate}}}}}", Small);
            if (!s.Validate().IsError) return s;
        }

        return null;
    }

    [Test]
    public void Changed_upstream_section_is_refused_with_stage_and_section()
    {
        var (ckpt, _) = CheckpointCodecTests.Make(WorldGenStage.Tectonics);
        var changed = Small with { SeaLevel = Small.SeaLevel with { LandFractionBp = 3200 } };
        var act = () => WorldGenerator.Generate(42, changed, new WorldGenOptions(Resume: ckpt));
        act.Should().Throw<CheckpointMismatchException>()
            .Which.Differences.Should().Contain("Tectonics: section SeaLevel differs");
    }

    [Test]
    public void Changed_downstream_section_is_accepted()
    {
        var (ckpt, _) = CheckpointCodecTests.Make(WorldGenStage.Tectonics);
        var changed = Small with { Climate = Small.Climate with { EquatorDeciC = Small.Climate.EquatorDeciC + 10 } };
        var world = WorldGenerator.GenerateGlobe(42, changed, new WorldGenOptions(Resume: ckpt));
        world.Stages.Single(r => r.Stage == WorldGenStage.Tectonics).Hash.Should().Be(ckpt.Stages[0].OutputHash);
    }

    [Test]
    public void Different_grid_is_refused_before_loading()
    {
        var (ckpt, _) = CheckpointCodecTests.Make(WorldGenStage.Tectonics);
        var bigger = Small with { Grid = Small.Grid with { Frequency = 16 } };
        var act = () => WorldGenerator.GenerateGlobe(42, bigger, new WorldGenOptions(Resume: ckpt));
        act.Should().Throw<CheckpointMismatchException>()
            .Which.Differences.Should().Contain("Tectonics: section GridGlobe differs");
    }

    [Test]
    public void Different_tile_multiplier_is_accepted()
    {
        var (ckpt, _) = CheckpointCodecTests.Make(WorldGenStage.Climate);
        var coarser = Small with { Grid = Small.Grid with { TileMultiplier = Small.Grid.TileMultiplier / 2 } };
        var act = () => WorldGenerator.GenerateGlobe(42, coarser, new WorldGenOptions(Resume: ckpt));
        act.Should().NotThrow();
    }

    [Test]
    public void Different_seed_is_refused()
    {
        var (ckpt, _) = CheckpointCodecTests.Make(WorldGenStage.Tectonics);
        var act = () => WorldGenerator.GenerateGlobe(7, Small, new WorldGenOptions(Resume: ckpt));
        act.Should().Throw<CheckpointMismatchException>()
            .Which.Differences.Should().Equal("seed differs (42 → 7)");
    }

    [Test]
    public void Mismatch_message_lists_every_difference_on_its_own_line()
    {
        var ex = new CheckpointMismatchException(["a", "b"]);
        ex.Message.Should().Contain("a\nb").And.Contain("checkpoint");
    }

    [Test]
    public void ResumeSettings_layers_generation_over_checkpoint_settings()
    {
        var stored = new WorldGenSettings { Grid = new() { Frequency = 8 }, Plates = new() { Count = 9 } };
        var (ckpt, _) = CheckpointCodecTests.Make(WorldGenStage.Tectonics, stored);

        WorldGenerator.ResumeSettings(ckpt, null, null).Should().Be(stored);
        var layered = WorldGenerator.ResumeSettings(ckpt, null, """{"Climate":{"EquatorDeciC":310}}""");
        layered.Plates.Count.Should().Be(9);
        layered.Climate.EquatorDeciC.Should().Be(310);
        var preset = WorldGenerator.ResumeSettings(ckpt, WorldGenPresets.Small5Km, null);
        preset.Should().Be(WorldGenPresets.Get(WorldGenPresets.Small5Km));
    }
}

public class StopAfterTests
{
    [Test]
    public void GenerateGlobe_stop_after_runs_only_up_to_that_stage()
    {
        var w = WorldGenerator.GenerateGlobe(42, CheckpointCodecTests.Small, WorldGenStage.SeaLevel);
        w.Stages.Select(s => s.Stage).Should().Equal(WorldGenStage.Tectonics, WorldGenStage.SeaLevel);
        w.Layers.CurrentDir.Should().BeEmpty();
    }

    [Test]
    public void GenerateGlobe_stop_after_rejects_a_tile_stage()
    {
        var act = () => WorldGenerator.GenerateGlobe(42, CheckpointCodecTests.Small, WorldGenStage.TileHydrology);
        act.Should().Throw<ArgumentException>().WithMessage("*globe stage*");
    }

    [Test]
    public void GenerateGlobe_stop_after_before_the_checkpoint_stage_loads_only_the_checkpoint()
    {
        var (ckpt, _) = CheckpointCodecTests.Make(WorldGenStage.Winds);
        var w = WorldGenerator.GenerateGlobe(42, CheckpointCodecTests.Small, WorldGenStage.SeaLevel, new WorldGenOptions(Resume: ckpt));
        w.Stages.Should().OnlyContain(s => s.Loaded);
        w.Stages.Select(s => s.Stage).Should().Equal(WorldGenStage.Tectonics, WorldGenStage.SeaLevel, WorldGenStage.Currents, WorldGenStage.Winds);
    }
}
