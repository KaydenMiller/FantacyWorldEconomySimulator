using ErrorOr;
using WorldEcon.Domain.Geography;
using WorldEcon.SharedKernel;
using WorldEcon.SharedKernel.Domain;

namespace WorldEcon.Domain.Economy;

/// <summary>A settlement's representative trading agent (spec §3.2). Buys low, ships via caravans, sells high.</summary>
public sealed class RepresentativeMerchant : AggregateRoot<MerchantId>, IMerchantAgent
{
    public WorldId WorldId { get; }
    public SettlementId Seat { get; private set; }
    public Money Capital { get; private set; }
    public long CargoCapacity { get; private set; }
    public long Reach { get; private set; }

    private RepresentativeMerchant() : base(default) { } // EF

    private RepresentativeMerchant(MerchantId id, WorldId worldId, SettlementId seat, Money capital,
        long cargoCapacity, long reach) : base(id)
    {
        WorldId = worldId;
        Seat = seat;
        Capital = capital;
        CargoCapacity = cargoCapacity;
        Reach = reach;
    }

    public static ErrorOr<RepresentativeMerchant> Create(WorldId worldId, SettlementId seat, Money capital,
        long cargoCapacity, long reach)
    {
        if (capital.IsNegative)
            return Error.Validation("merchant.capital.negative", "Capital must not be negative.");
        if (cargoCapacity < 1)
            return Error.Validation("merchant.cargocapacity.tooSmall", "Cargo capacity must be at least 1.");
        if (reach < 1)
            return Error.Validation("merchant.reach.tooSmall", "Reach must be at least 1.");

        return new RepresentativeMerchant(MerchantId.New(), worldId, seat, capital, cargoCapacity, reach);
    }

    /// <summary>Spend capital (e.g. to buy goods). Cannot go into debt.</summary>
    public void Spend(Money amount)
    {
        if (amount.IsNegative)
            throw new InvalidOperationException("Cannot spend a negative amount.");
        if (amount.Units > Capital.Units)
            throw new InvalidOperationException("Cannot spend more than available capital.");
        Capital -= amount;
    }

    /// <summary>Earn capital (e.g. from a sale).</summary>
    public void Earn(Money amount)
    {
        if (amount.IsNegative)
            throw new InvalidOperationException("Cannot earn a negative amount.");
        Capital += amount;
    }
}
