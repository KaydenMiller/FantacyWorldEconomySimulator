using ErrorOr;
using WorldEcon.Domain.Geography;
using WorldEcon.SharedKernel;
using WorldEcon.SharedKernel.Domain;

namespace WorldEcon.Domain.Economy;

/// <summary>
/// A single-good shipment travelling between two settlements over a schedule of route legs (spec §7.1).
/// <see cref="DepartTick"/> and <see cref="ArriveTick"/> are the first leg's enter tick and the last leg's exit tick.
/// </summary>
// NOTE: multi-good cargo is deferred; a caravan currently carries exactly one GoodId.
public sealed class Caravan : AggregateRoot<CaravanId>
{
    private List<CaravanLeg> _legs = [];

    public WorldId WorldId { get; }
    public MerchantId OwnerId { get; private set; }
    public SettlementId OriginId { get; private set; }
    public SettlementId DestinationId { get; private set; }
    public GoodId GoodId { get; private set; }
    public long Quantity { get; private set; }
    public Money UnitCostBasis { get; private set; }
    public Tick DepartTick { get; private set; }
    public Tick ArriveTick { get; private set; }
    public bool Delivered { get; private set; }

    /// <summary>True once the merchant's seat has heard about this trip (at <see cref="ReturnTick"/>).</summary>
    public bool HomeReported { get; private set; }

    /// <summary>When the caravan is back at its seat: the way home takes as long as the way out.</summary>
    public Tick ReturnTick => new(ArriveTick.Value + (ArriveTick.Value - DepartTick.Value));

    /// <summary>Total length of the trip, metres.</summary>
    public long LengthMetres => _legs.Sum(l => l.LengthMetres);

    /// <summary>Vehicle class the caravan uses for the whole trip.</summary>
    public string VehicleClassName { get; private set; } = VehicleClass.DefaultMerchantClass;

    public IReadOnlyList<CaravanLeg> Legs => _legs;

    private Caravan() : base(default) { } // EF

    private Caravan(CaravanId id, WorldId worldId) : base(id) => WorldId = worldId;

    public static ErrorOr<Caravan> Create(WorldId worldId, MerchantId owner, SettlementId origin,
        SettlementId destination, GoodId good, long quantity, Money unitCostBasis,
        string vehicleClassName, IReadOnlyList<CaravanLeg> legs)
    {
        if (quantity < 1)
            return Error.Validation("caravan.quantity.tooSmall", "Quantity must be at least 1.");
        if (unitCostBasis.IsNegative)
            return Error.Validation("caravan.unitcostbasis.negative", "Unit cost basis must not be negative.");
        if (origin == destination)
            return Error.Validation("caravan.route.sameEndpoints", "Origin and destination must differ.");
        if (string.IsNullOrWhiteSpace(vehicleClassName))
            return Error.Validation("caravan.vehicleclass.empty", "Vehicle class name must not be empty.");
        var valid = LegSchedule.Validate(legs);
        if (valid.IsError)
            return valid.Errors;

        return new Caravan(CaravanId.New(), worldId)
        {
            OwnerId = owner,
            OriginId = origin,
            DestinationId = destination,
            GoodId = good,
            Quantity = quantity,
            UnitCostBasis = unitCostBasis,
            VehicleClassName = vehicleClassName.Trim(),
            _legs = legs.ToList(),
            DepartTick = legs[0].EnterTick,
            ArriveTick = legs[^1].ExitTick,
            Delivered = false,
        };
    }

    /// <summary>
    /// Replaces the leg schedule (after a route upgrade). The legs must be valid and keep the first enter tick;
    /// <see cref="ArriveTick"/> follows the new last leg.
    /// </summary>
    public ErrorOr<Success> ReplaceLegs(IReadOnlyList<CaravanLeg> legs)
    {
        var valid = LegSchedule.Validate(legs);
        if (valid.IsError)
            return valid.Errors;
        if (legs[0].EnterTick != DepartTick)
            return Error.Validation("caravan.legs.depart", "Replacement legs must keep the departure tick.");
        _legs = legs.ToList();
        ArriveTick = legs[^1].ExitTick;
        return Result.Success;
    }

    /// <summary>Records that the seat has heard about this trip. A second call is a bug.</summary>
    public void MarkHomeReported()
    {
        if (!Delivered)
            throw new InvalidOperationException("A caravan reports home only after delivery.");
        if (HomeReported)
            throw new InvalidOperationException("Caravan already reported home.");
        HomeReported = true;
    }

    /// <summary>Marks the shipment as delivered. Idempotency is not allowed: a second call is a bug.</summary>
    public void MarkDelivered()
    {
        if (Delivered)
            throw new InvalidOperationException("Caravan is already delivered.");
        Delivered = true;
    }
}
