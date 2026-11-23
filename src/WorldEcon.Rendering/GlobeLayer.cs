namespace WorldEcon.Rendering;

/// <summary>The data layers a globe PNG can show.</summary>
public enum GlobeLayer
{
    Plates,
    Elevation,
    Currents,
    Winds,
    Temperature,
    Precipitation,
    Biome,
    Hydrology,
    Areas,
    Settlements,

    /// <summary>Landform: the largest composition share of a globe cell, the region cell's drawing kind, the tile landform.</summary>
    Landform,
}
