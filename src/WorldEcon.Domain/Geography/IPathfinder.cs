namespace WorldEcon.Domain.Geography;

public interface IPathfinder
{
    Path? FindPath(SettlementId from, SettlementId to);
    IReadOnlyList<ReachableSettlement> FindReachable(SettlementId from, long maxDistance);
}
