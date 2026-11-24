using FluentAssertions;
using WorldEcon.Rendering;
using WorldEcon.WorldGen.Pipeline;
using WorldEcon.WorldGen.Pipeline.Checkpoints;

namespace WorldEcon.Rendering.Tests.Unit;

/// <summary>Rendering the globe layers of a world that is part-way through generation (stage viewer, <c>globe gen --png</c>).</summary>
public class StagePreviewRenderTests
{
    private sealed class Collect : IWorldGenObserver
    {
        public List<StageSnapshot> Snapshots { get; } = [];
        public void StageCompleted(StageSnapshot snapshot) => Snapshots.Add(snapshot);
        public void WorldCompleted(GeneratedWorld world) { }
    }

    private static List<StageSnapshot> Snapshots()
    {
        var c = new Collect();
        WorldGenerator.GenerateGlobe(42, new WorldGenSettings { Grid = new() { Frequency = 8 } }, new WorldGenOptions(Observer: c));
        return c.Snapshots;
    }

    [Test]
    public void Every_layer_of_every_stage_snapshot_renders()
    {
        foreach (var s in Snapshots())
        foreach (var layer in Enum.GetValues<GlobeLayer>())
        {
            var act = () => GlobePngRenderer.RenderMap(new MapWorld(s.Layers), 64, 64, 0, 0, 1, new MapDrawOptions { Layer = layer });
            act.Should().NotThrow($"{layer} at {s.Stage}");
        }
    }

    [Test]
    public void Layer_without_data_yet_is_drawn_in_the_no_data_colour()
    {
        var plates = Snapshots()[0].Layers.CloneGlobe();
        plates.ElevationM = [];
        LayerPalette.ColorOf(plates, GlobeLayer.Elevation, 0).Should().Be(LayerPalette.NoData);
        LayerPalette.ColorOf(plates, GlobeLayer.Plates, 0).Should().NotBe(LayerPalette.NoData);
    }

    [Test]
    public void Currents_base_map_falls_back_to_elevation_before_biomes_exist()
    {
        var currents = Snapshots().Single(s => s.Stage == WorldGenStage.Currents).Layers;
        LayerPalette.ColorOf(currents, GlobeLayer.Currents, 5).Should().Be(LayerPalette.ColorOf(currents, GlobeLayer.Elevation, 5));
    }

    [Test]
    public void Default_layer_per_stage_matches_the_design()
    {
        StageLayers.DefaultFor(WorldGenStage.Plates).Should().Be(GlobeLayer.Plates);
        StageLayers.DefaultFor(WorldGenStage.Elevation).Should().Be(GlobeLayer.Elevation);
        StageLayers.DefaultFor(WorldGenStage.SeaLevel).Should().Be(GlobeLayer.Elevation);
        StageLayers.DefaultFor(WorldGenStage.Currents).Should().Be(GlobeLayer.Currents);
        StageLayers.DefaultFor(WorldGenStage.Winds).Should().Be(GlobeLayer.Winds);
        StageLayers.DefaultFor(WorldGenStage.Climate).Should().Be(GlobeLayer.Temperature);
        StageLayers.DefaultFor(WorldGenStage.Summary).Should().Be(GlobeLayer.Biome);
        StageLayers.DefaultFor(WorldGenStage.Areas).Should().Be(GlobeLayer.Biome);
    }
}

public class RecordLayerTests
{
    private static WorldEcon.WorldGen.Pipeline.WorldLayers World() =>
        WorldEcon.WorldGen.Pipeline.WorldGenerator.GenerateGlobe(42, new WorldEcon.WorldGen.Pipeline.WorldGenSettings { Grid = new() { Frequency = 8 } },
            WorldEcon.WorldGen.Pipeline.WorldGenStage.Tectonics).Layers;

    [Test]
    public void Crust_age_colour_differs_between_young_ocean_and_old_craton()
    {
        var l = World();
        var young = Enumerable.Range(0, l.Grid.CellCount).First(c => l.Crust!.Kind[c] == WorldEcon.WorldGen.Tectonics.CrustKind.Oceanic && l.Crust.FormedAgeMy[c] < 20);
        var old = Enumerable.Range(0, l.Grid.CellCount).First(c => l.Crust!.FormedAgeMy[c] > 500);
        LayerPalette.ColorOf(l, GlobeLayer.CrustAge, young).Should().NotBe(LayerPalette.ColorOf(l, GlobeLayer.CrustAge, old));
    }

    [Test]
    public void Record_layers_are_no_data_without_a_record()
    {
        var l = World();
        l.Crust = null;
        foreach (var layer in new[] { GlobeLayer.CrustAge, GlobeLayer.Orogeny, GlobeLayer.Sediment })
            LayerPalette.ColorOf(l, layer, 0).Should().Be(LayerPalette.NoData);
    }

    [Test]
    public void Orogeny_layer_shows_cells_without_orogeny_in_a_neutral_colour()
    {
        var l = World();
        var none = Enumerable.Range(0, l.Grid.CellCount).First(c => l.Crust!.Orogeny[c] == WorldEcon.WorldGen.Tectonics.OrogenyKind.None);
        LayerPalette.ColorOf(l, GlobeLayer.Orogeny, none).Should().Be(LayerPalette.NoOrogeny);
    }
}
