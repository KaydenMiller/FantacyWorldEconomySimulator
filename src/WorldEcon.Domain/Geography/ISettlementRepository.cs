namespace WorldEcon.Domain.Geography;

public interface ISettlementRepository
{
    Task<Settlement?> GetAsync(SettlementId id);
    Task<IReadOnlyList<Settlement>> ListByWorldAsync(WorldId worldId);
    Task AddAsync(Settlement settlement);
}
