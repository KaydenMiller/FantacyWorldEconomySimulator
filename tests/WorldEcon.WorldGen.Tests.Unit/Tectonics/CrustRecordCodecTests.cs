using FluentAssertions;
using WorldEcon.WorldGen.Pipeline;
using WorldEcon.WorldGen.Pipeline.Checkpoints;
using WorldEcon.WorldGen.Tectonics;

namespace WorldEcon.WorldGen.Tests.Unit.Tectonics;

public class CrustRecordCodecTests
{
    [Test]
    public void Record_and_boundary_layers_round_trip()
    {
        var l = WorldGenerator.GenerateGlobe(42, TectonicsStageTests.Small, WorldGenStage.Tectonics).Layers;
        var bytes = CrustRecordCodec.Encode(l.Crust!, l.CollisionStrengthPermille, l.BoundaryDistanceCells);
        var (crust, strength, distance) = CrustRecordCodec.Decode(bytes, l.Grid.CellCount);
        crust.Hash().Should().Be(l.Crust!.Hash());
        strength.Should().Equal(l.CollisionStrengthPermille);
        distance.Should().Equal(l.BoundaryDistanceCells);
    }

    [Test]
    public void Wrong_cell_count_is_refused()
    {
        var l = WorldGenerator.GenerateGlobe(42, TectonicsStageTests.Small, WorldGenStage.Tectonics).Layers;
        var bytes = CrustRecordCodec.Encode(l.Crust!, l.CollisionStrengthPermille, l.BoundaryDistanceCells);
        var act = () => CrustRecordCodec.Decode(bytes, l.Grid.CellCount + 1);
        act.Should().Throw<FormatException>().WithMessage("*cells*");
    }

    [Test]
    public void Checkpoint_v2_round_trips_the_record()
    {
        var obs = new Checkpoints.RecordingObserver();
        WorldGenerator.GenerateGlobe(42, TectonicsStageTests.Small, new WorldGenOptions(Observer: obs));
        var c = WorldGenCheckpoint.FromSnapshots(42, TectonicsStageTests.Small, obs.Snapshots, WorldGenStage.Climate);
        var d = CheckpointCodec.Decode(CheckpointCodec.Encode(c));
        CheckpointCodec.FormatVersion.Should().Be(2);
        d.Layers.Crust!.Hash().Should().Be(c.Layers.Crust!.Hash());
        d.Layers.BoundaryDistanceCells.Should().Equal(c.Layers.BoundaryDistanceCells);
        d.Frames.Should().OnlyContain(f => f.Layers.Crust != null);
    }
}
