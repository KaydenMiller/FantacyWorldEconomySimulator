using System.Globalization;
using WorldEcon.Rendering;
using WorldEcon.Seeding;
using WorldEcon.WorldGen.Pipeline;
using WorldEcon.WorldGen.Pipeline.Checkpoints;

namespace WorldEcon.Cli;

/// <summary>
/// <c>globe gen</c>: generates a world without a database, for tuning. Prints one line per stage (name, hash, duration,
/// "(loaded)" for stages taken from a checkpoint); optionally saves a checkpoint after a globe stage, resumes from one,
/// stops after a globe stage, and writes one PNG per finished globe stage.
/// </summary>
internal static class GlobeGenCommand
{
    public const string Usage =
        "globe gen [--seed <n>] [--preset <name>] [--generation <file.json>] [--resume <file.wgckpt>] " +
        "[--save-after <Stage> <file.wgckpt>] [--stop-after <Stage>] [--png <layer|auto> <dir>] [--metrics]";

    private const string GlobeOnly = "only globe stages (Tectonics, SeaLevel, Currents, Winds, Climate) can be checkpointed";

    private static readonly WorldGenStage[] GlobeStages =
        [WorldGenStage.Tectonics, WorldGenStage.SeaLevel, WorldGenStage.Currents, WorldGenStage.Winds, WorldGenStage.Climate];

    private sealed class Collect : IWorldGenObserver
    {
        public List<StageSnapshot> Snapshots { get; } = [];
        public void StageCompleted(StageSnapshot snapshot) => Snapshots.Add(snapshot);
        public void WorldCompleted(GeneratedWorld world) { }
    }

    public static int Run(string[] args)
    {
        ulong? seed = null;
        string? preset = null, generationFile = null, resumePath = null, savePath = null, pngLayer = null, pngDir = null;
        WorldGenStage? saveAfter = null, stopAfter = null;
        var metrics = false;
        for (var i = 2; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new InvalidOperationException($"{args[i]} needs a value. Usage: {Usage}");
            switch (args[i])
            {
                case "--seed":
                    var text = Next();
                    seed = ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var s)
                        ? s : throw new InvalidOperationException($"--seed must be a whole number from 0 to {ulong.MaxValue}, not '{text}'.");
                    break;
                case "--preset": preset = Next(); break;
                case "--generation": generationFile = Next(); break;
                case "--resume": resumePath = Next(); break;
                case "--save-after":
                    saveAfter = GlobeStage(Next());
                    savePath = Next();
                    break;
                case "--stop-after": stopAfter = GlobeStage(Next()); break;
                case "--metrics": metrics = true; break;
                case "--png":
                    pngLayer = Next();
                    pngDir = Next();
                    break;
                default: throw new InvalidOperationException($"unexpected argument '{args[i]}'. Usage: {Usage}");
            }
        }

        GlobeLayer? fixedLayer = pngLayer is null or "auto" ? null
            : Enum.TryParse<GlobeLayer>(pngLayer, ignoreCase: true, out var l) && Enum.IsDefined(l) ? l
            : throw new InvalidOperationException($"unknown layer '{pngLayer}'; use auto or one of {string.Join(", ", Enum.GetNames<GlobeLayer>())}.");

        var checkpoint = resumePath is null ? null : CheckpointCodec.Read(resumePath);
        var generationJson = generationFile is null ? null : File.ReadAllText(generationFile);
        var settings = checkpoint is not null
            ? WorldGenerator.ResumeSettings(checkpoint, preset, generationJson)
            : generationFile is null ? WorldGenPresets.Get(preset ?? WorldGenPresets.Earth5Km)
            : GenerationSettingsFile.Read(generationFile, WorldGenPresets.Get(preset ?? WorldGenPresets.Earth5Km));
        var worldSeed = seed ?? checkpoint?.WorldSeed ?? DemoSeeder.DefaultSeed;

        var collect = savePath is not null || pngDir is not null ? new Collect() : null;
        var options = new WorldGenOptions(Observer: collect, Resume: checkpoint);
        var world = stopAfter is { } stop
            ? WorldGenerator.GenerateGlobe(worldSeed, settings, stop, options)
            : WorldGenerator.Generate(worldSeed, settings, options);

        Console.WriteLine($"Generated seed {worldSeed}{(checkpoint is null ? "" : $" from {resumePath} (after {checkpoint.LastStage})")}:");
        foreach (var r in world.Stages)
            Console.WriteLine($"  {r.Stage,-14} {r.Hash:x16} {r.Duration.TotalMilliseconds:F0} ms{(r.Loaded ? " (loaded)" : "")}");

        if (metrics)
            Console.WriteLine(WorldEcon.WorldGen.Queries.WorldMetrics.Compute(world.Layers, settings.Grid.PlanetRadiusKm).Format().TrimEnd());

        if (saveAfter is { } after)
        {
            var ckpt = WorldGenCheckpoint.FromSnapshots(worldSeed, settings, collect!.Snapshots, after);
            CheckpointCodec.Write(savePath!, ckpt);
            Console.WriteLine($"Saved checkpoint after {after} to {savePath}");
        }

        if (pngDir is not null)
        {
            Directory.CreateDirectory(pngDir);
            foreach (var snap in collect!.Snapshots.Where(x => x.FrameIndex == x.FrameCount - 1))
            {
                var layer = fixedLayer ?? StageLayers.DefaultFor(snap.Stage);
                var n = Array.IndexOf(GlobeStages, snap.Stage) + 1;
                var path = Path.Combine(pngDir, $"{n:00}-{snap.Stage}.png");
                File.WriteAllBytes(path, GlobePngRenderer.RenderMap(new MapWorld(snap.Layers), 1024, 1024, 0, 0, 1, new MapDrawOptions { Layer = layer }));
                Console.WriteLine($"Wrote {snap.Stage} ({layer}) to {path}");
            }
        }

        return 0;
    }

    private static WorldGenStage GlobeStage(string name) =>
        Enum.TryParse<WorldGenStage>(name, ignoreCase: true, out var s) && GlobeStages.Contains(s)
            ? s
            : throw new InvalidOperationException(GlobeOnly + $"; '{name}' is not one.");
}
