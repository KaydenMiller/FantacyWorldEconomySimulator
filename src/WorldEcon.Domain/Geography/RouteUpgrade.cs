using WorldEcon.SharedKernel;

namespace WorldEcon.Domain.Geography;

/// <summary>History row: a route's surface changed at a tick. Key (RouteId, Tick).</summary>
public sealed class RouteUpgrade
{
    public RouteId RouteId { get; private set; }
    public Tick Tick { get; private set; }
    public RouteSurface FromSurface { get; private set; }
    public RouteSurface ToSurface { get; private set; }

    private RouteUpgrade() { } // EF

    public RouteUpgrade(RouteId routeId, Tick tick, RouteSurface fromSurface, RouteSurface toSurface)
    {
        RouteId = routeId;
        Tick = tick;
        FromSurface = fromSurface;
        ToSurface = toSurface;
    }
}
