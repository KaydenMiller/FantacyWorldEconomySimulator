namespace WorldEcon.Domain.Geography;

public interface IWorldRepository
{
    Task<World?> GetAsync(WorldId id);
    Task AddAsync(World world);
}
