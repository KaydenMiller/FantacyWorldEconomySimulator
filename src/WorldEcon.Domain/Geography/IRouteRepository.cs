namespace WorldEcon.Domain.Geography;

public interface IRouteRepository
{
    Task<IReadOnlyList<Route>> ListByWorldAsync(WorldId worldId);

    /// <summary>Adds a generated travel graph: nodes, routes, their tile paths and the roads grouping them.</summary>
    Task AddRangeAsync(IEnumerable<RouteNode> nodes, IEnumerable<Route> routes, IEnumerable<RouteTile> tiles, IEnumerable<Road> roads);

    /// <summary>Ids of the routes whose path includes <paramref name="tileId"/>, in id order.</summary>
    Task<IReadOnlyList<RouteId>> RoutesOnTileAsync(WorldId worldId, int tileId);
}
