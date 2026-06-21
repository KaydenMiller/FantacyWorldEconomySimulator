namespace WorldEcon.Persistence.Snapshots;

/// <summary>Structural difference between two world DBs for a given world (geography vertical).</summary>
public sealed record WorldDiff(
    IReadOnlyList<string> AddedSettlements,
    IReadOnlyList<string> RemovedSettlements,
    IReadOnlyList<string> ChangedSettlements);
