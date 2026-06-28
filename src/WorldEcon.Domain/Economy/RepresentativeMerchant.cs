using ErrorOr;
using WorldEcon.Domain.Geography;
using WorldEcon.SharedKernel;
using WorldEcon.SharedKernel.Domain;
using WorldEcon.SharedKernel.Measure;

namespace WorldEcon.Domain.Economy;

/// <summary>A settlement's representative trading agent (spec §3.2). Buys low, ships via caravans, sells high.</summary>
public sealed class RepresentativeMerchant : AggregateRoot<MerchantId>, IMerchantAgent
{
    public WorldId WorldId { get; }
    public SettlementId Seat { get; private set; }
    public Money Capital { get; private set; }
    public Mass WeightCapacity { get; private set; }
    public Volume VolumeCapacity { get; private set; }
    public long Reach { get; private set; }

    private RepresentativeMerchant() : base(default) { } // EF

    private RepresentativeMerchant(MerchantId id, WorldId worldId, SettlementId seat, Money capital,
        Mass weightCapacity, Volume volumeCapacity, long reach) : base(id)
    {
        WorldId = worldId;
        Seat = seat;
        Capital = capital;
        WeightCapacity = weightCapacity;
        VolumeCapacity = volumeCapacity;
        Reach = reach;
    }

    public static ErrorOr<RepresentativeMerchant> Create(WorldId worldId, SettlementId seat, Money capital,
        Mass weightCapacity, Volume volumeCapacity, long reach)
    {
        if (capital.IsNegative)
            return Error.Validation("merchant.capital.negative", "Capital must not be negative.");
        if (weightCapacity.Grams < 1)
            return Error.Validation("merchant.weightcapacity.tooSmall", "Weight capacity must be at least 1 gram.");
        if (volumeCapacity.CubicCentimeters < 1)
            return Error.Validation("merchant.volumecapacity.tooSmall", "Volume capacity must be at least 1 cm³.");
        if (reach < 1)
            return Error.Validation("merchant.reach.tooSmall", "Reach must be at least 1.");

        return new RepresentativeMerchant(MerchantId.New(), worldId, seat, capital, weightCapacity, volumeCapacity, reach);
    }

    /// <summary>DM tuning: set hauling capacity (both ≥ 1 base unit).</summary>
    public ErrorOr<Success> SetCapacity(Mass weightCapacity, Volume volumeCapacity)
    {
        if (weightCapacity.Grams < 1)
            return Error.Validation("merchant.weightcapacity.tooSmall", "Weight capacity must be at least 1 gram.");
        if (volumeCapacity.CubicCentimeters < 1)
            return Error.Validation("merchant.volumecapacity.tooSmall", "Volume capacity must be at least 1 cm³.");
        WeightCapacity = weightCapacity;
        VolumeCapacity = volumeCapacity;
        return Result.Success;
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
