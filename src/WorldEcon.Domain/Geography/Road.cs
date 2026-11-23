using ErrorOr;
using WorldEcon.SharedKernel.Domain;

namespace WorldEcon.Domain.Geography;

/// <summary>One route of a <see cref="Road"/>, in order, with the direction the road travels it.</summary>
public sealed record RoadRouteEntry(int Sequence, RouteId RouteId, TravelDirection Direction);

/// <summary>
/// A named grouping of routes, as authored in the seed: the routes it was split into, in travel order.
/// Every route belongs to exactly one road.
/// </summary>
public sealed class Road : AggregateRoot<RoadId>
{
    private readonly List<RoadRouteEntry> _routes = [];

    public WorldId WorldId { get; private set; }
    public string Name { get; private set; }
    public RouteCategory Category { get; private set; }
    public RouteProvenance Provenance { get; private set; }
    public IReadOnlyList<RoadRouteEntry> Routes => _routes;

    private Road() : base(default) { Name = null!; } // EF

    private Road(RoadId id, WorldId worldId, string name, RouteCategory category, RouteProvenance provenance) : base(id)
    {
        WorldId = worldId;
        Name = name;
        Category = category;
        Provenance = provenance;
    }

    public static ErrorOr<Road> Create(WorldId worldId, string name, RouteCategory category, RouteProvenance provenance,
        IReadOnlyList<(RouteId Route, TravelDirection Direction)> routes)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Error.Validation("road.name.empty", "Road name must not be empty.");
        if (routes.Count == 0)
            return Error.Validation("road.routes.empty", "A road needs at least one route.");
        var road = new Road(RoadId.New(), worldId, name.Trim(), category, provenance);
        for (int i = 0; i < routes.Count; i++)
            road._routes.Add(new RoadRouteEntry(i, routes[i].Route, routes[i].Direction));
        return road;
    }
}
