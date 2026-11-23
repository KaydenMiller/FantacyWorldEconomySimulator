namespace WorldEcon.Rendering;

/// <summary>A settlement (or other) marker: drawn at the globe cell centre at Globe View and on its tile at Region and Detail View.</summary>
/// <param name="GlobeCell">Globe cell of the marker.</param>
/// <param name="TileId">Tile of the marker; −1 places it at the globe cell centre at every level.</param>
/// <param name="Label">Label text; empty for a dot only.</param>
public readonly record struct MapMarker(int GlobeCell, int TileId, string Label);

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

    /// <summary>Marker dots.</summary>
    public bool Settlements { get; init; } = true;

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
