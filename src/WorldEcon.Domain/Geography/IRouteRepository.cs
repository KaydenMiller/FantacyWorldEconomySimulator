namespace WorldEcon.Domain.Geography;

public interface IRouteRepository
{
    Task<IReadOnlyList<Route>> ListByWorldAsync(WorldId worldId);
    Task AddAsync(Route route);
}
