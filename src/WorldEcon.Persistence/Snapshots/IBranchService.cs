namespace WorldEcon.Persistence.Snapshots;

public interface IBranchService
{
    /// <summary>Fork <paramref name="sourceDbPath"/> into an independent world DB at <paramref name="branchDbPath"/>.</summary>
    Task BranchAsync(string sourceDbPath, string branchDbPath);
}
