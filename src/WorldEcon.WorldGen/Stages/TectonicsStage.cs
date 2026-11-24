using WorldEcon.Simulation.Random;
using WorldEcon.WorldGen.Pipeline;
using WorldEcon.WorldGen.Pipeline.Checkpoints;
using WorldEcon.WorldGen.Tectonics;

namespace WorldEcon.WorldGen.Stages;

/// <summary>
/// The plate history simulation (design 2026-11-24), replacing the version 5 Plates and Elevation stages. Starting plates
/// come from the flood fill and rotation draw of <see cref="PlateStage"/>, starting crust from <see cref="InitialCrust"/>;
/// then <see cref="TectonicsSettings.SimulatedMy"/> / <see cref="TectonicsSettings.StepMy"/> steps, each: age the record,
/// move and resolve (<see cref="StepState.StepMotionAndInteractions"/>), fuse, rift and refuse (<see cref="Events"/>),
/// erode and expose (<see cref="Surface"/>). A frame is published every <see cref="TectonicsSettings.FrameEverySteps"/>
/// steps and at the end. Writes <see cref="WorldLayers.PlateOf"/>, <see cref="WorldLayers.Plates"/>,
/// <see cref="WorldLayers.ElevationM"/>, <see cref="WorldLayers.BoundaryKind"/>, <see cref="WorldLayers.Crust"/>,
/// <see cref="WorldLayers.CollisionStrengthPermille"/> and <see cref="WorldLayers.BoundaryDistanceCells"/>.
/// </summary>
public static class TectonicsStage
{
    public static void Run(StageContext ctx)
    {
        var l = ctx.Layers;
        var s = ctx.Settings;
        var t = s.Tectonics;
        var seed = StageSeed.For(ctx.WorldSeed, WorldGenStage.Tectonics);
        PlateStage.Run(l, s, DeterministicHash.Combine(seed, 1, 0, 0));
        var crust = InitialCrust.Build(l.Grid, l.Geometry, t, s.Plates, DeterministicHash.Combine(seed, 2, 0, 0));
        var radius = (double)s.Grid.PlanetRadiusKm;
        var state = StepState.Create(l.Grid, l.Geometry, s, l.PlateOf, crust.Record,
            l.Plates.Select(p => p.RotationAxis).ToList(), l.Plates.Select(p => p.AngularSpeed * radius).ToList(),
            DeterministicHash.Combine(seed, 3, 0, 0));

        var steps = Math.Max(1, t.SimulatedMy / t.StepMy);
        var frameCount = (steps - 1) / t.FrameEverySteps + 1;
        var noiseSeed = DeterministicHash.Combine(seed, 4, 0, 0);
        var frame = 0;
        for (var i = 0; i < steps; i++)
        {
            Surface.AgeRecord(state);
            state.StepMotionAndInteractions();
            Events.FuseCandidates(state);
            Events.RollRifts(state);
            Events.ProcessScheduled(state);
            Events.RemoveEmptyPlates(state);
            Events.MergeSliverPlates(state);
            PlateDynamics.Update(state);
            Surface.Erode(state);
            Surface.Expose(state);
            ctx.StageProgress?.Report((i + 1) / (double)steps);
            var done = i + 1;
            if (done < steps && done % t.FrameEverySteps == 0)
            {
                Write(l, state, s, noiseSeed);
                ctx.Frames.Publish(frame++, frameCount, $"{t.SimulatedMy - done * t.StepMy} My ago");
            }
        }

        Write(l, state, s, noiseSeed);
        ctx.Frames.Publish(frameCount - 1, frameCount, "0 My ago");
    }

    public static ulong Hash(WorldLayers l) =>
        DeterministicHash.Combine(
            DeterministicHash.Combine(LayerHash.Of(l.PlateOf), LayerHash.Of(l.ElevationM), LayerHash.Of(l.BoundaryKind), 0),
            l.Crust?.Hash() ?? 0, LayerHash.Of(MemoryAsInts(l.CollisionStrengthPermille)), LayerHash.Of(MemoryAsInts(l.BoundaryDistanceCells)));

    // Present-day layers from the state: plates, crust, elevation, boundaries, shelves.
    private static void Write(WorldLayers l, StepState state, WorldGenSettings s, ulong noiseSeed)
    {
        var n = l.Grid.CellCount;
        l.PlateOf = (int[])state.PlateOfCell.Clone();
        l.Crust = state.Present();
        var plates = new PlateInfo[state.PlateCount];
        var cells = new int[state.PlateCount];
        var continental = new int[state.PlateCount];
        var first = Enumerable.Repeat(-1, state.PlateCount).ToArray();
        for (var c = 0; c < n; c++)
        {
            var p = l.PlateOf[c];
            cells[p]++;
            if (l.Crust.Kind[c] != CrustKind.Oceanic) continental[p]++;
            if (first[p] < 0) first[p] = c;
        }

        for (var p = 0; p < plates.Length; p++)
            plates[p] = new PlateInfo(p, continental[p] * 2 > cells[p], state.Axis[p], state.StepMicroRad[p] / 1e6 / s.Tectonics.StepMy, Math.Max(0, first[p]));
        l.Plates = plates;
        var elevation = Isostasy.Elevation(l.Grid, l.Geometry, l.Crust, s.Tectonics, s.Elevation, noiseSeed);
        l.ElevationM = elevation;
        PresentBoundaries.Classify(l);
        PresentBoundaries.AddShelves(l, elevation, s);
        l.ElevationM = elevation;
    }

    private static int[] MemoryAsInts(ushort[] a) => a.Select(x => (int)x).ToArray();
}
