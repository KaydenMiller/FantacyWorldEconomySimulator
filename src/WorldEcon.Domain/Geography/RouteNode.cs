using ErrorOr;
using WorldEcon.SharedKernel.Domain;

namespace WorldEcon.Domain.Geography;

/// <summary>
/// A point where routes meet: a settlement, a junction where two routes cross, or a named site.
/// Each node sits on one tile of the world's tile grid.
/// </summary>
public sealed class RouteNode : AggregateRoot<RouteNodeId>
{
    public WorldId WorldId { get; private set; }
    public RouteNodeKind Kind { get; private set; }
    public int TileId { get; private set; }
    public SettlementId? SettlementId { get; private set; }
    public string? Name { get; private set; }

    /// <summary>Port kind of a settlement node (None for non-ports and for junctions and sites).</summary>
    public PortKind PortKind { get; private set; }

    private RouteNode() : base(default) { } // EF

    private RouteNode(RouteNodeId id, WorldId worldId, RouteNodeKind kind, int tileId, SettlementId? settlementId, string? name)
        : base(id)
    {
        WorldId = worldId;
        Kind = kind;
        TileId = tileId;
        SettlementId = settlementId;
        Name = name;
    }

    public static ErrorOr<RouteNode> Create(WorldId worldId, RouteNodeKind kind, int tileId, SettlementId? settlementId, string? name,
        PortKind portKind = PortKind.None)
    {
        if (portKind != PortKind.None && kind != RouteNodeKind.Settlement)
            return Error.Validation("routenode.port.settlementOnly", "Only settlement nodes can be ports.");
        if (tileId < 0)
            return Error.Validation("routenode.tile.negative", "Tile id must not be negative.");
        if (kind == RouteNodeKind.Settlement && settlementId is null)
            return Error.Validation("routenode.settlement.required", "A settlement node needs a settlement id.");
        if (kind != RouteNodeKind.Settlement && settlementId is not null)
            return Error.Validation("routenode.settlement.forbidden", $"A {kind} node must not have a settlement id.");
        return new RouteNode(RouteNodeId.New(), worldId, kind, tileId, settlementId, name) { PortKind = portKind };
    }
}
