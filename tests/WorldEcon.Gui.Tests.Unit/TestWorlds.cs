using WorldEcon.Application.Globe;
using WorldEcon.Domain.Geography;
using WorldEcon.Gui.ViewModels;
using WorldEcon.SharedKernel.Calendar;
using WorldEcon.WorldGen.Grid;
using WorldEcon.WorldGen.Pipeline;
using WorldEcon.WorldGen.Tiles;

namespace WorldEcon.Gui.Tests.Unit;

/// <summary>Small generated worlds for view-model tests (no database): Frequency 16, region ×3, tile ×4 (368,642 tiles), seed 42.</summary>
internal static class TestWorlds
{
    public static readonly WorldGenSettings Settings = new() { Grid = new() { Frequency = 16, RegionMultiplier = 3, TileMultiplier = 4 } };

    private static readonly Lazy<(LoadedGlobe Loaded, GeneratedWorld Generated)> Globe16 = new(() =>
    {
        var generated = WorldGenerator.Generate(42, Settings);
        var world = World.Create("Testland", 42UL, CalendarDefinition.Default, "1.0.0").Value;
        var land = FirstLandCell(generated.Layers);
        var tiles = generated.Tiles!.Grid;
        var tile = tiles.Nearest(generated.Layers.Geometry.Centre(land));
        return (LoadedGlobe.FromGenerated(world, generated, Settings, [new GlobeSettlement(Guid.NewGuid(), "Testburg", land, tile)]), generated);
    });

    public static LoadedGlobe Loaded => Globe16.Value.Loaded;

    public static GeneratedWorld Generated => Globe16.Value.Generated;

    public static WorldSession Session() => new(Loaded, path: null);

    /// <summary>A session whose tile blocks come from <paramref name="tiles"/> (for example a gated source).</summary>
    public static WorldSession Session(ITileBlockSource tiles) => new(Loaded with { Tiles = tiles }, path: null);

    public static int FirstLandCell(WorldLayers l) =>
        Enumerable.Range(0, l.Grid.CellCount).First(c => !l.IsOcean(c) && l.Grid.Neighbours(c).ToArray().All(n => !l.IsOcean(n)));

    public static MainViewModel MainWithWorld(double width = 800, double height = 600, WorldSession? session = null)
    {
        var vm = new MainViewModel();
        vm.UseSession(session ?? Session());
        vm.Map!.SetViewport(width, height);
        return vm;
    }
}

/// <summary>A tile source whose loads wait until <see cref="Release"/>; counts the loads.</summary>
internal sealed class GatedTileSource(ITileBlockSource inner) : ITileBlockSource
{
    private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _loads;

    public int Loads => Volatile.Read(ref _loads);

    public void Release() => _gate.TrySetResult();

    public async Task<TileBlock?> LoadAsync(int globeCell, CancellationToken ct)
    {
        Interlocked.Increment(ref _loads);
        await _gate.Task.WaitAsync(ct);
        return await inner.LoadAsync(globeCell, ct);
    }
}
