namespace WorldEcon.WorldGen.Pipeline;

/// <summary>
/// Generation stages. The numeric values feed <see cref="StageSeed.For"/>, so existing values never change.
/// <see cref="WorldGenerator.Generate"/> runs Plates, Elevation, SeaLevel, Currents, Winds, Climate, then the tile stages
/// TileGrid, TileElevation, TileSeaLevel, TileClimate, TileHydrology, TileLandform, then Summary and Areas.
/// <see cref="WorldGenerator.GenerateGlobe"/> (test and tool helper) runs Plates … Climate only. Biomes and Hydrology
/// (the version 3 globe stages, removed in version 4: Summary writes their layers from the tiles) are not run by any
/// pipeline; the values stay reserved so that later values and their stage seeds do not change.
/// </summary>
public enum WorldGenStage
{
    Plates = 1,
    Elevation,
    SeaLevel,
    Currents,
    Winds,
    Climate,

    /// <summary>Reserved (version 3 globe stage, not run).</summary>
    Biomes,

    /// <summary>Reserved (version 3 globe stage, not run).</summary>
    Hydrology,

    Areas,

    /// <summary>Tile grid, region grid and the parent tables (hash 0: no generated values).</summary>
    TileGrid,
    TileElevation,
    TileSeaLevel,
    TileClimate,
    TileHydrology,
    TileLandform,

    /// <summary>Globe-cell summaries written into <see cref="WorldLayers"/> and the region-cell summary.</summary>
    Summary,
}
