using WorldEcon.Rendering;
using WorldEcon.WorldGen.Pipeline;
using WorldEcon.WorldGen.Pipeline.Checkpoints;
using WorldEcon.WorldGen.Tiles;

namespace WorldEcon.Gui.ViewModels;

/// <summary>
/// A generation run held in memory, for the tuning mode's map: one globe stage snapshot (globe data only), or the
/// finished world with its in-memory tiles. It has no settlements, routes or nodes.
/// </summary>
public sealed class PreviewSession : IMapSession
{
    private PreviewSession(MapWorld world)
    {
        World = world;
        Renderer = new MapRenderer(world);
    }

    /// <summary>The globe layers of a stage snapshot (Globe View only).</summary>
    public static PreviewSession FromSnapshot(StageSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new PreviewSession(new MapWorld(snapshot.Layers));
    }

    /// <summary>A finished run: globe summaries, region summary and in-memory tiles.</summary>
    public static PreviewSession FromWorld(GeneratedWorld world, WorldGenSettings settings) =>
        new(MapWorld.FromGenerated(world, settings));

    public MapWorld World { get; }

    public MapRenderer Renderer { get; }

    public WorldLayers Layers => World.Globe;

    public RegionSummary? Regions => World.Regions;

    public IReadOnlyList<MapMarker> Markers { get; } = [];

    public IReadOnlyList<MapRoute> RouteLines { get; } = [];

    public IReadOnlyList<MapNode> NodeMarkers { get; } = [];

    public void Dispose() => Renderer.Dispose();
}
