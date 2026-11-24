using System.Diagnostics;
using WorldEcon.WorldGen.Grid;
using WorldEcon.WorldGen.Pipeline.Checkpoints;
using WorldEcon.WorldGen.Stages;
using WorldEcon.WorldGen.Tiles;

namespace WorldEcon.WorldGen.Pipeline;

/// <summary>Timing and output hash for one executed stage. <paramref name="Loaded"/> is true for a stage taken from a checkpoint (duration zero).</summary>
public sealed record StageResult(WorldGenStage Stage, ulong Hash, TimeSpan Duration, bool Loaded = false);

/// <summary>
/// The output of <see cref="WorldGenerator.Generate"/> (or of the climate-only helper <see cref="WorldGenerator.GenerateGlobe"/>). <see cref="Layers"/>
/// holds the globe-cell layers; after <see cref="WorldGenerator.Generate"/> their elevation, sea level, biome, composition
/// and hydrology layers are the tile summaries (<see cref="TileSummaryStage"/>).
/// </summary>
public sealed record GeneratedWorld(WorldLayers Layers, IReadOnlyList<StageResult> Stages, int GeneratorVersion)
{
    /// <summary>The world seed the run used.</summary>
    public ulong WorldSeed { get; init; }

    /// <summary>Tile layers; null for <see cref="WorldGenerator.GenerateGlobe"/>.</summary>
    public TileLayers? Tiles { get; init; }

    /// <summary>Region-cell summary; null for <see cref="WorldGenerator.GenerateGlobe"/>.</summary>
    public RegionSummary? Regions { get; init; }

    /// <summary>Tile lakes and dry lake beds (<see cref="TileLayers.Lakes"/>); empty for <see cref="WorldGenerator.GenerateGlobe"/>.</summary>
    public IReadOnlyList<TileLake> Lakes { get; init; } = [];

    /// <summary>Per-globe-cell texel samples of the tiles (the map's stored imagery); null for <see cref="WorldGenerator.GenerateGlobe"/>.</summary>
    public CellImagery? Imagery { get; init; }

    /// <summary>Major river edges at tile resolution (the map's Globe and Region View rivers); null for <see cref="WorldGenerator.GenerateGlobe"/>.</summary>
    public RiverCourses? RiverCourses { get; init; }
}

/// <summary>
/// Optional run parameters. <see cref="Parallel"/> is passed to the parallel tile loops (output does not depend on it).
/// <see cref="Progress"/> receives (stage about to run, fraction of the whole run done before it) for every stage, then
/// (<see cref="WorldGenStage.Areas"/>, 1.0) at the end. The fraction weights each stage by its share of the measured
/// default-preset run time (fixed weights in <see cref="WorldGenerator"/>), so it is an estimate of elapsed time.
/// <see cref="Observer"/> receives a copy of the globe layers after every globe stage and frame, then the finished world;
/// without one no copies are made. <see cref="Resume"/> starts the run from a checkpoint: it is validated against the
/// seed and settings (<see cref="CheckpointMismatchException"/> on any difference), its stages are loaded instead of run,
/// and the stages after it run as usual.
/// </summary>
public sealed record WorldGenOptions(
    ParallelOptions? Parallel = null,
    IProgress<(WorldGenStage Stage, double Fraction)>? Progress = null,
    IWorldGenObserver? Observer = null,
    WorldGenCheckpoint? Resume = null);

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
    /// and their settings are removed. 5 (flat drainage): within one filled level the tile floods order tiles by hop
    /// distance from where the flood entered that level, so flats drain by shortest routes instead of in tile-id order
    /// (straight parallel channels). 6 (plate history): the Tectonics stage replaces Plates and Elevation; elevation comes
    /// from a simulated crust record; tile crests follow the orogeny record.
    /// </summary>
    public const int GeneratorVersion = 6;

    /// <summary>
    /// A globe stage: its id, its code version (bumped whenever its algorithm changes its output; part of checkpoint
    /// fingerprints), the settings sections it reads (checked by the resume-equivalence tests), its run action and its
    /// output hash.
    /// </summary>
    internal sealed record StageEntry(
        WorldGenStage Stage,
        int CodeVersion,
        IReadOnlyList<SettingsSection> Reads,
        Action<StageContext> Run,
        Func<WorldLayers, ulong> Hash);

    /// <summary>Globe stages 1–6 in run order, shared by both pipelines; the only stages a checkpoint can cover.</summary>
    internal static IReadOnlyList<StageEntry> GlobeStages => ClimateStages;

    private static readonly StageEntry[] ClimateStages =
    [
        new(WorldGenStage.Tectonics, 1, [SettingsSection.GridGlobe, SettingsSection.Plates, SettingsSection.Elevation, SettingsSection.Tectonics, SettingsSection.SeaLevel],
            TectonicsStage.Run,
            TectonicsStage.Hash),
        new(WorldGenStage.SeaLevel, 1, [SettingsSection.GridGlobe, SettingsSection.SeaLevel],
            ctx => SeaLevelStage.Run(ctx.Layers, ctx.Settings),
            SeaLevelStage.Hash),
        new(WorldGenStage.Currents, 1, [SettingsSection.GridGlobe, SettingsSection.Currents],
            ctx => CurrentStage.Run(ctx.Layers, ctx.Settings),
            CurrentStage.Hash),
        new(WorldGenStage.Winds, 1, [SettingsSection.GridGlobe, SettingsSection.Winds],
            ctx => WindStage.Run(ctx.Layers, ctx.Settings, StageSeed.For(ctx.WorldSeed, WorldGenStage.Winds)),
            WindStage.Hash),
        new(WorldGenStage.Climate, 1, [SettingsSection.GridGlobe, SettingsSection.Climate],
            ctx => ClimateStage.Run(ctx.Layers, ctx.Settings, StageSeed.For(ctx.WorldSeed, WorldGenStage.Winds)),
            ClimateStage.Hash),
    ];

    // Areas runs after the tile stages and is never checkpointed, so it declares no fingerprint sections.
    private static readonly StageEntry Areas = new(WorldGenStage.Areas, 1, [],
        ctx => AreaStage.Run(ctx.Layers, ctx.Settings, StageSeed.For(ctx.WorldSeed, WorldGenStage.Areas)),
        AreaStage.Hash);

    // Progress weights: approximate share of the default-preset Release run time per stage (task 5 measurement).
    private static readonly (WorldGenStage Stage, double Weight)[] Weights =
    [
        (WorldGenStage.Tectonics, 4), (WorldGenStage.SeaLevel, 0.1), (WorldGenStage.Currents, 0.1),
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
        var results = new List<StageResult>(Weights.Length);
        var observer = options?.Observer;
        var layers = RunGlobeStages(worldSeed, settings, options, ClimateStages, results, progress);

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

        RunEntry(Areas, worldSeed, settings, layers, results, progress, observer: null);

        // Derived map data (not a stage: no hash, no effect on generated values).
        var imagery = CellImagery.Build(tiles, layers.Grid, layers.Geometry, parallel);
        var courses = RiverCourses.Build(tiles, settings.Tiles.MajorRiverFlow);
        progress.End();
        var world = new GeneratedWorld(layers, results, GeneratorVersion)
        {
            WorldSeed = worldSeed, Tiles = tiles, Regions = regions, Lakes = tiles.Lakes, Imagery = imagery, RiverCourses = courses,
        };
        observer?.WorldCompleted(world);
        return world;

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
    /// <see cref="WorldGenOptions.Observer"/> receives the globe stages; <see cref="IWorldGenObserver.WorldCompleted"/> is
    /// not called.
    /// </summary>
    public static GeneratedWorld GenerateGlobe(ulong worldSeed, WorldGenSettings settings, WorldGenOptions? options = null) =>
        GenerateGlobe(worldSeed, settings, options, ClimateStages);

    /// <summary>
    /// As <see cref="GenerateGlobe(ulong, WorldGenSettings, WorldGenOptions?)"/>, stopping after the globe stage
    /// <paramref name="stopAfter"/>. With <see cref="WorldGenOptions.Resume"/> covering later stages, the checkpoint's stages
    /// are loaded and nothing runs. Throws <see cref="ArgumentException"/> when <paramref name="stopAfter"/> is not a globe stage.
    /// </summary>
    public static GeneratedWorld GenerateGlobe(ulong worldSeed, WorldGenSettings settings, WorldGenStage stopAfter, WorldGenOptions? options = null)
    {
        var stop = Array.FindIndex(ClimateStages, e => e.Stage == stopAfter);
        if (stop < 0)
            throw new ArgumentException($"{stopAfter} is not a globe stage (Tectonics, SeaLevel, Currents, Winds, Climate).", nameof(stopAfter));
        if (options?.Resume is { } resume)
            stop = Math.Max(stop, Array.FindIndex(ClimateStages, e => e.Stage == resume.LastStage));
        return GenerateGlobe(worldSeed, settings, options, ClimateStages[..(stop + 1)]);
    }

    /// <summary>As the public overload, with the globe stages given (tests run fake stages through it).</summary>
    internal static GeneratedWorld GenerateGlobe(ulong worldSeed, WorldGenSettings settings, WorldGenOptions? options, IReadOnlyList<StageEntry> stages)
    {
        Validate(settings);
        var results = new List<StageResult>(stages.Count);
        var layers = RunGlobeStages(worldSeed, settings, options, stages, results, new ProgressReporter(options?.Progress));
        return new GeneratedWorld(layers, results, 0) { WorldSeed = worldSeed };
    }

    /// <summary>
    /// The settings for a run resumed from <paramref name="checkpoint"/>: the checkpoint's settings, replaced by
    /// <paramref name="preset"/> when given (<see cref="WorldGenPresets.Get"/>), with <paramref name="generationJson"/>
    /// (a settings JSON object) layered on top when given.
    /// </summary>
    public static WorldGenSettings ResumeSettings(WorldGenCheckpoint checkpoint, string? preset, string? generationJson)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        var settings = preset is null ? checkpoint.Settings : WorldGenPresets.Get(preset);
        return generationJson is null ? settings : WorldGenSettingsJson.Deserialize(generationJson, settings);
    }

    /// <summary>
    /// Runs <paramref name="stages"/>, or with <see cref="WorldGenOptions.Resume"/> validates the checkpoint, loads the
    /// stages it covers (results marked loaded, the observer told about their frames and results) and runs the rest.
    /// </summary>
    private static WorldLayers RunGlobeStages(ulong worldSeed, WorldGenSettings settings, WorldGenOptions? options,
        IReadOnlyList<StageEntry> stages, List<StageResult> results, ProgressReporter progress)
    {
        var observer = options?.Observer;
        var resume = options?.Resume;
        var start = 0;
        WorldLayers layers;
        if (resume is null)
        {
            layers = NewLayers(settings);
        }
        else
        {
            var current = StageFingerprint.For(settings, resume.LastStage, stages);
            var diff = StageFingerprint.Differences(resume.Prints, resume.WorldSeed, resume.GeneratorVersion, current, worldSeed, GeneratorVersion);
            if (diff.Count > 0) throw new CheckpointMismatchException(diff);

            layers = resume.Layers.CloneGlobe();
            foreach (var stage in resume.Stages)
            {
                var s = stage.Print.Stage;
                if (observer is not null)
                {
                    var frames = resume.Frames.Where(f => f.Stage == s).OrderBy(f => f.FrameIndex).ToList();
                    foreach (var f in frames)
                        observer.StageCompleted(new StageSnapshot(s, f.FrameIndex, f.FrameCount, f.Label, 0, TimeSpan.Zero, true, f.Layers.CloneGlobe()));
                    var count = frames.Count == 0 ? 1 : frames[0].FrameCount;
                    var stageLayers = s == resume.LastStage ? layers.CloneGlobe() : LayersAt(resume, s);
                    observer.StageCompleted(new StageSnapshot(s, count - 1, count, null, stage.OutputHash, TimeSpan.Zero, true, stageLayers));
                }

                results.Add(new StageResult(s, stage.OutputHash, TimeSpan.Zero, Loaded: true));
            }

            start = resume.Stages.Count;
        }

        for (var i = start; i < stages.Count; i++)
            RunEntry(stages[i], worldSeed, settings, layers, results, progress, observer);
        return layers;
    }

    /// <summary>
    /// The checkpoint's layers reduced to what existed after <paramref name="stage"/>: the layers of later covered stages
    /// are emptied, so the viewer shows each loaded stage as it was when that stage finished.
    /// </summary>
    private static WorldLayers LayersAt(WorldGenCheckpoint c, WorldGenStage stage)
    {
        var l = c.Layers.CloneGlobe();
        var after = false;
        foreach (var s in c.Stages.Select(x => x.Print.Stage))
        {
            if (after) ClearStageOutput(l, s);
            if (s == stage) after = true;
        }

        return l;
    }

    private static void ClearStageOutput(WorldLayers l, WorldGenStage stage)
    {
        switch (stage)
        {
            case WorldGenStage.Tectonics:
                l.PlateOf = []; l.Plates = []; l.ElevationM = []; l.BoundaryKind = []; l.Crust = null;
                l.CollisionStrengthPermille = []; l.BoundaryDistanceCells = [];
                break;
            case WorldGenStage.SeaLevel: l.SeaLevelM = 0; break;
            case WorldGenStage.Currents: l.CurrentDir = []; l.CurrentAnomalyDeciC = []; break;
            case WorldGenStage.Winds: l.WindDir = []; l.WindStrength = []; break;
            case WorldGenStage.Climate: l.TemperatureDeciC = []; l.PrecipitationMm = []; break;
        }
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

    private static void RunEntry(StageEntry entry, ulong worldSeed, WorldGenSettings settings, WorldLayers layers, List<StageResult> results,
        ProgressReporter progress, IWorldGenObserver? observer)
    {
        progress.Begin(entry.Stage);
        var sw = Stopwatch.StartNew();
        var frames = new FrameSink(entry.Stage, layers, observer, sw);
        entry.Run(new StageContext(worldSeed, settings, layers, frames) { StageProgress = progress.Within(entry.Stage) });
        var hash = entry.Hash(layers);
        sw.Stop();
        results.Add(new StageResult(entry.Stage, hash, sw.Elapsed));
        frames.Finish(hash);
    }

    /// <summary>
    /// Frame publishing for one stage run. Non-final frames go to the observer at once (hash 0). The final frame
    /// (index = count − 1) is held until the stage hash is known. A stage that published no final frame gets one final
    /// snapshot after it returns: frame 0 of 1 if it published nothing, else the frame after the last one it published.
    /// Without an observer nothing is copied.
    /// </summary>
    private sealed class FrameSink(WorldGenStage stage, WorldLayers layers, IWorldGenObserver? observer, Stopwatch clock) : IStageFrameSink
    {
        private StageSnapshot? _final;
        private int _lastIndex = -1;
        private int _lastCount;

        public void Publish(int frameIndex, int frameCount, string? label)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(frameCount, 1);
            ArgumentOutOfRangeException.ThrowIfNegative(frameIndex);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(frameIndex, frameCount);
            _lastIndex = frameIndex;
            _lastCount = frameCount;
            if (observer is null) return;
            var snapshot = new StageSnapshot(stage, frameIndex, frameCount, label, 0, clock.Elapsed, false, layers.CloneGlobe());
            if (frameIndex == frameCount - 1) _final = snapshot;
            else observer.StageCompleted(snapshot);
        }

        public void Finish(ulong hash)
        {
            if (observer is null) return;
            if (_final is not null)
            {
                observer.StageCompleted(_final with { Hash = hash, Duration = clock.Elapsed });
                return;
            }

            var (index, count) = _lastIndex < 0 ? (0, 1) : (_lastIndex + 1, Math.Max(_lastCount, _lastIndex + 2));
            observer.StageCompleted(new StageSnapshot(stage, index, count, null, hash, clock.Elapsed, false, layers.CloneGlobe()));
        }
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

        /// <summary>Progress inside <paramref name="stage"/> (0–1), reported as the run fraction between its start and end; never backwards.</summary>
        public IProgress<double>? Within(WorldGenStage stage)
        {
            if (sink is null) return null;
            var weight = Weights.FirstOrDefault(w => w.Stage == stage).Weight;
            var start = _done;
            var last = -1.0;
            return new Inline(f =>
            {
                var value = Math.Min(1.0, (start + weight * Math.Clamp(f, 0, 1)) / Total);
                if (value <= last) return;
                last = value;
                sink.Report((stage, value));
            });
        }

        private sealed class Inline(Action<double> report) : IProgress<double>
        {
            public void Report(double value) => report(value);
        }
    }
}
