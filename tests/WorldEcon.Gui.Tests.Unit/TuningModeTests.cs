using FluentAssertions;
using Microsoft.Data.Sqlite;
using WorldEcon.Gui.ViewModels;
using WorldEcon.Rendering;
using WorldEcon.WorldGen.Pipeline;

namespace WorldEcon.Gui.Tests.Unit;

/// <summary>The main window in tuning mode: the map follows the stage viewer; Save as world opens the tuned world.</summary>
public class TuningModeTests
{
    private const string SmallJson = """{ "Grid": { "Frequency": 8, "RegionMultiplier": 2, "TileMultiplier": 2 } }""";

    private static (MainViewModel Vm, string File) Enter(string seed = "42")
    {
        var file = Path.Combine(Path.GetTempPath(), $"tune_main_{Guid.NewGuid():N}.json");
        File.WriteAllText(file, SmallJson);
        var vm = new MainViewModel();
        vm.EnterTuning(new GenerationSettingsViewModel { SettingsFile = file, SeedText = seed }, a => { a(); return Task.CompletedTask; });
        return (vm, file);
    }

    [Test]
    public async Task Map_follows_the_selected_stage_and_its_layer()
    {
        var (vm, file) = Enter();
        try
        {
            vm.IsTuning.Should().BeTrue();
            vm.ShowsMap.Should().BeTrue();
            vm.HasWorld.Should().BeFalse();
            await vm.Tuning!.RunAsync();
            vm.Map.Should().NotBeNull();
            vm.Map!.Layer.Should().Be(GlobeLayer.Biome);

            vm.Tuning.SelectedStage = WorldGenStage.Tectonics;
            vm.Map!.Layer.Should().Be(GlobeLayer.Elevation);
            vm.Map.Level.Should().Be(MapLevel.Globe);

            vm.Map.LayerIndex = (int)GlobeLayer.Plates;
            vm.Tuning.CurrentLayer.Should().Be(GlobeLayer.Plates, "a layer picked on the map is the user's layer");
            vm.Tuning.SelectedStage = WorldGenStage.Climate;
            vm.Map!.Layer.Should().Be(GlobeLayer.Plates);
        }
        finally
        {
            vm.LeaveTuning();
            File.Delete(file);
        }
    }

    [Test]
    public async Task Leaving_tuning_clears_the_map()
    {
        var (vm, file) = Enter();
        try
        {
            await vm.Tuning!.RunAsync();
            vm.LeaveTuning();
            vm.IsTuning.Should().BeFalse();
            vm.Map.Should().BeNull();
            vm.ShowsMap.Should().BeFalse();
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Test]
    public async Task Save_as_world_opens_the_tuned_world_with_its_seed()
    {
        var (vm, file) = Enter("7");
        var db = Path.Combine(Path.GetTempPath(), $"tune_main_{Guid.NewGuid():N}.db");
        try
        {
            await vm.Tuning!.RunAsync();
            var world = vm.Tuning.World!;
            (await vm.SaveTunedWorldAsync(db)).Should().BeTrue(vm.ErrorText);
            vm.IsTuning.Should().BeFalse();
            vm.HasWorld.Should().BeTrue();
            vm.Session!.Data.World.Seed.Should().Be(7UL);
            vm.Session.Layers.Grid.CellCount.Should().Be(world.Layers.Grid.CellCount);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(file);
            File.Delete(db);
        }
    }
}
