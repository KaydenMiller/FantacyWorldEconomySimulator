using WorldEcon.Rendering;
using WorldEcon.WorldGen.Pipeline;
using WorldEcon.WorldGen.Tiles;

namespace WorldEcon.Gui.ViewModels;

/// <summary>
/// What the map (<see cref="MapViewModel"/>) draws from: a world loaded from a database (<see cref="WorldSession"/>) or
/// an in-memory preview of a generation run (<see cref="PreviewSession"/>). <see cref="Regions"/> is null for a world
/// that has only globe data (a stage before the tiles), which the renderer then shows at Globe View only.
/// </summary>
public interface IMapSession : IDisposable
{
    MapWorld World { get; }

    MapRenderer Renderer { get; }

    WorldLayers Layers { get; }

    RegionSummary? Regions { get; }

    IReadOnlyList<MapMarker> Markers { get; }

    IReadOnlyList<MapRoute> RouteLines { get; }

    IReadOnlyList<MapNode> NodeMarkers { get; }
}
