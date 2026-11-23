using System.Diagnostics;
using WorldEcon.Simulation.Random;
using WorldEcon.WorldGen.Grid;
using WorldEcon.WorldGen.Pipeline;

namespace WorldEcon.WorldGen.Tiles;

/// <summary>Timing and output hash of one tile stage (<see cref="TileStages.RunTerrainAndClimate"/>).</summary>
public sealed record TileStageResult(string Stage, ulong Hash, TimeSpan Duration);

/// <summary>
/// Entry point for the tile stages that follow the globe Climate stage: grid and parent tables, then TileElevation,
/// TileSeaLevel, TileClimate (<see cref="RunTerrainAndClimate(WorldLayers, WorldGenSettings, ulong, ParallelOptions?)"/>)
/// and TileHydrology (<see cref="Run(WorldLayers, WorldGenSettings, ulong, ParallelOptions?)"/> runs all of them).
/// <see cref="WorldGenerator.Generate"/> calls the stages one by one (to time and hash each) and adds TileLandform and the
/// summaries; these entry points serve tests and tools that need only the tile terrain and hydrology.
/// </summary>
public static class TileStages
{
    private const ulong TileGenTag = 0x54494C4547454E00UL; // "TILEGEN\0"

    /// <summary>Seed of tile stage <paramref name="stage"/> (1 = TileElevation), independent of the globe stage seeds.</summary>
    public static ulong SeedFor(ulong worldSeed, int stage) => DeterministicHash.Combine(worldSeed, TileGenTag, (ulong)stage, 0);

    /// <summary>
    /// Builds the tile grid (Frequency × RegionMultiplier × TileMultiplier), the region level and the parent tables
    /// tile → globe, tile → region and region → globe.
    /// </summary>
    public static TileLayers CreateLayers(WorldLayers globe, WorldGenSettings settings)
    {
        ArgumentNullException.ThrowIfNull(globe);
        ArgumentNullException.ThrowIfNull(settings);
        var g = settings.Grid;
        if (globe.Grid.Frequency != g.Frequency)
            throw new ArgumentException($"Globe frequency {globe.Grid.Frequency} differs from Grid.Frequency {g.Frequency}.", nameof(settings));
        if (!TileGrid.IsSupportedFrequency(g.Frequency, g.RegionMultiplier, g.TileMultiplier))
            throw new ArgumentException(
                $"Frequency {g.Frequency} × {g.RegionMultiplier} × {g.TileMultiplier} exceeds {TileGrid.MaxFrequency}.", nameof(settings));

        int regionFrequency = g.Frequency * g.RegionMultiplier;
        var globeTiles = new TileGrid(g.Frequency);
        var regionTiles = new TileGrid(regionFrequency);
        var tiles = new TileGrid(regionFrequency * g.TileMultiplier);
        var regionGrid = GlobeGrid.Build(regionFrequency);
        var regionGeometry = GlobeGeometry.Compute(regionGrid);
        return new TileLayers(
            tiles, globeTiles, regionGrid, regionGeometry,
            globeParent: GridHierarchy.Parents(tiles, globeTiles),
            regionParent: GridHierarchy.Parents(tiles, regionTiles),
            regionGlobeParent: GridHierarchy.Parents(regionTiles, globeTiles))
        {
            PlanetRadiusKm = g.PlanetRadiusKm,
        };
    }

    /// <summary>
    /// Runs grid setup, TileElevation, TileSeaLevel and TileClimate on globe layers that have completed the Climate stage.
    /// Output does not depend on <paramref name="options"/> (thread count).
    /// </summary>
    public static TileLayers RunTerrainAndClimate(WorldLayers globe, WorldGenSettings settings, ulong worldSeed, ParallelOptions? options = null) =>
        RunTerrainAndClimate(globe, settings, worldSeed, out _, options);

    /// <inheritdoc cref="RunTerrainAndClimate(WorldLayers, WorldGenSettings, ulong, ParallelOptions?)"/>
    /// <param name="results">One entry per step ("Grid", "TileElevation", "TileSeaLevel", "TileClimate"); Grid has hash 0. Durations exclude hashing.</param>
    public static TileLayers RunTerrainAndClimate(WorldLayers globe, WorldGenSettings settings, ulong worldSeed,
        out IReadOnlyList<TileStageResult> results, ParallelOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(globe);
        if (globe.TemperatureDeciC.Length != globe.Grid.CellCount || globe.WindDir.Length != globe.Grid.CellCount)
            throw new ArgumentException("Globe layers must have completed the Winds and Climate stages.", nameof(globe));

        var list = new List<TileStageResult>(4);
        var sw = Stopwatch.StartNew();
        var tiles = CreateLayers(globe, settings);
        list.Add(new TileStageResult("Grid", 0, sw.Elapsed));

        sw.Restart();
        TileElevationStage.Run(tiles, globe, settings, SeedFor(worldSeed, 1), options);
        var tileElevationTime = sw.Elapsed;
        list.Add(new TileStageResult("TileElevation", TileElevationStage.Hash(tiles), tileElevationTime));

        sw.Restart();
        TileSeaLevelStage.Run(tiles, settings, options);
        var tileSeaLevelTime = sw.Elapsed;
        list.Add(new TileStageResult("TileSeaLevel", TileSeaLevelStage.Hash(tiles), tileSeaLevelTime));

        sw.Restart();
        TileClimateStage.Run(tiles, globe, settings, options);
        var tileClimateTime = sw.Elapsed;
        list.Add(new TileStageResult("TileClimate", TileClimateStage.Hash(tiles), tileClimateTime));

        results = list;
        return tiles;
    }

    /// <summary>
    /// Runs every tile stage: <see cref="RunTerrainAndClimate(WorldLayers, WorldGenSettings, ulong, ParallelOptions?)"/>
    /// then TileHydrology. Output does not depend on <paramref name="options"/> (thread count).
    /// </summary>
    public static TileLayers Run(WorldLayers globe, WorldGenSettings settings, ulong worldSeed, ParallelOptions? options = null) =>
        Run(globe, settings, worldSeed, out _, options);

    /// <inheritdoc cref="Run(WorldLayers, WorldGenSettings, ulong, ParallelOptions?)"/>
    /// <param name="results">The <see cref="RunTerrainAndClimate(WorldLayers, WorldGenSettings, ulong, out IReadOnlyList{TileStageResult}, ParallelOptions?)"/> entries followed by "TileHydrology".</param>
    public static TileLayers Run(WorldLayers globe, WorldGenSettings settings, ulong worldSeed,
        out IReadOnlyList<TileStageResult> results, ParallelOptions? options = null)
    {
        var tiles = RunTerrainAndClimate(globe, settings, worldSeed, out var terrain, options);
        var list = new List<TileStageResult>(terrain);
        var sw = Stopwatch.StartNew();
        TileHydrologyStage.Run(tiles, settings, options);
        var hydrologyTime = sw.Elapsed;
        list.Add(new TileStageResult("TileHydrology", TileHydrologyStage.Hash(tiles), hydrologyTime));
        results = list;
        return tiles;
    }
}
