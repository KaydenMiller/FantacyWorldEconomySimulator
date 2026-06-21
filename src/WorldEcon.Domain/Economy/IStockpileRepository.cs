namespace WorldEcon.Domain.Economy;

public interface IStockpileRepository
{
    Task<Stockpile?> GetByOwnerAndGoodAsync(StockpileOwnerKind ownerKind, Guid ownerId, GoodId goodId);
    Task AddAsync(Stockpile stockpile);
}
