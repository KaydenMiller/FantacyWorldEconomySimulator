using WorldEcon.WorldGen.Grid;

namespace WorldEcon.Rendering;

/// <summary>A settlement (or other) marker: drawn at the globe cell centre at Globe View and on its tile at Region and Detail View.</summary>
/// <param name="GlobeCell">Globe cell of the marker.</param>
/// <param name="TileId">Tile of the marker; −1 places it at the globe cell centre at every level.</param>
/// <param name="Label">Label text; empty for a dot only.</param>
/// <param name="Port">Port kind: a ring around the dot (thin for a river port, thick for a lake or ocean port).</param>
/// <param name="Id">Settlement id, returned by <see cref="MapRenderer.PickObjects"/>; empty when not pickable.</param>
public readonly record struct MapMarker(int GlobeCell, int TileId, string Label, MapPortKind Port = MapPortKind.None, Guid Id = default);

/// <summary>Kind of a map object that can be picked: a caravan, a settlement marker or a junction/site node.</summary>
public enum MapObjectKind { Caravan = 0, Settlement = 1, Node = 2 }

/// <summary>A picked or selected map object.</summary>
public readonly record struct MapObject(MapObjectKind Kind, Guid Id);

/// <summary>Port kind of a marker: none, river port, or lake/ocean port.</summary>
public enum MapPortKind { None = 0, River = 1, Coastal = 2 }

/// <summary>Medium of a drawn route: Land (solid), Sea (dashed) or Air (dotted).</summary>
public enum MapRouteKind { Land = 0, Sea = 1, Air = 2 }

/// <summary>A route drawn as a line through its tile centres, from the first tile to the last.</summary>
/// <param name="RouteId">Id of the route in the travel graph (caravan legs name it).</param>
/// <param name="Kind">Land, Sea or Air; picks the line style.</param>
/// <param name="Tiles">Tile path (tile ids of the world's tile grid), at least two tiles.</param>
public sealed record MapRoute(Guid RouteId, MapRouteKind Kind, IReadOnlyList<int> Tiles);

/// <summary>A caravan on a route: where it is on the unit sphere, which way it travels (unit tangent) and the route's medium.</summary>
public readonly record struct MapCaravan(Guid Id, Vector3d Position, Vector3d Heading, MapRouteKind Kind);

/// <summary>Kind of a drawn route node that is not a settlement.</summary>
public enum MapNodeKind { Junction = 0, Site = 1 }

/// <summary>A junction or site drawn on its tile (at its globe cell centre at Globe View).</summary>
/// <param name="Id">Route node id.</param>
/// <param name="Kind">Junction (small circle on the line) or Site (diamond).</param>
/// <param name="GlobeCell">Globe cell of the node's tile.</param>
/// <param name="TileId">Tile of the node.</param>
/// <param name="Label">Name, or null for an unnamed junction (no label).</param>
public sealed record MapNode(Guid Id, MapNodeKind Kind, int GlobeCell, int TileId, string? Label);

/// <summary>
/// What is under a pixel at the level drawn there (<see cref="MapRenderer.Pick"/>), also used to outline a selection.
/// </summary>
/// <param name="Level">Level of <see cref="Id"/>: Globe (globe cell id), Region (region cell id) or Detail (tile id).</param>
/// <param name="Id">Globe cell, region cell or tile id.</param>
/// <param name="GlobeCell">Globe cell whose group the item is drawn in (for a tile: its tile-level parent; for a region cell: its region-level parent).</param>
/// <param name="RegionCell">Region cell of the item (for a globe cell: the region cell nearest the picked point); −1 when the world has no region level.</param>
public readonly record struct MapPick(MapLevel Level, int Id, int GlobeCell, int RegionCell);

/// <summary>What <see cref="MapRenderer.Draw"/> paints: the colour layer, the overlay toggles (spec §11), markers and selection.</summary>
public sealed record MapDrawOptions
{
    /// <summary>Colour layer; applies at every level (layers without tile data use the parent globe cell's colour).</summary>
    public GlobeLayer Layer { get; init; } = GlobeLayer.Biome;

    /// <summary>Gaps (off by default: the map is one continuous picture): 1 px between globe cells at Globe View; 4 px between globe-cell groups and 1 px between cells at Region and Detail View.</summary>
    public bool Borders { get; init; }

    /// <summary>Outlines of every visible hex of the drawn level (globe cells, region cells or tiles) over the imagery.</summary>
    public bool HexGrid { get; init; }

    /// <summary>Major rivers at Globe and Region View, all rivers at Detail View.</summary>
    public bool Rivers { get; init; } = true;

    /// <summary>
    /// Chevrons pointing downstream, one per 120 px of river length (<see cref="MapRenderer.RiverArrowSpacingPx"/>), on
    /// rivers with flow ≥ <see cref="MapWorld.MajorRiverFlow"/>; drawn only with <see cref="Rivers"/>.
    /// </summary>
    public bool RiverArrows { get; init; }

    /// <summary>
    /// A hollow dot at each source and a filled dot where a river meets ocean or lake, on the same rivers as
    /// <see cref="RiverArrows"/>; drawn only with <see cref="Rivers"/>. Confluences get no marker.
    /// </summary>
    public bool RiverEnds { get; init; }

    /// <summary>Large lakes drawn from their region cells at Globe View.</summary>
    public bool Lakes { get; init; } = true;

    /// <summary>Triangle glyphs on globe cells (Globe View) and region cells (Region View) with mountain share ≥ 2,500 basis points, except cells drawn as water.</summary>
    public bool MountainSymbols { get; init; } = true;

    /// <summary>Route lines (<see cref="RouteLines"/>) under the markers, at every level: the master switch of the Routes group.</summary>
    public bool Routes { get; init; } = true;

    /// <summary>Land roads (solid), when <see cref="Routes"/> is on.</summary>
    public bool LandRoutes { get; init; } = true;

    /// <summary>Voyages between ports (dashed, translucent), when <see cref="Routes"/> is on.</summary>
    public bool SeaRoutes { get; init; } = true;

    /// <summary>Air routes (dotted), when <see cref="Routes"/> is on.</summary>
    public bool AirRoutes { get; init; } = true;

    /// <summary>Routes to draw when <see cref="Routes"/> is on.</summary>
    public IReadOnlyList<MapRoute>? RouteLines { get; init; }

    /// <summary>Marker dots.</summary>
    public bool Settlements { get; init; } = true;

    /// <summary>Caravan markers (<see cref="CaravanMarkers"/>), when <see cref="Routes"/> and the caravan's category are on.</summary>
    public bool Caravans { get; init; } = true;

    /// <summary>Caravans on routes to draw.</summary>
    public IReadOnlyList<MapCaravan>? CaravanMarkers { get; init; }

    /// <summary>Junction circles at Region and Detail View (off by default), when <see cref="Routes"/> is on.</summary>
    public bool Junctions { get; init; }

    /// <summary>Site diamonds at every level, when <see cref="Routes"/> is on.</summary>
    public bool Sites { get; init; } = true;

    /// <summary>Junctions and sites to draw.</summary>
    public IReadOnlyList<MapNode>? NodeMarkers { get; init; }

    /// <summary>Selected caravan, settlement or node: a ring around it.</summary>
    public MapObject? SelectedObject { get; init; }

    /// <summary>Route drawn with a yellow casing (the selected caravan's route, or a route chosen in a panel).</summary>
    public Guid? HighlightedRoute { get; init; }

    /// <summary>Marker labels (decluttered by <see cref="LabelLayout"/>); drawn only with <see cref="Settlements"/>.</summary>
    public bool Labels { get; init; } = true;

    /// <summary>Wind or current arrows at Globe View when <see cref="Layer"/> is Winds or Currents.</summary>
    public bool Arrows { get; init; } = true;

    /// <summary>The equator (latitude 0), drawn wider and brighter than the <see cref="Graticule"/> lines, at every level.</summary>
    public bool Equator { get; init; } = true;

    /// <summary>
    /// Latitude and longitude lines at every level, spaced by <see cref="MapRenderer.GraticuleStepDeg"/> (30° down to 1° as
    /// the view zooms in). Drawn under the <see cref="Equator"/>; latitude 0 is drawn as a plain line only when
    /// <see cref="Equator"/> is off.
    /// </summary>
    public bool Graticule { get; init; }

    /// <summary>Fixed directional light on the sphere, fading out as the view flattens; false gives exact palette colours.</summary>
    public bool Shaded { get; init; }

    public IReadOnlyList<MapMarker>? Markers { get; init; }

    /// <summary>Item outlined as the current selection (drawn only when its level is the level drawn there).</summary>
    public MapPick? Selected { get; init; }

    /// <summary>Item outlined as under the pointer.</summary>
    public MapPick? Hover { get; init; }

    /// <summary>Globe cells tinted with <see cref="MapRenderer.HighlightTint"/>; at Region and Detail View the cells drawn in their groups are tinted.</summary>
    public IReadOnlySet<int>? HighlightedGlobeCells { get; init; }
}
