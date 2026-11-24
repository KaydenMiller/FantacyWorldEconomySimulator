using FluentAssertions;
using WorldEcon.WorldGen.Pipeline;
using WorldEcon.WorldGen.Pipeline.Checkpoints;
using WorldEcon.WorldGen.Stages;
using WorldEcon.WorldGen.Tests.Unit.Checkpoints;

namespace WorldEcon.WorldGen.Tests.Unit.Tectonics;

public class TectonicsStageTests
{
    internal static readonly WorldGenSettings Small = new() { Grid = new() { Frequency = 16 }, Tectonics = new() { SimulatedMy = 60, FrameEverySteps = 10 } };

    [Test]
    public void Globe_stages_start_with_Tectonics()
    {
        WorldGenerator.GlobeStages.Select(e => e.Stage).Should().Equal(
            WorldGenStage.Tectonics, WorldGenStage.SeaLevel, WorldGenStage.Currents, WorldGenStage.Winds, WorldGenStage.Climate);
        WorldGenerator.GlobeStages[0].Reads.Should().Equal(
            SettingsSection.GridGlobe, SettingsSection.Plates, SettingsSection.Elevation, SettingsSection.Tectonics, SettingsSection.SeaLevel);
        WorldGenerator.GeneratorVersion.Should().Be(6);
    }

    [Test]
    public void Stage_is_deterministic()
    {
        var a = WorldGenerator.GenerateGlobe(42, Small, WorldGenStage.Tectonics);
        var b = WorldGenerator.GenerateGlobe(42, Small, WorldGenStage.Tectonics);
        a.Stages[0].Hash.Should().Be(b.Stages[0].Hash);
        WorldGenerator.GenerateGlobe(43, Small, WorldGenStage.Tectonics).Stages[0].Hash.Should().NotBe(a.Stages[0].Hash);
    }

    [Test]
    public void Frames_count_and_labels()
    {
        var obs = new RecordingObserver();
        WorldGenerator.GenerateGlobe(42, Small, WorldGenStage.Tectonics, new WorldGenOptions(Observer: obs));
        // 60 My / 2 My = 30 steps; a frame every 10 steps → frames after steps 10 and 20, then the final one.
        obs.Snapshots.Select(s => s.FrameLabel).Should().Equal("40 My ago", "20 My ago", "0 My ago");
        obs.Snapshots.Should().OnlyContain(s => s.FrameCount == 3);
    }

    [Test]
    public void Every_cell_has_crust_a_plate_and_dense_plate_ids()
    {
        var l = WorldGenerator.GenerateGlobe(42, Small, WorldGenStage.Tectonics).Layers;
        l.Crust.Should().NotBeNull();
        l.Crust!.Count.Should().Be(l.Grid.CellCount);
        l.PlateOf.Distinct().Order().Should().Equal(Enumerable.Range(0, l.Plates.Length));
        l.Plates.Select(p => p.Id).Should().Equal(Enumerable.Range(0, l.Plates.Length));
        l.ElevationM.Should().HaveCount(l.Grid.CellCount);
        l.BoundaryDistanceCells.Should().HaveCount(l.Grid.CellCount);
    }

    public static IEnumerable<Func<TectonicsSettings>> Extremes()
    {
        yield return () => new TectonicsSettings { SimulatedMy = 2, StepMy = 2 };
        yield return () => new TectonicsSettings { SimulatedMy = 40, CratonCount = 1 };
        yield return () => new TectonicsSettings { SimulatedMy = 40, StartArrangement = 1, CratonCount = 1000 };
    }

    [Test]
    [MethodDataSource(nameof(Extremes))]
    public void Extreme_settings_complete(TectonicsSettings t)
    {
        foreach (var plates in new[] { 1, 20 })
        foreach (var fraction in new[] { 0, 10_000 })
        {
            var s = Small with { Tectonics = t, Plates = new() { Count = plates, ContinentalFractionBp = fraction } };
            var act = () => WorldGenerator.GenerateGlobe(42, s, WorldGenStage.Climate);
            act.Should().NotThrow($"plates {plates}, continental {fraction} bp");
        }
    }

    [Test]
    public void Resume_after_Tectonics_matches_fresh_run()
    {
        var obs = new RecordingObserver();
        var fresh = WorldGenerator.GenerateGlobe(42, Small, new WorldGenOptions(Observer: obs));
        var ckpt = CheckpointCodec.Decode(CheckpointCodec.Encode(WorldGenCheckpoint.FromSnapshots(42, Small, obs.Snapshots, WorldGenStage.Tectonics)));
        var resumed = WorldGenerator.GenerateGlobe(42, Small, new WorldGenOptions(Resume: ckpt));
        resumed.Stages.Select(r => r.Hash).Should().Equal(fresh.Stages.Select(r => r.Hash));
    }
}
