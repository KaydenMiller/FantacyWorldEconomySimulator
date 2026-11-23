namespace WorldEcon.WorldGen.Pipeline;

/// <summary>Stage 7 main type of a cell: what the globe map shows. Stored as byte in <see cref="WorldLayers.BiomeOf"/>.</summary>
public enum Biome : byte
{
    Ocean,
    Lake,
    Ice,
    Tundra,
    Taiga,
    TemperateForest,
    Grassland,
    Shrubland,
    Desert,
    Savanna,
    TropicalForest,
    Swamp,
    Mountain,
    Hills,
}

/// <summary>Landform of a tile (TileLandform stage) and of a globe cell's composition entries (tile counts per landform).</summary>
public enum Landform : byte
{
    Ocean,
    Lake,
    DryLakeBed,
    Mountain,
    Hills,
    Plain,
    Basin,
    Valley,
    Wetland,
}

/// <summary>One composition entry: a landform and its share of the cell in basis points (10,000 = 100 %).</summary>
public readonly record struct CompositionEntry(Landform Landform, short ShareBp);

/// <summary>Stage 8 surface-water classification of a cell. Stored as byte in <see cref="WorldLayers.WaterBodyKind"/>.</summary>
public enum WaterBodyKind : byte
{
    None,
    River,
    Lake,
    DryLakeBed,
}

/// <summary>
/// Stage 8 river step across the edge shared by two neighbouring cells, stored on the unordered pair
/// (<paramref name="LowCell"/> &lt; <paramref name="HighCell"/>). <paramref name="FlowsLowToHigh"/> is true when water
/// moves from <paramref name="LowCell"/> to <paramref name="HighCell"/>. <paramref name="Flow"/> is the accumulated
/// flow leaving the upstream cell, in mm·km² per year (precipitation in mm/year × cell area in km²).
/// </summary>
public readonly record struct RiverEdge(int LowCell, int HighCell, bool FlowsLowToHigh, long Flow);
