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
    /// <summary>Furthest trip the merchant considers, in ticks of travel time.</summary>
    public long Reach { get; private set; }

    /// <summary>
    /// The merchant's preferred <see cref="VehicleClass"/>: the tie-break between equally good trips. Until fleets exist a
    /// merchant may use every class its seat offers.
    /// </summary>
    public string VehicleClassName { get; private set; } = VehicleClass.DefaultMerchantClass;

    /// <summary>0 (bold) to 100 (cautious): how rarely the merchant gambles on a trip that is not the best one.</summary>
    public int RiskAversion { get; private set; } = 50;

    private List<MerchantTrip> _trips = [];

    /// <summary>The merchant's last two completed trips, slot 0 the most recent.</summary>
    public IReadOnlyList<MerchantTrip> Trips => _trips;

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
        Mass weightCapacity, Volume volumeCapacity, long reach, string vehicleClassName = VehicleClass.DefaultMerchantClass,
        int riskAversion = 50)
    {
        if (riskAversion is < 0 or > 100)
            return Error.Validation("merchant.risk.range", "Risk aversion must be between 0 and 100.");
        if (string.IsNullOrWhiteSpace(vehicleClassName))
            return Error.Validation("merchant.vehicleclass.empty", "Vehicle class name must not be empty.");
        if (capital.IsNegative)
            return Error.Validation("merchant.capital.negative", "Capital must not be negative.");
        if (weightCapacity.Grams < 1)
            return Error.Validation("merchant.weightcapacity.tooSmall", "Weight capacity must be at least 1 gram.");
        if (volumeCapacity.CubicCentimeters < 1)
            return Error.Validation("merchant.volumecapacity.tooSmall", "Volume capacity must be at least 1 cm³.");
        if (reach < 1)
            return Error.Validation("merchant.reach.tooSmall", "Reach must be at least 1.");

        return new RepresentativeMerchant(MerchantId.New(), worldId, seat, capital, weightCapacity, volumeCapacity, reach)
        {
            VehicleClassName = vehicleClassName.Trim(),
            RiskAversion = riskAversion,
        };
    }

    /// <summary>
    /// Risk aversion for a merchant the seed or spawn did not give one: a deterministic value 0–100 from the world seed and
    /// the merchant id (SplitMix64 mixing, stable across runtimes).
    /// </summary>
    public static int DefaultRiskAversion(ulong worldSeed, MerchantId id)
    {
        Span<byte> g = stackalloc byte[16];
        id.Value.TryWriteBytes(g);
        ulong x = worldSeed ^ 0x5249534B_00000000UL; // "RISK"
        x = Mix(x ^ BitConverter.ToUInt64(g[..8]));
        x = Mix(x ^ BitConverter.ToUInt64(g[8..]));
        return (int)(x % 101);

        static ulong Mix(ulong z)
        {
            unchecked
            {
                z += 0x9E3779B97F4A7C15UL;
                z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
                z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
                return z ^ (z >> 31);
            }
        }
    }

    /// <summary>DM tuning: risk aversion 0–100.</summary>
    public ErrorOr<Success> SetRiskAversion(int riskAversion)
    {
        if (riskAversion is < 0 or > 100)
            return Error.Validation("merchant.risk.range", "Risk aversion must be between 0 and 100.");
        RiskAversion = riskAversion;
        return Result.Success;
    }

    /// <summary>Remembers a completed trip as slot 0; the previous slot 0 becomes slot 1; older trips are forgotten.</summary>
    public void Remember(TripRecord trip)
    {
        var previous = _trips.FirstOrDefault(t => t.Slot == 0);
        _trips = previous is null
            ? [MerchantTrip.Of(0, trip)]
            : [MerchantTrip.Of(0, trip), MerchantTrip.Of(1, previous.Record)];
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
