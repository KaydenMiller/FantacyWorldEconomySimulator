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

    /// <summary>Tectonics record: when the crust formed (ocean young → old, continental crust by age).</summary>
    CrustAge,

    /// <summary>Tectonics record: kind of the last mountain building, brighter when younger.</summary>
    Orogeny,

    /// <summary>Tectonics record: sediment thickness.</summary>
    Sediment,
}
