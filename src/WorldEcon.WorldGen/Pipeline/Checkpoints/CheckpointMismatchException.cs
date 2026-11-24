namespace WorldEcon.WorldGen.Pipeline.Checkpoints;

/// <summary>
/// A run cannot resume from a checkpoint because its seed, generator version, a covered stage's code version or a
/// settings section a covered stage reads differs. <see cref="Differences"/> has one line per difference
/// (<see cref="StageFingerprint.Differences"/>).
/// </summary>
public sealed class CheckpointMismatchException(IReadOnlyList<string> differences)
    : Exception("The checkpoint does not match this run:\n" + string.Join("\n", differences))
{
    public IReadOnlyList<string> Differences { get; } = differences;
}
