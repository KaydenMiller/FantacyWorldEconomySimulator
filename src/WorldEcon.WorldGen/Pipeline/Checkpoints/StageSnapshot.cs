namespace WorldEcon.WorldGen.Pipeline.Checkpoints;

/// <summary>
/// The globe layers as they were when a globe stage finished, or when it published a frame. <see cref="Layers"/> is a
/// copy (<see cref="WorldLayers.CloneGlobe"/>), so later stages do not change it. A stage that publishes no frames has
/// one snapshot with <see cref="FrameIndex"/> 0 and <see cref="FrameCount"/> 1. The last frame of a stage is the stage
/// result and carries the stage <see cref="Hash"/>; earlier frames have hash 0. <see cref="Loaded"/> is true for stages
/// taken from a checkpoint instead of run.
/// </summary>
public sealed record StageSnapshot(
    WorldGenStage Stage,
    int FrameIndex,
    int FrameCount,
    string? FrameLabel,
    ulong Hash,
    TimeSpan Duration,
    bool Loaded,
    WorldLayers Layers);

/// <summary>
/// Receives generation results as they are produced (<see cref="WorldGenOptions.Observer"/>). Both methods are called on
/// the generation thread; a user interface must post to its own thread.
/// </summary>
public interface IWorldGenObserver
{
    /// <summary>After every globe stage and every frame a globe stage publishes, in order.</summary>
    void StageCompleted(StageSnapshot snapshot);

    /// <summary>Once, after the last stage of <see cref="WorldGenerator.Generate"/> (not called by <see cref="WorldGenerator.GenerateGlobe"/>).</summary>
    void WorldCompleted(GeneratedWorld world);
}

/// <summary>
/// Lets a stage publish intermediate states (frames), for example the steps of a simulation. Each call snapshots the
/// layers as they are at that moment. A stage that publishes frames publishes its final state as frame
/// <c>frameCount − 1</c>.
/// </summary>
public interface IStageFrameSink
{
    void Publish(int frameIndex, int frameCount, string? label);
}

/// <summary>What a globe stage receives when it runs.</summary>
public sealed record StageContext(ulong WorldSeed, WorldGenSettings Settings, WorldLayers Layers, IStageFrameSink Frames)
{
    /// <summary>Progress within the stage, 0–1 (long stages report it so the run's progress and time remaining move during them).</summary>
    public IProgress<double>? StageProgress { get; init; }
}
