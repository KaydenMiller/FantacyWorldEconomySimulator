using FluentAssertions;
using WorldEcon.Gui.ViewModels;
using WorldEcon.Rendering;
using WorldEcon.WorldGen.Pipeline;
using WorldEcon.WorldGen.Pipeline.Checkpoints;

namespace WorldEcon.Gui.Tests.Unit;

/// <summary>The map over an in-memory preview (tuning mode) instead of a world database.</summary>
public class PreviewSessionTests
{
    private sealed class Collect : IWorldGenObserver
    {
        public List<StageSnapshot> Snapshots { get; } = [];
        public GeneratedWorld? World { get; private set; }
        public void StageCompleted(StageSnapshot snapshot) => Snapshots.Add(snapshot);
        public void WorldCompleted(GeneratedWorld world) => World = world;
    }

    private static readonly Lazy<Collect> Run = new(() =>
    {
        var c = new Collect();
        WorldGenerator.Generate(42, TestWorlds.Settings, new WorldGenOptions(Observer: c));
        return c;
    });

    [Test]
    public void Preview_of_a_Plates_snapshot_draws_globe_view_only()
    {
        using var session = PreviewSession.FromSnapshot(Run.Value.Snapshots[0]);
        session.Regions.Should().BeNull();
        session.Markers.Should().BeEmpty();
        var map = new MapViewModel(session);
        map.SetViewport(400, 300);
        map.ZoomToLevel(MapLevel.Detail);
        map.Level.Should().Be(MapLevel.Globe);
        map.LayerIndex = (int)GlobeLayer.Elevation; // no data yet at Plates: drawn as no data, not a crash
        var act = () => GlobePngRenderer.RenderMap(map.Renderer, map.View, map.DrawOptions);
        act.Should().NotThrow();
    }

    [Test]
    public void Preview_of_a_finished_world_allows_detail_view()
    {
        using var session = PreviewSession.FromWorld(Run.Value.World!, TestWorlds.Settings);
        session.Regions.Should().NotBeNull();
        var map = new MapViewModel(session);
        map.SetViewport(400, 300);
        map.ZoomToLevel(MapLevel.Detail);
        map.Level.Should().Be(MapLevel.Detail);
    }

    [Test]
    public void World_session_is_a_map_session()
    {
        IMapSession session = TestWorlds.Session();
        session.Regions.Should().NotBeNull();
        session.Markers.Should().NotBeEmpty();
    }
}

public class PreviewGpuTests
{
    [Test]
    public void Stage_previews_without_imagery_are_drawn_on_the_cpu()
    {
        var c = new System.Collections.Generic.List<StageSnapshot>();
        var obs = new ObserverList(c);
        var world = WorldGenerator.Generate(42, TestWorlds.Settings, new WorldGenOptions(Observer: obs));
        using var stage = PreviewSession.FromSnapshot(c[0]);
        new MapViewModel(stage).GpuAllowed.Should().BeFalse("a stage snapshot has no map imagery for the GPU scene");
        using var full = PreviewSession.FromWorld(world, TestWorlds.Settings);
        new MapViewModel(full).GpuAllowed.Should().BeTrue();
        new MapViewModel(TestWorlds.Session()).GpuAllowed.Should().BeTrue();
    }

    private sealed class ObserverList(System.Collections.Generic.List<StageSnapshot> list) : IWorldGenObserver
    {
        public void StageCompleted(StageSnapshot snapshot) => list.Add(snapshot);
        public void WorldCompleted(GeneratedWorld world) { }
    }
}
