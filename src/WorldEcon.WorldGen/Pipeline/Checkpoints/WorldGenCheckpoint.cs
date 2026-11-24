namespace WorldEcon.WorldGen.Pipeline.Checkpoints;

/// <summary>One stage covered by a checkpoint: its fingerprint part and its output hash.</summary>
public sealed record CheckpointStage(StagePrint Print, ulong OutputHash);

/// <summary>An intermediate frame a stage published (every frame except the stage's final one).</summary>
public sealed record CheckpointFrame(WorldGenStage Stage, int FrameIndex, int FrameCount, string? Label, WorldLayers Layers);

/// <summary>
/// The generated state after a globe stage (design: stage checkpoints and viewer). <see cref="Layers"/> holds every globe
/// layer written by the stages up to and including <see cref="LastStage"/>. <see cref="Settings"/> are the settings the
/// run used. A run may resume from it when <see cref="StageFingerprint.Differences"/> finds no difference.
/// </summary>
public sealed record WorldGenCheckpoint(
    ulong WorldSeed,
    int GeneratorVersion,
    WorldGenStage LastStage,
    ulong Fingerprint,
    IReadOnlyList<CheckpointStage> Stages,
    WorldGenSettings Settings,
    WorldLayers Layers,
    IReadOnlyList<CheckpointFrame> Frames)
{
    /// <summary>The fingerprint parts of <see cref="Stages"/>, in order.</summary>
    public IReadOnlyList<StagePrint> Prints => Stages.Select(s => s.Print).ToList();

    /// <summary>
    /// A checkpoint after <paramref name="lastStage"/> from the snapshots of a run with <paramref name="settings"/> (in the
    /// order the observer received them). The final snapshot of <paramref name="lastStage"/> gives the layers; the
    /// non-final frames of the covered stages are kept. Throws <see cref="ArgumentException"/> when
    /// <paramref name="lastStage"/> is not a globe stage or a covered stage has no final snapshot.
    /// </summary>
    public static WorldGenCheckpoint FromSnapshots(ulong worldSeed, WorldGenSettings settings, IReadOnlyList<StageSnapshot> snapshots, WorldGenStage lastStage)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(snapshots);
        var prints = StageFingerprint.For(settings, lastStage);
        var stages = new List<CheckpointStage>(prints.Count);
        var frames = new List<CheckpointFrame>();
        StageSnapshot? last = null;
        foreach (var p in prints)
        {
            var final = snapshots.LastOrDefault(s => s.Stage == p.Stage && s.FrameIndex == s.FrameCount - 1)
                ?? throw new ArgumentException($"There is no finished snapshot of stage {p.Stage}.", nameof(snapshots));
            stages.Add(new CheckpointStage(p, final.Hash));
            frames.AddRange(snapshots
                .Where(s => s.Stage == p.Stage && s.FrameIndex < s.FrameCount - 1)
                .Select(s => new CheckpointFrame(s.Stage, s.FrameIndex, s.FrameCount, s.FrameLabel, s.Layers)));
            last = final;
        }

        var version = WorldGenerator.GeneratorVersion;
        return new WorldGenCheckpoint(worldSeed, version, lastStage, StageFingerprint.Combine(worldSeed, version, prints), stages,
            settings, last!.Layers, frames);
    }
}

/// <summary>A checkpoint file or byte array that cannot be read; the message names the problem.</summary>
public sealed class CheckpointFormatException(string message, Exception? inner = null) : Exception(message, inner);
