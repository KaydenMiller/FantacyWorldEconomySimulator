using FluentAssertions;
using WorldEcon.Gui.ViewModels;
using WorldEcon.Rendering;
using WorldEcon.WorldGen.Pipeline;

namespace WorldEcon.Gui.Tests.Unit;

/// <summary>Tuning mode: live stage snapshots, stepping, rerun from a kept stage, checkpoints.</summary>
public class StageViewerViewModelTests
{
    private const string SmallJson = """{ "Grid": { "Frequency": 8, "RegionMultiplier": 2, "TileMultiplier": 2 } }""";

    private static readonly WorldGenStage[] Globe =
        [WorldGenStage.Tectonics, WorldGenStage.SeaLevel, WorldGenStage.Currents, WorldGenStage.Winds, WorldGenStage.Climate];

    private sealed class Fixture : IDisposable
    {
        public string File { get; } = Path.Combine(Path.GetTempPath(), $"tune_{Guid.NewGuid():N}.json");
        public GenerationSettingsViewModel Settings { get; }
        public StageViewerViewModel Viewer { get; }

        public Fixture(string json = SmallJson)
        {
            System.IO.File.WriteAllText(File, json);
            Settings = new GenerationSettingsViewModel { SettingsFile = File };
            Viewer = new StageViewerViewModel(Settings, a => { a(); return Task.CompletedTask; });
        }

        public void Write(string json) => System.IO.File.WriteAllText(File, json);

        public void Dispose()
        {
            Viewer.Dispose();
            System.IO.File.Delete(File);
        }
    }

    private static ulong HashOf(StageViewerViewModel v, WorldGenStage s) => v.SnapshotOf(s)!.Hash;

    [Test]
    public async Task Run_marks_stages_done_in_order_and_sets_default_layers()
    {
        using var f = new Fixture();
        var layers = new List<(WorldGenStage? Stage, GlobeLayer Layer)>();
        f.Viewer.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(StageViewerViewModel.CurrentSession)) layers.Add((f.Viewer.SelectedStage, f.Viewer.CurrentLayer));
        };

        await f.Viewer.RunAsync();

        f.Viewer.Error.Should().BeNull();
        layers.Select(x => x.Layer).Should().Equal(
            GlobeLayer.Elevation, GlobeLayer.Elevation, GlobeLayer.Currents, GlobeLayer.Winds, GlobeLayer.Temperature, GlobeLayer.Biome);
        f.Viewer.Stages.Select(r => r.State).Should().OnlyContain(s => s == StageState.Done);
        f.Viewer.Stages.Select(r => r.Stage).Should().Equal([.. Globe, WorldGenStage.Summary]);
        f.Viewer.SelectedStage.Should().Be(WorldGenStage.Summary);
        f.Viewer.CurrentSession!.Regions.Should().NotBeNull("the finished world has region data");
        f.Viewer.IsRunning.Should().BeFalse();
        f.Viewer.CanSaveAsWorld.Should().BeTrue();
    }

    [Test]
    public async Task Selecting_a_stage_shows_its_snapshot_and_keeps_a_user_chosen_layer()
    {
        using var f = new Fixture();
        await f.Viewer.RunAsync();
        f.Viewer.SelectedStage = WorldGenStage.Tectonics;
        f.Viewer.CurrentSession!.Regions.Should().BeNull();
        f.Viewer.CurrentLayer.Should().Be(GlobeLayer.Elevation);
        f.Viewer.CurrentLayer = GlobeLayer.Plates;
        f.Viewer.SelectedStage = WorldGenStage.Climate;
        f.Viewer.CurrentLayer.Should().Be(GlobeLayer.Plates, "a layer the user picked is kept while stepping");
    }

    [Test]
    public async Task Run_while_running_is_refused()
    {
        using var f = new Fixture();
        var gate = new TaskCompletionSource();
        var viewer = new StageViewerViewModel(f.Settings, async a => { await gate.Task; a(); });
        var first = viewer.RunAsync();
        viewer.IsRunning.Should().BeTrue();
        viewer.CanRun.Should().BeFalse();
        viewer.CanRerun.Should().BeFalse();
        (await viewer.RunAsync()).Should().BeFalse("a second run does not start");
        gate.SetResult();
        (await first).Should().BeTrue();
        viewer.Dispose();
    }

    [Test]
    public async Task Rerun_from_Tectonics_keeps_it_and_reruns_the_rest()
    {
        using var f = new Fixture();
        await f.Viewer.RunAsync();
        var plates = HashOf(f.Viewer, WorldGenStage.Tectonics);
        var elevation = HashOf(f.Viewer, WorldGenStage.Winds);

        f.Viewer.SelectedStage = WorldGenStage.Tectonics;
        f.Write("""{ "Grid": { "Frequency": 8, "RegionMultiplier": 2, "TileMultiplier": 2 }, "Winds": { "PeakStrength": 200 } }""");
        f.Viewer.Reload().Should().BeTrue();
        (await f.Viewer.RerunFromSelectedAsync()).Should().BeTrue(f.Viewer.Error);

        f.Viewer.Stages[0].State.Should().Be(StageState.Loaded);
        f.Viewer.Stages.Skip(1).Should().OnlyContain(r => r.State == StageState.Done);
        HashOf(f.Viewer, WorldGenStage.Tectonics).Should().Be(plates);
        HashOf(f.Viewer, WorldGenStage.Winds).Should().NotBe(elevation);
    }

    [Test]
    public async Task Rerun_after_changing_an_upstream_section_sets_error_and_keeps_snapshots()
    {
        using var f = new Fixture();
        await f.Viewer.RunAsync();
        var climate = HashOf(f.Viewer, WorldGenStage.Climate);

        f.Viewer.SelectedStage = WorldGenStage.Tectonics;
        f.Write("""{ "Grid": { "Frequency": 8, "RegionMultiplier": 2, "TileMultiplier": 2 }, "SeaLevel": { "LandFractionBp": 3200 } }""");
        f.Viewer.Reload().Should().BeTrue();
        (await f.Viewer.RerunFromSelectedAsync()).Should().BeFalse();

        f.Viewer.Error.Should().Contain("Tectonics: section SeaLevel differs");
        HashOf(f.Viewer, WorldGenStage.Climate).Should().Be(climate);
        f.Viewer.Stages.Should().OnlyContain(r => r.State == StageState.Done);
    }

    [Test]
    public async Task Reload_with_invalid_file_keeps_previous_settings_and_snapshots()
    {
        using var f = new Fixture();
        await f.Viewer.RunAsync();
        var climate = HashOf(f.Viewer, WorldGenStage.Climate);

        f.Write("""{ "Plates": { "Nope": 1 } }""");
        f.Viewer.Reload().Should().BeFalse();
        f.Viewer.Error.Should().Contain("Nope");
        HashOf(f.Viewer, WorldGenStage.Climate).Should().Be(climate);
        f.Viewer.SelectedStage = WorldGenStage.Climate;
        f.Viewer.CanRerun.Should().BeTrue("the snapshots are still there");
    }

    [Test]
    public async Task Save_then_open_checkpoint_shows_loaded_stages()
    {
        using var f = new Fixture();
        await f.Viewer.RunAsync();
        var winds = HashOf(f.Viewer, WorldGenStage.Winds);
        f.Viewer.SelectedStage = WorldGenStage.Winds;
        f.Viewer.CanSaveCheckpoint.Should().BeTrue();
        var path = Path.Combine(Path.GetTempPath(), $"tune_{Guid.NewGuid():N}.wgckpt");
        try
        {
            f.Viewer.SaveCheckpoint(path);

            using var g = new Fixture();
            (await g.Viewer.OpenCheckpointAsync(path)).Should().BeTrue(g.Viewer.Error);
            g.Viewer.Stages.Take(4).Should().OnlyContain(r => r.State == StageState.Loaded);
            g.Viewer.Stages.Skip(4).Should().OnlyContain(r => r.State == StageState.Done);
            HashOf(g.Viewer, WorldGenStage.Winds).Should().Be(winds);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task Save_checkpoint_is_disabled_for_the_finished_world_row()
    {
        using var f = new Fixture();
        await f.Viewer.RunAsync();
        f.Viewer.SelectedStage = WorldGenStage.Summary;
        f.Viewer.CanSaveCheckpoint.Should().BeFalse();
        f.Viewer.CanRerun.Should().BeFalse();
    }

    [Test]
    public async Task Open_of_a_corrupt_checkpoint_sets_error()
    {
        using var f = new Fixture();
        var path = Path.Combine(Path.GetTempPath(), $"tune_{Guid.NewGuid():N}.wgckpt");
        File.WriteAllText(path, "garbage");
        try
        {
            (await f.Viewer.OpenCheckpointAsync(path)).Should().BeFalse();
            f.Viewer.Error.Should().Contain("not a checkpoint file");
        }
        finally
        {
            File.Delete(path);
        }
    }
}

public class StageViewerBindingTests
{
    [Test]
    public void Layer_index_and_frame_value_mirror_the_typed_properties()
    {
        var file = Path.Combine(Path.GetTempPath(), $"tune_b_{Guid.NewGuid():N}.json");
        File.WriteAllText(file, """{ "Grid": { "Frequency": 8, "RegionMultiplier": 2, "TileMultiplier": 2 } }""");
        try
        {
            using var v = new StageViewerViewModel(new GenerationSettingsViewModel { SettingsFile = file }, a => { a(); return Task.CompletedTask; });
            v.LayerNames.Should().Equal(Enum.GetNames<GlobeLayer>());
            v.CurrentLayerIndex = (int)GlobeLayer.Winds;
            v.CurrentLayer.Should().Be(GlobeLayer.Winds);
            v.CurrentLayerIndex = 999;
            v.CurrentLayer.Should().Be(GlobeLayer.Winds, "an index outside the list is ignored");
            v.SelectedStageName.Should().Be("none");
            v.SelectedFrameValue.Should().Be(0);
        }
        finally
        {
            File.Delete(file);
        }
    }
}

public class StageViewerProgressTests
{
    [Test]
    public async Task Progress_text_shows_the_time_remaining_during_the_run_and_clears_after()
    {
        var file = Path.Combine(Path.GetTempPath(), $"tune_p_{Guid.NewGuid():N}.json");
        File.WriteAllText(file, """{ "Grid": { "Frequency": 8, "RegionMultiplier": 2, "TileMultiplier": 2 } }""");
        try
        {
            using var v = new StageViewerViewModel(new GenerationSettingsViewModel { SettingsFile = file }, a => { a(); return Task.CompletedTask; });
            var texts = new List<string>();
            v.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(StageViewerViewModel.ProgressText) && v.ProgressText is { } t) texts.Add(t); };
            await v.RunAsync();
            texts.Should().Contain(t => t.StartsWith("Tectonics…") && (t.Contains("remaining") || t.Contains("estimating")));
            v.ProgressText.Should().BeNull();
        }
        finally
        {
            File.Delete(file);
        }
    }
}

public class StageViewerMetricsTests
{
    [Test]
    public async Task Metrics_text_appears_after_the_world_completes()
    {
        var file = Path.Combine(Path.GetTempPath(), $"tune_m_{Guid.NewGuid():N}.json");
        File.WriteAllText(file, """{ "Grid": { "Frequency": 8, "RegionMultiplier": 2, "TileMultiplier": 2 } }""");
        try
        {
            using var v = new StageViewerViewModel(new GenerationSettingsViewModel { SettingsFile = file }, a => { a(); return Task.CompletedTask; });
            v.MetricsText.Should().BeNull();
            await v.RunAsync();
            v.MetricsText.Should().Contain("Landmasses");
        }
        finally
        {
            File.Delete(file);
        }
    }
}
