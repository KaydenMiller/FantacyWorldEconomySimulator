using WorldEcon.Domain.Geography;

namespace WorldEcon.Persistence.Snapshots;

public interface ICompareService
{
    /// <summary>Diff settlements of <paramref name="worldId"/> between two world DBs (baseline → candidate).</summary>
    Task<WorldDiff> CompareAsync(string baselineDbPath, string candidateDbPath, WorldId worldId);
}
