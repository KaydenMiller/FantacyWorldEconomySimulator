using System.Diagnostics;
using WorldEcon.WorldGen.Grid;
using WorldEcon.WorldGen.Stages;
using WorldEcon.WorldGen.Tiles;

namespace WorldEcon.WorldGen.Pipeline;

/// <summary>Timing and output hash for one executed stage.</summary>
public sealed record StageResult(WorldGenStage Stage, ulong Hash, TimeSpan Duration);

/// <summary>
/// The output of <see cref="WorldGenerator.Generate"/> (or of the climate-only helper <see cref="WorldGenerator.GenerateGlobe"/>). <see cref="Layers"/>
/// holds the globe-cell layers; after <see cref="WorldGenerator.Generate"/> their elevation, sea level, biome, composition
/// and hydrology layers are the tile summaries (<see cref="TileSummaryStage"/>).
/// </summary>
public sealed record GeneratedWorld(WorldLayers Layers, IReadOnlyList<StageResult> Stages, int GeneratorVersion)
{
    /// <summary>Tile layers; null for <see cref="WorldGenerator.GenerateGlobe"/>.</summary>
    public TileLayers? Tiles { get; init; }

    /// <summary>Region-cell summary; null for <see cref="WorldGenerator.GenerateGlobe"/>.</summary>
    public RegionSummary? Regions { get; init; }

    /// <summary>Tile lakes and dry lake beds (<see cref="TileLayers.Lakes"/>); empty for <see cref="WorldGenerator.GenerateGlobe"/>.</summary>
    public IReadOnlyList<TileLake> Lakes { get; init; } = [];
}

/// <summary>
/// Optional run parameters. <see cref="Parallel"/> is passed to the parallel tile loops (output does not depend on it).
/// <see cref="Progress"/> receives (stage about to run, fraction of the whole run done before it) for every stage, then
/// (<see cref="WorldGenStage.Areas"/>, 1.0) at the end. The fraction weights each stage by its share of the measured
/// default-preset run time (fixed weights in <see cref="WorldGenerator"/>), so it is an estimate of elapsed time.
/// </summary>
public sealed record WorldGenOptions(ParallelOptions? Parallel = null, IProgress<(WorldGenStage Stage, double Fraction)>? Progress = null);

public static class WorldGenerator
{
    /// <summary>
    /// Bumped whenever a stage algorithm changes its output. 2: Stage 8 drops river edges inside a lake or dry lake bed
    /// and zeroes ocean flow; Stage 9 requires a dominant landform for landform-based area membership. 3 (calibration
    /// phase 2): continental shelves (Stage 3), wind band and isotherm noise and polar meridional share (Stages 5–6),
    /// saturation floor, spread rain-out, per-step loss cap and rounded precipitation (Stage 6), slope thresholds and
    /// forested hills (Stage 7), minimum basin depth, base evaporation, depth-scaled floor share and short-system
    /// removal (Stages 7–8), and recalibrated defaults. 4 (tile detail): tile stages after Climate; globe elevation, sea
    /// level, biome, composition and hydrology layers are summaries of the tiles; the globe Biomes and Hydrology stages
    /// and their settings are removed.
    /// </summary>
    public const int GeneratorVersion = 4;

    private sealed record StageEntry(
        WorldGenStage Stage,
        Action<ulong, WorldGenSettings, WorldLayers> Run,
        Func<WorldLayers, ulong> Hash);

    // Globe stages 1–6, shared by both pipelines.
    private static readonly StageEntry[] ClimateStages =
    [
        new(WorldGenStage.Plates,
            (worldSeed, settings, layers) => PlateStage.Run(layers, settings, StageSeed.For(worldSeed, WorldGenStage.Plates)),
            PlateStage.Hash),
        new(WorldGenStage.Elevation,
            (worldSeed, settings, layers) => ElevationStage.Run(layers, settings, StageSeed.For(worldSeed, WorldGenStage.Elevation)),
            ElevationStage.Hash),
        new(WorldGenStage.SeaLevel,
            (_, settings, layers) => SeaLevelStage.Run(layers, settings),
            SeaLevelStage.Hash),
        new(WorldGenStage.Currents,
            (_, settings, layers) => CurrentStage.Run(layers, settings),
            CurrentStage.Hash),
        new(WorldGenStage.Winds,
            (worldSeed, settings, layers) => WindStage.Run(layers, settings, StageSeed.For(worldSeed, WorldGenStage.Winds)),
            WindStage.Hash),
        new(WorldGenStage.Climate,
            (worldSeed, settings, layers) => ClimateStage.Run(layers, settings, StageSeed.For(worldSeed, WorldGenStage.Winds)),
            ClimateStage.Hash),
    ];

    private static readonly StageEntry Areas = new(WorldGenStage.Areas,
        (worldSeed, settings, layers) => AreaStage.Run(layers, settings, StageSeed.For(worldSeed, WorldGenStage.Areas)),
        AreaStage.Hash);

    // Progress weights: approximate share of the default-preset Release run time per stage (task 5 measurement).
    private static readonly (WorldGenStage Stage, double Weight)[] Weights =
    [
        (WorldGenStage.Plates, 0.2), (WorldGenStage.Elevation, 0.2), (WorldGenStage.SeaLevel, 0.1), (WorldGenStage.Currents, 0.1),
        (WorldGenStage.Winds, 0.1), (WorldGenStage.Climate, 0.2), (WorldGenStage.TileGrid, 1), (WorldGenStage.TileElevation, 1),
        (WorldGenStage.TileSeaLevel, 8), (WorldGenStage.TileClimate, 1), (WorldGenStage.TileHydrology, 5),
        (WorldGenStage.TileLandform, 2), (WorldGenStage.Summary, 1), (WorldGenStage.Areas, 0.1),
    ];

    /// <summary>
    /// Runs the full pipeline: globe Plates, Elevation, SeaLevel, Currents, Winds, Climate; tile TileGrid, TileElevation,
    /// TileSeaLevel, TileClimate, TileHydrology, TileLandform; Summary (globe layers and region summary from the tiles);
    /// Areas (on the summaries). Throws <see cref="InvalidOperationException"/> listing every error from
    /// <see cref="WorldGenSettings.Validate"/>.
    /// </summary>
    public static GeneratedWorld Generate(ulong worldSeed, WorldGenSettings settings, WorldGenOptions? options = null)
    {
        Validate(settings);
        var parallel = options?.Parallel;
        var progress = new ProgressReporter(options?.Progress);
        var layers = NewLayers(settings);
        var results = new List<StageResult>(Weights.Length);
        foreach (var entry in ClimateStages) RunEntry(entry, worldSeed, settings, layers, results, progress);

        var sw = new Stopwatch();
        progress.Begin(WorldGenStage.TileGrid);
        sw.Restart();
        var tiles = TileStages.CreateLayers(layers, settings);
        results.Add(new StageResult(WorldGenStage.TileGrid, 0, sw.Elapsed));

        TimeAndHash(WorldGenStage.TileElevation, () => TileElevationStage.Run(tiles, layers, settings, TileStages.SeedFor(worldSeed, 1), parallel),
            () => TileElevationStage.Hash(tiles));
        TimeAndHash(WorldGenStage.TileSeaLevel, () => TileSeaLevelStage.Run(tiles, settings, parallel), () => TileSeaLevelStage.Hash(tiles));
        TimeAndHash(WorldGenStage.TileClimate, () => TileClimateStage.Run(tiles, layers, settings, parallel), () => TileClimateStage.Hash(tiles));
        TimeAndHash(WorldGenStage.TileHydrology, () => TileHydrologyStage.Run(tiles, settings, parallel), () => TileHydrologyStage.Hash(tiles));
        TimeAndHash(WorldGenStage.TileLandform, () => TileLandformStage.Run(tiles, settings, parallel), () => TileLandformStage.Hash(tiles));
        RegionSummary? regions = null;
        TimeAndHash(WorldGenStage.Summary, () => regions = TileSummaryStage.Run(tiles, layers, settings, parallel),
            () => TileSummaryStage.Hash(layers, regions!));

        RunEntry(Areas, worldSeed, settings, layers, results, progress);
        progress.End();
        return new GeneratedWorld(layers, results, GeneratorVersion) { Tiles = tiles, Regions = regions, Lakes = tiles.Lakes };

        void TimeAndHash(WorldGenStage stage, Action run, Func<ulong> hash)
        {
            progress.Begin(stage);
            sw.Restart();
            run();
            var elapsed = sw.Elapsed;
            results.Add(new StageResult(stage, hash(), elapsed));
        }
    }

    /// <summary>
    /// Test and tool helper: runs only the globe stages Plates, Elevation, SeaLevel, Currents, Winds and Climate (the
    /// input of the tile stages), without tiles, summaries or areas. <see cref="GeneratedWorld.GeneratorVersion"/> is 0
    /// (not a generated world: no tiles, biome, hydrology or area layers), and <see cref="GeneratedWorld.Tiles"/> is null.
    /// </summary>
    public static GeneratedWorld GenerateGlobe(ulong worldSeed, WorldGenSettings settings)
    {
        Validate(settings);
        var layers = NewLayers(settings);
        var results = new List<StageResult>(ClimateStages.Length);
        var none = new ProgressReporter(null);
        foreach (var entry in ClimateStages) RunEntry(entry, worldSeed, settings, layers, results, none);
        return new GeneratedWorld(layers, results, 0);
    }

    private static void Validate(WorldGenSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var valid = settings.Validate();
        if (valid.IsError)
            throw new InvalidOperationException($"Invalid world generation settings: {string.Join(" ", valid.Errors.Select(e => e.Description))}");
    }

    private static WorldLayers NewLayers(WorldGenSettings settings)
    {
        var grid = GlobeGrid.Build(settings.Grid.Frequency);
        return new WorldLayers(grid, GlobeGeometry.Compute(grid));
    }

    private static void RunEntry(StageEntry entry, ulong worldSeed, WorldGenSettings settings, WorldLayers layers, List<StageResult> results, ProgressReporter progress)
    {
        progress.Begin(entry.Stage);
        var sw = Stopwatch.StartNew();
        entry.Run(worldSeed, settings, layers);
        sw.Stop();
        results.Add(new StageResult(entry.Stage, entry.Hash(layers), sw.Elapsed));
    }

    private sealed class ProgressReporter(IProgress<(WorldGenStage Stage, double Fraction)>? sink)
    {
        private static readonly double Total = Weights.Sum(w => w.Weight);
        private double _done;
        private WorldGenStage? _current;

        public void Begin(WorldGenStage stage)
        {
            if (sink is null) return;
            if (_current is { } previous) _done += Weights.FirstOrDefault(w => w.Stage == previous).Weight;
            _current = stage;
            sink.Report((stage, Math.Min(1.0, _done / Total)));
        }

        public void End() => sink?.Report((WorldGenStage.Areas, 1.0));
    }
}
