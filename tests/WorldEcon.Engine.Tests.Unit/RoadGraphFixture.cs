using WorldEcon.Domain.Economy;
using WorldEcon.Domain.Geography;
using WorldEcon.Persistence;

namespace WorldEcon.Engine.Tests.Unit;

/// <summary>Builds a small travel graph for engine tests: a node per settlement and routes between them.</summary>
internal static class RoadGraphFixture
{
    /// <summary>Adds a settlement node for each settlement (tile = index), the default vehicle classes, and returns the nodes.</summary>
    public static RouteNode[] AddNodes(WorldDbContext ctx, WorldId world, params Settlement[] settlements)
        => AddNodes(ctx, world, settlements.Select(s => (s, PortKind.None)).ToArray());

    /// <summary>As <see cref="AddNodes(WorldDbContext, WorldId, Settlement[])"/>, with a port kind per settlement.</summary>
    public static RouteNode[] AddNodes(WorldDbContext ctx, WorldId world, params (Settlement Settlement, PortKind Port)[] settlements)
    {
        var nodes = settlements.Select((s, i) =>
            RouteNode.Create(world, RouteNodeKind.Settlement, i, s.Settlement.Id, s.Settlement.Name, s.Port).Value).ToArray();
        ctx.RouteNodes.AddRange(nodes);
        ctx.VehicleClasses.AddRange(VehicleClass.Defaults(world));
        return nodes;
    }

    /// <summary>Estimated travel times for every settlement (as world creation writes them) over the given routes.</summary>
    public static void AddEstimates(WorldDbContext ctx, World world, RouteNode[] nodes, params Route[] routes)
        => ctx.TravelKnowledge.AddRange(TravelKnowledge.Estimates(world, nodes, routes, VehicleClass.Defaults(world.Id)));

    /// <summary>A flat two-way route of <paramref name="lengthMetres"/>.</summary>
    public static Route AddRoute(WorldDbContext ctx, WorldId world, RouteNode a, RouteNode b, long lengthMetres,
        RouteCategory category = RouteCategory.Land, int maxTier = 99)
    {
        var r = Route.Create(world, a.Id, b.Id, category, Route.DefaultSurface(category), 100, 0, lengthMetres,
            100, 100, 1, maxTier, RouteProvenance.Generated).Value;
        ctx.Routes.Add(r);
        return r;
    }
}
