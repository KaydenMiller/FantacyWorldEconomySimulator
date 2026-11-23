using ErrorOr;
using WorldEcon.SharedKernel.Domain;

namespace WorldEcon.Domain.Geography;

/// <summary>
/// One edge of the travel graph between two <see cref="RouteNode"/>s, traversable in both directions
/// unless a direction's speed factor is 0. Its tile path is stored as <see cref="RouteTile"/> rows.
/// </summary>
public sealed class Route : AggregateRoot<RouteId>
{
    public const int MaxFactor = 1_000;

    public WorldId WorldId { get; private set; }
    public RouteNodeId NodeA { get; private set; }
    public RouteNodeId NodeB { get; private set; }
    public RouteCategory Category { get; private set; }
    public RouteSurface Surface { get; private set; }

    /// <summary>Percentage slowdown from terrain (100 = none).</summary>
    public int Difficulty { get; private set; }
    public int Danger { get; private set; }

    /// <summary>Number of caravans dispatched over this route.</summary>
    public long TrafficCount { get; private set; }
    public long LengthMetres { get; private set; }

    /// <summary>Percentage of surface speed travelling NodeA → NodeB (0 = impassable that way).</summary>
    public int SpeedFactorAtoB { get; private set; }

    /// <summary>Percentage of surface speed travelling NodeB → NodeA (0 = impassable that way).</summary>
    public int SpeedFactorBtoA { get; private set; }
    public int MinTier { get; private set; }
    public int MaxTier { get; private set; }
    public RouteProvenance Provenance { get; private set; }

    private Route() : base(default) { } // EF

    private Route(RouteId id) : base(id) { }

    public static ErrorOr<Route> Create(WorldId worldId, RouteNodeId a, RouteNodeId b, RouteCategory category,
        RouteSurface surface, int difficulty, int danger, long lengthMetres, int speedFactorAtoB, int speedFactorBtoA,
        int minTier, int maxTier, RouteProvenance provenance)
    {
        if (a == b)
            return Error.Validation("route.selfloop", "A route may not connect a node to itself.");
        if (lengthMetres <= 0)
            return Error.Validation("route.length.nonpositive", "Route length must be positive.");
        if (difficulty <= 0)
            return Error.Validation("route.difficulty.nonpositive", "Difficulty must be positive.");
        if (danger < 0)
            return Error.Validation("route.danger.negative", "Danger must not be negative.");
        if (speedFactorAtoB is < 0 or > MaxFactor || speedFactorBtoA is < 0 or > MaxFactor)
            return Error.Validation("route.factor.range", $"Speed factors must be between 0 and {MaxFactor}.");
        if (speedFactorAtoB == 0 && speedFactorBtoA == 0)
            return Error.Validation("route.factor.bothZero", "A route must be passable in at least one direction.");
        if (minTier < 1)
            return Error.Validation("route.tier.min", "Minimum tier must be at least 1.");
        if (minTier > maxTier)
            return Error.Validation("route.tier.order", "Minimum tier must not exceed maximum tier.");
        if (!SurfaceFits(category, surface))
            return Error.Validation("route.surface.category", $"Surface {surface} is not valid for a {category} route.");

        return new Route(RouteId.New())
        {
            WorldId = worldId, NodeA = a, NodeB = b, Category = category, Surface = surface,
            Difficulty = difficulty, Danger = danger, LengthMetres = lengthMetres,
            SpeedFactorAtoB = speedFactorAtoB, SpeedFactorBtoA = speedFactorBtoA,
            MinTier = minTier, MaxTier = maxTier, Provenance = provenance,
        };
    }

    /// <summary>True when <paramref name="surface"/> belongs to <paramref name="category"/>.</summary>
    public static bool SurfaceFits(RouteCategory category, RouteSurface surface) => category switch
    {
        RouteCategory.Land => surface is RouteSurface.Track or RouteSurface.DirtRoad or RouteSurface.Paved,
        RouteCategory.Sea => surface is RouteSurface.OpenWater,
        RouteCategory.Air => surface is RouteSurface.Sky,
        _ => false,
    };

    /// <summary>The surface a generated route of <paramref name="category"/> gets when the seed names none.</summary>
    public static RouteSurface DefaultSurface(RouteCategory category) => category switch
    {
        RouteCategory.Sea => RouteSurface.OpenWater,
        RouteCategory.Air => RouteSurface.Sky,
        _ => RouteSurface.DirtRoad,
    };

    /// <summary>Changes the surface and returns the old one. The new surface must fit the category.</summary>
    public ErrorOr<RouteSurface> ChangeSurface(RouteSurface surface)
    {
        if (!SurfaceFits(Category, surface))
            return Error.Validation("route.surface.category", $"Surface {surface} is not valid for a {Category} route.");
        var old = Surface;
        Surface = surface;
        return old;
    }

    public void AddTraffic() => TrafficCount++;

    public int SpeedFactor(TravelDirection direction) =>
        direction == TravelDirection.AtoB ? SpeedFactorAtoB : SpeedFactorBtoA;

    public RouteNodeId Start(TravelDirection direction) => direction == TravelDirection.AtoB ? NodeA : NodeB;

    public RouteNodeId End(TravelDirection direction) => direction == TravelDirection.AtoB ? NodeB : NodeA;

    /// <summary>True when a vehicle of <paramref name="vehicle"/>'s category and tier may use this route.</summary>
    public bool Allows(VehicleClass vehicle) =>
        vehicle.Category == Category && vehicle.Tier >= MinTier && vehicle.Tier <= MaxTier;
}
