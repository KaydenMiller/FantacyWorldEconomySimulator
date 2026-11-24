using FluentAssertions;
using WorldEcon.WorldGen.Pipeline;
using WorldEcon.WorldGen.Pipeline.Checkpoints;

namespace WorldEcon.WorldGen.Tests.Unit.Checkpoints;

/// <summary>Observer that records everything it is told.</summary>
internal sealed class RecordingObserver : IWorldGenObserver
{
    public List<StageSnapshot> Snapshots { get; } = [];
    public List<GeneratedWorld> Worlds { get; } = [];
    public List<string> Calls { get; } = [];

    public void StageCompleted(StageSnapshot snapshot)
    {
        Snapshots.Add(snapshot);
        Calls.Add($"stage {snapshot.Stage} {snapshot.FrameIndex}/{snapshot.FrameCount}");
    }

    public void WorldCompleted(GeneratedWorld world)
    {
        Worlds.Add(world);
        Calls.Add("world");
    }
}

internal static class FakeStages
{
    /// <summary>
    /// A stage that writes ElevationM in three published frames labelled "0 Myr", "10 Myr", "20 Myr". It declares the
    /// real Tectonics stage's code version and reads, so checkpoints built from its snapshots (which fingerprint the real
    /// stages) resume through it.
    /// </summary>
    public static WorldGenerator.StageEntry ThreeFrames(WorldGenStage stage = WorldGenStage.Tectonics) =>
        new(stage, 1, [SettingsSection.GridGlobe, SettingsSection.Plates, SettingsSection.Elevation, SettingsSection.Tectonics, SettingsSection.SeaLevel],
            ctx =>
            {
                var n = ctx.Layers.Grid.CellCount;
                for (var f = 0; f < 3; f++)
                {
                    ctx.Layers.ElevationM = Enumerable.Repeat(f * 10, n).ToArray();
                    ctx.Frames.Publish(f, 3, $"{f * 10} Myr");
                }
            },
            l => LayerHash.Of(l.ElevationM));
}

public class StageObserverTests
{
    private static readonly WorldGenSettings Small = new() { Grid = new() { Frequency = 4 } };

    private static readonly WorldGenStage[] GlobeOrder =
        [WorldGenStage.Tectonics, WorldGenStage.SeaLevel, WorldGenStage.Currents, WorldGenStage.Winds, WorldGenStage.Climate];

    [Test]
    public void Observer_receives_one_snapshot_per_globe_stage_in_order_then_the_world()
    {
        var obs = new RecordingObserver();
        var world = WorldGenerator.Generate(42, Small, new WorldGenOptions(Observer: obs));

        var finals = obs.Snapshots.Where(s => s.FrameIndex == s.FrameCount - 1).ToList();
        finals.Select(s => s.Stage).Should().Equal(GlobeOrder);
        finals.Skip(1).Should().OnlyContain(s => s.FrameIndex == 0 && s.FrameCount == 1 && !s.Loaded && s.FrameLabel == null);
        obs.Snapshots.Where(s => s.Stage != WorldGenStage.Tectonics).Should().HaveCount(4, "only Tectonics publishes frames");
        foreach (var s in finals)
            s.Hash.Should().Be(world.Stages.Single(r => r.Stage == s.Stage).Hash);
        obs.Worlds.Should().ContainSingle().Which.Should().BeSameAs(world);
        obs.Calls[^1].Should().Be("world");
    }

    [Test]
    public void Snapshots_are_copies()
    {
        var obs = new RecordingObserver();
        var world = WorldGenerator.Generate(42, Small, new WorldGenOptions(Observer: obs));

        var tectonics = obs.Snapshots.Last(s => s.Stage == WorldGenStage.Tectonics);
        tectonics.Layers.CurrentDir.Should().BeEmpty("Tectonics ran before Currents");
        tectonics.Layers.PlateOf.Should().Equal(world.Layers.PlateOf);
        var elev = obs.Snapshots.Last(s => s.Stage == WorldGenStage.Tectonics).Layers;
        var before = world.Layers.ElevationM[0];
        elev.ElevationM[0] = before + 12345;
        world.Layers.ElevationM[0].Should().Be(before);
        elev.Grid.Should().BeSameAs(world.Layers.Grid);
    }

    [Test]
    public void GenerateGlobe_reports_globe_stages_but_no_world()
    {
        var obs = new RecordingObserver();
        WorldGenerator.GenerateGlobe(42, Small, new WorldGenOptions(Observer: obs));
        obs.Snapshots.Where(s => s.FrameIndex == s.FrameCount - 1).Select(s => s.Stage).Should().Equal(GlobeOrder);
        obs.Worlds.Should().BeEmpty();
    }

    [Test]
    public void Fake_multi_frame_stage_publishes_frames_with_indices_and_labels()
    {
        var obs = new RecordingObserver();
        var fake = FakeStages.ThreeFrames();
        var world = WorldGenerator.GenerateGlobe(42, Small, new WorldGenOptions(Observer: obs), [fake]);

        obs.Snapshots.Select(s => (s.FrameIndex, s.FrameCount, s.FrameLabel)).Should().Equal((0, 3, "0 Myr"), (1, 3, "10 Myr"), (2, 3, "20 Myr"));
        obs.Snapshots.Select(s => s.Layers.ElevationM[0]).Should().Equal(0, 10, 20);
        obs.Snapshots[0].Hash.Should().Be(0);
        obs.Snapshots[1].Hash.Should().Be(0);
        obs.Snapshots[2].Hash.Should().Be(world.Stages.Single().Hash);
    }

    [Test]
    public void Stage_that_publishes_no_frames_still_reports_its_result()
    {
        var obs = new RecordingObserver();
        var silent = new WorldGenerator.StageEntry(WorldGenStage.Tectonics, 1, [SettingsSection.GridGlobe],
            ctx => ctx.Layers.ElevationM = new int[ctx.Layers.Grid.CellCount], l => LayerHash.Of(l.ElevationM));
        WorldGenerator.GenerateGlobe(42, Small, new WorldGenOptions(Observer: obs), [silent]);
        obs.Snapshots.Should().ContainSingle().Which.FrameCount.Should().Be(1);
    }

    [Test]
    public void Without_observer_no_clone_happens()
    {
        var before = WorldLayers.CloneCount;
        WorldGenerator.Generate(42, Small);
        WorldLayers.CloneCount.Should().Be(before);
    }

    [Test]
    public void StageResults_of_a_fresh_run_are_not_loaded()
    {
        WorldGenerator.Generate(42, Small).Stages.Should().OnlyContain(r => !r.Loaded);
    }
}
