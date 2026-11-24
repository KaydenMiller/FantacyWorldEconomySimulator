using System.Diagnostics;
using WorldEcon.WorldGen.Grid;
using WorldEcon.WorldGen.Pipeline;
using WorldEcon.WorldGen.Stages;
using WorldEcon.WorldGen.Tectonics;

namespace WorldEcon.WorldGen.Tests.Unit.Tectonics;

/// <summary>
/// The plan's performance gate (Task 2): time of the move step (advance + claim) with 20 plates, projected to 125 steps.
/// Explicit: run with <c>--treenode-filter "/*/*/MotionBenchmark/*"</c>.
/// </summary>
[Explicit]
[NotInParallel]
public class MotionBenchmark
{
    [Test]
    [Arguments(64)]
    [Arguments(256)]
    public void Move_step_time(int frequency)
    {
        var settings = new WorldGenSettings { Grid = new() { Frequency = frequency } };
        var grid = GlobeGrid.Build(frequency);
        var layers = new WorldLayers(grid, GlobeGeometry.Compute(grid));
        PlateStage.Run(layers, settings, 42);
        var frames = new PlateFrames(grid, layers.Geometry, layers.PlateOf);
        for (var p = 0; p < frames.PlateCount; p++) frames.SetAxis(p, layers.Plates[p].RotationAxis);
        frames.Claim(); // warm-up
        var sw = Stopwatch.StartNew();
        const int steps = 20;
        for (var s = 0; s < steps; s++)
        {
            for (var p = 0; p < frames.PlateCount; p++) frames.Advance(p, 7_000);
            frames.Claim();
        }

        var perStep = sw.Elapsed.TotalMilliseconds / steps;
        TestContext.Current!.Output.WriteLine($"frequency {frequency}, {grid.CellCount:N0} cells: {perStep:F1} ms per move step, projected {perStep * 125 / 1000:F1} s for 125 steps");
    }
}
