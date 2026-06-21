namespace WorldEcon.Persistence.Snapshots;

/// <summary>A branch is a snapshot to a new path; the copy diverges independently (Fowler's Parallel Model).</summary>
public sealed class FileBranchService(ISnapshotService snapshots) : IBranchService
{
    public Task BranchAsync(string sourceDbPath, string branchDbPath)
        => snapshots.CaptureAsync(sourceDbPath, branchDbPath);
}
