using ErrorOr;
using WorldEcon.Domain.Geography;
using WorldEcon.SharedKernel;

namespace WorldEcon.Domain.Economy;

/// <summary>Whether a known travel time is computed from the travel graph or was told by a merchant who made the trip.</summary>
public enum KnowledgeSource { Estimated = 0, Reported = 1 }

/// <summary>One completed trip as a merchant tells it: from, to, vehicle class, ticks taken, length and arrival tick.</summary>
public sealed record TripRecord(SettlementId From, SettlementId To, string VehicleClass, long Ticks, long LengthMetres, long ArriveTick);

/// <summary>One of a merchant's last two trips (slot 0 the most recent). Key (MerchantId, Slot).</summary>
public sealed class MerchantTrip
{
    public int Slot { get; private set; }
    public SettlementId From { get; private set; }
    public SettlementId To { get; private set; }
    public string VehicleClass { get; private set; } = null!;
    public long Ticks { get; private set; }
    public long LengthMetres { get; private set; }
    public long ArriveTick { get; private set; }

    private MerchantTrip() { } // EF

    public static MerchantTrip Of(int slot, TripRecord r) => new()
    {
        Slot = slot, From = r.From, To = r.To, VehicleClass = r.VehicleClass, Ticks = r.Ticks,
        LengthMetres = r.LengthMetres, ArriveTick = r.ArriveTick,
    };

    public TripRecord Record => new(From, To, VehicleClass, Ticks, LengthMetres, ArriveTick);
}

/// <summary>
/// What one settlement (the knower) believes about travel from one settlement to another by one vehicle class.
/// Key (KnowerSettlementId, FromSettlementId, ToSettlementId, VehicleClass). Rows start as estimates from the travel graph
/// and are replaced by newer reports from merchants arriving at the knower (<see cref="Offer"/>).
/// </summary>
public sealed class TravelKnowledge
{
    public WorldId WorldId { get; private set; }
    public SettlementId KnowerSettlementId { get; private set; }
    public SettlementId FromSettlementId { get; private set; }
    public SettlementId ToSettlementId { get; private set; }
    public string VehicleClass { get; private set; } = null!;
    public long Ticks { get; private set; }
    public long LengthMetres { get; private set; }
    public KnowledgeSource Source { get; private set; }

    /// <summary>Arrival tick of the trip that was reported; 0 for an estimate.</summary>
    public long UpdatedTick { get; private set; }

    private TravelKnowledge() { } // EF

    public static ErrorOr<TravelKnowledge> Estimate(WorldId world, SettlementId knower, SettlementId from, SettlementId to,
        string vehicleClass, long ticks, long lengthMetres)
    {
        if (ticks < 1) return Error.Validation("knowledge.ticks.min", "Travel time must be at least 1 tick.");
        if (lengthMetres < 0) return Error.Validation("knowledge.length.negative", "Length must not be negative.");
        if (from == to) return Error.Validation("knowledge.selfloop", "From and to must differ.");
        return new TravelKnowledge
        {
            WorldId = world, KnowerSettlementId = knower, FromSettlementId = from, ToSettlementId = to,
            VehicleClass = vehicleClass, Ticks = ticks, LengthMetres = lengthMetres, Source = KnowledgeSource.Estimated, UpdatedTick = 0,
        };
    }

    /// <summary>A new row from a report (the knower had no row for this pair and class).</summary>
    public static TravelKnowledge FromReport(WorldId world, SettlementId knower, TripRecord trip) => new()
    {
        WorldId = world, KnowerSettlementId = knower, FromSettlementId = trip.From, ToSettlementId = trip.To,
        VehicleClass = trip.VehicleClass, Ticks = Math.Max(1, trip.Ticks), LengthMetres = trip.LengthMetres,
        Source = KnowledgeSource.Reported, UpdatedTick = trip.ArriveTick,
    };

    /// <summary>
    /// Takes a report for this row's pair and class when it is newer than what the row holds (an estimate is older than
    /// any report). Returns true when the row changed.
    /// </summary>
    public bool Offer(TripRecord trip)
    {
        if (trip.From != FromSettlementId || trip.To != ToSettlementId || trip.VehicleClass != VehicleClass)
            throw new ArgumentException("The report is for another pair or class.", nameof(trip));
        if (Source == KnowledgeSource.Reported && trip.ArriveTick <= UpdatedTick) return false;
        Ticks = Math.Max(1, trip.Ticks);
        LengthMetres = trip.LengthMetres;
        Source = KnowledgeSource.Reported;
        UpdatedTick = trip.ArriveTick;
        return true;
    }

    /// <summary>
    /// Estimated travel times: for every settlement node S and every vehicle class S offers (by its port kind), the
    /// shortest travel-graph time to every settlement reachable within <see cref="World.KnowledgeRadiusTicks"/>.
    /// </summary>
    public static IEnumerable<TravelKnowledge> Estimates(World world, IReadOnlyList<RouteNode> nodes, IReadOnlyList<Route> routes,
        IReadOnlyList<VehicleClass> vehicles)
    {
        var settlementOf = nodes.Where(n => n.SettlementId is not null).ToDictionary(n => n.Id, n => n.SettlementId!.Value);
        foreach (var vehicle in vehicles)
        {
            var graph = new TravelGraph(TravelGraph.EdgesFor(routes, vehicle, world, Tick.DefaultMinutesPerDay));
            foreach (var s in nodes.Where(n => n.SettlementId is not null && vehicle.OfferedAt(n.PortKind)).OrderBy(n => n.Id.Value))
                foreach (var r in graph.FindReachable(s.Id, world.KnowledgeRadiusTicks))
                    if (settlementOf.TryGetValue(r.Node, out var to) && to != s.SettlementId!.Value)
                    {
                        var row = Estimate(world.Id, s.SettlementId!.Value, s.SettlementId!.Value, to, vehicle.Name,
                            r.Path.TotalTicks, r.Path.TotalLengthMetres);
                        if (!row.IsError) yield return row.Value;
                    }
        }
    }
}
