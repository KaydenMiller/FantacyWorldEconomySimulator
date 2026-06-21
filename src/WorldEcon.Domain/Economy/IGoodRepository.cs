namespace WorldEcon.Domain.Economy;

public interface IGoodRepository
{
    Task<Good?> GetAsync(GoodId id);
    Task<IReadOnlyList<Good>> ListByWorldAsync(Geography.WorldId worldId);
    Task AddAsync(Good good);
}
