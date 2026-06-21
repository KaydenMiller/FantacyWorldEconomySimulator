using ErrorOr;
using WorldEcon.SharedKernel.Domain;

namespace WorldEcon.Domain.Geography;

/// <summary>A directed edge between two settlements (spec §9.1–9.2).</summary>
public sealed class Route : AggregateRoot<RouteId>
{
    public WorldId WorldId { get; }
    public SettlementId FromSettlementId { get; private set; }
    public SettlementId ToSettlementId { get; private set; }
    public long Distance { get; private set; }
    public Terrain Terrain { get; private set; }
    public int Danger { get; private set; }
    public RouteCategory Category { get; private set; }

    // Parameterless ctor for EF Core materialization.
    private Route() : base(default) { }

    private Route(RouteId id, WorldId worldId, SettlementId from, SettlementId to,
        long distance, Terrain terrain, int danger, RouteCategory category) : base(id)
    {
        WorldId = worldId;
        FromSettlementId = from;
        ToSettlementId = to;
        Distance = distance;
        Terrain = terrain;
        Danger = danger;
        Category = category;
    }

    public static ErrorOr<Route> Create(WorldId worldId, SettlementId from, SettlementId to,
        long distance, Terrain terrain, int danger, RouteCategory category)
    {
        if (from.Equals(to))
            return Error.Validation("route.selfloop", "A route may not connect a settlement to itself.");
        if (distance <= 0)
            return Error.Validation("route.distance.nonpositive", "Distance must be positive.");
        if (danger < 0)
            return Error.Validation("route.danger.negative", "Danger must not be negative.");

        return new Route(RouteId.New(), worldId, from, to, distance, terrain, danger, category);
    }
}
