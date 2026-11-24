using FluentAssertions;
using WorldEcon.WorldGen.Pipeline;
using WorldEcon.WorldGen.Pipeline.Checkpoints;

namespace WorldEcon.WorldGen.Tests.Unit.Checkpoints;

public class CheckpointCodecTests
{
    internal static readonly WorldGenSettings Small = new() { Grid = new() { Frequency = 8 } };

    internal static (WorldGenCheckpoint Checkpoint, GeneratedWorld World) Make(WorldGenStage last, WorldGenSettings? settings = null)
    {
        settings ??= Small;
        var obs = new RecordingObserver();
        var world = WorldGenerator.GenerateGlobe(42, settings, new WorldGenOptions(Observer: obs));
        return (WorldGenCheckpoint.FromSnapshots(42, settings, obs.Snapshots, last), world);
    }

    private static void ShouldMatchLayers(WorldLayers a, WorldLayers b)
    {
        LayerHash.Of(a.PlateOf).Should().Be(LayerHash.Of(b.PlateOf));
        a.Plates.Should().Equal(b.Plates);
        LayerHash.Of(a.ElevationM).Should().Be(LayerHash.Of(b.ElevationM));
        LayerHash.Of(a.BoundaryKind).Should().Be(LayerHash.Of(b.BoundaryKind));
        a.SeaLevelM.Should().Be(b.SeaLevelM);
        LayerHash.Of(a.CurrentDir).Should().Be(LayerHash.Of(b.CurrentDir));
        LayerHash.Of(a.CurrentAnomalyDeciC).Should().Be(LayerHash.Of(b.CurrentAnomalyDeciC));
        LayerHash.Of(a.WindDir).Should().Be(LayerHash.Of(b.WindDir));
        LayerHash.Of(a.WindStrength).Should().Be(LayerHash.Of(b.WindStrength));
        LayerHash.Of(a.TemperatureDeciC).Should().Be(LayerHash.Of(b.TemperatureDeciC));
        LayerHash.Of(a.PrecipitationMm).Should().Be(LayerHash.Of(b.PrecipitationMm));
        a.Grid.CellCount.Should().Be(b.Grid.CellCount);
    }

    [Test]
    public void FromSnapshots_builds_header_from_settings_and_snapshots()
    {
        var (c, world) = Make(WorldGenStage.SeaLevel);
        c.WorldSeed.Should().Be(42);
        c.GeneratorVersion.Should().Be(WorldGenerator.GeneratorVersion);
        c.LastStage.Should().Be(WorldGenStage.SeaLevel);
        c.Stages.Select(s => s.Print.Stage).Should().Equal(WorldGenStage.Tectonics, WorldGenStage.SeaLevel);
        c.Stages.Select(s => s.OutputHash).Should().Equal(world.Stages.Take(2).Select(r => r.Hash));
        c.Fingerprint.Should().Be(StageFingerprint.Combine(42, WorldGenerator.GeneratorVersion, StageFingerprint.For(Small, WorldGenStage.SeaLevel)));
        c.Layers.ElevationM.Should().NotBeEmpty();
        c.Layers.CurrentDir.Should().BeEmpty("Currents is after the checkpoint");
    }

    [Test]
    public void Round_trip_preserves_header_layers_and_hashes()
    {
        var (c, _) = Make(WorldGenStage.Climate);
        var d = CheckpointCodec.Decode(CheckpointCodec.Encode(c));
        d.WorldSeed.Should().Be(c.WorldSeed);
        d.GeneratorVersion.Should().Be(c.GeneratorVersion);
        d.LastStage.Should().Be(c.LastStage);
        d.Fingerprint.Should().Be(c.Fingerprint);
        d.Stages.Should().Equal(c.Stages);
        d.Settings.Should().Be(c.Settings);
        ShouldMatchLayers(d.Layers, c.Layers);
        d.Frames.Should().OnlyContain(f => f.Stage == WorldGenStage.Tectonics, "the Tectonics stage publishes frames");
        d.Frames.Should().HaveCount(c.Frames.Count);
    }

    [Test]
    public void Round_trip_keeps_frames_of_multi_frame_stage()
    {
        var obs = new RecordingObserver();
        WorldGenerator.GenerateGlobe(42, Small, new WorldGenOptions(Observer: obs), [FakeStages.ThreeFrames()]);
        var c = WorldGenCheckpoint.FromSnapshots(42, Small, obs.Snapshots, WorldGenStage.Tectonics);
        c.Frames.Select(f => (f.FrameIndex, f.FrameCount, f.Label)).Should().Equal((0, 3, "0 Myr"), (1, 3, "10 Myr"));
        var d = CheckpointCodec.Decode(CheckpointCodec.Encode(c));
        d.Frames.Select(f => (f.Stage, f.FrameIndex, f.FrameCount, f.Label)).Should().Equal(c.Frames.Select(f => (f.Stage, f.FrameIndex, f.FrameCount, f.Label)));
        d.Frames.Select(f => f.Layers.ElevationM[0]).Should().Equal(0, 10);
        d.Layers.ElevationM[0].Should().Be(20);
    }

    [Test]
    public void Write_then_Read_round_trips_through_a_file()
    {
        var (c, _) = Make(WorldGenStage.SeaLevel);
        var path = Path.Combine(Path.GetTempPath(), $"ckpt-{Guid.NewGuid():N}.wgckpt");
        try
        {
            CheckpointCodec.Write(path, c);
            var d = CheckpointCodec.Read(path);
            d.Fingerprint.Should().Be(c.Fingerprint);
            ShouldMatchLayers(d.Layers, c.Layers);
            Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + "*").Should().ContainSingle("no temp file is left");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public void Truncated_data_throws_format_exception()
    {
        var bytes = CheckpointCodec.Encode(Make(WorldGenStage.Tectonics).Checkpoint);
        var act = () => CheckpointCodec.Decode(bytes.AsSpan(0, bytes.Length - 10));
        act.Should().Throw<CheckpointFormatException>().WithMessage("*truncated*");
    }

    [Test]
    public void Bad_magic_throws_format_exception()
    {
        var act = () => CheckpointCodec.Decode("hello world, not a checkpoint"u8);
        act.Should().Throw<CheckpointFormatException>().WithMessage("*not a checkpoint file*");
    }

    [Test]
    public void Empty_data_throws_format_exception()
    {
        var act = () => CheckpointCodec.Decode(ReadOnlySpan<byte>.Empty);
        act.Should().Throw<CheckpointFormatException>().WithMessage("*not a checkpoint file*");
    }

    [Test]
    public void Newer_format_version_throws_format_exception()
    {
        var bytes = CheckpointCodec.Encode(Make(WorldGenStage.Tectonics).Checkpoint);
        bytes[CheckpointCodec.Magic.Length] = CheckpointCodec.FormatVersion + 1;
        var act = () => CheckpointCodec.Decode(bytes);
        act.Should().Throw<CheckpointFormatException>().WithMessage($"*format version {CheckpointCodec.FormatVersion + 1}*");
    }

    [Test]
    public void Read_of_a_missing_or_corrupt_file_names_the_path()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ckpt-{Guid.NewGuid():N}.wgckpt");
        File.WriteAllText(path, "garbage");
        try
        {
            var act = () => CheckpointCodec.Read(path);
            act.Should().Throw<CheckpointFormatException>().WithMessage($"*{path}*not a checkpoint file*");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public void FromSnapshots_rejects_a_tile_stage()
    {
        var obs = new RecordingObserver();
        WorldGenerator.GenerateGlobe(42, Small, new WorldGenOptions(Observer: obs));
        var act = () => WorldGenCheckpoint.FromSnapshots(42, Small, obs.Snapshots, WorldGenStage.TileHydrology);
        act.Should().Throw<ArgumentException>();
    }

    [Test]
    public void FromSnapshots_rejects_a_stage_without_a_snapshot()
    {
        var obs = new RecordingObserver();
        WorldGenerator.GenerateGlobe(42, Small, new WorldGenOptions(Observer: obs));
        var act = () => WorldGenCheckpoint.FromSnapshots(42, Small, obs.Snapshots.Take(2).ToList(), WorldGenStage.Winds);
        act.Should().Throw<ArgumentException>().WithMessage("*no finished snapshot of stage*");
    }
}
