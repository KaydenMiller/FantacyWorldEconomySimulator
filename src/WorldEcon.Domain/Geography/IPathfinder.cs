namespace WorldEcon.Domain.Geography;

public interface IPathfinder
{
    RoutePath? FindPath(SettlementId from, SettlementId to);
    IReadOnlyList<ReachableSettlement> FindReachable(SettlementId from, long maxDistance);
}
