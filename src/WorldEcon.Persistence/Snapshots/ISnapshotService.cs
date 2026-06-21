namespace WorldEcon.Persistence.Snapshots;

public interface ISnapshotService
{
    /// <summary>Write a consistent, compacted copy of <paramref name="sourceDbPath"/> to <paramref name="destDbPath"/>.</summary>
    Task CaptureAsync(string sourceDbPath, string destDbPath);
}
