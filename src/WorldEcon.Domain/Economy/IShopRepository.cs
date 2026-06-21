using WorldEcon.Domain.Geography;

namespace WorldEcon.Domain.Economy;

public interface IShopRepository
{
    Task<Shop?> GetAsync(ShopId id);
    Task<IReadOnlyList<Shop>> ListBySettlementAsync(SettlementId settlementId);
    Task AddAsync(Shop shop);
}
