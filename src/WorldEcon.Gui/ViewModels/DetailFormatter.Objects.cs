using WorldEcon.Application.Globe;
using WorldEcon.Application.Queries;
using WorldEcon.Domain.Geography;
using WorldEcon.SharedKernel;
using WorldEcon.Simulation.Time;

namespace WorldEcon.Gui.ViewModels;

/// <summary>Details rows for caravans, junctions and sites.</summary>
public static partial class DetailFormatter
{
    /// <summary>"Y{year} M{month} D{day} HH:mm" of a tick in the world's calendar (as the TUI prints caravan dates).</summary>
    public static string Date(WorldSession session, long tick)
    {
        var d = new CalendarSystem(session.Data.World.Calendar).ToDate(new Tick(tick));
        return string.Create(Inv, $"Y{d.Year} M{d.Month} D{d.Day} {d.Hour:D2}:{d.Minute:D2}");
    }

    /// <summary>"Road · Category · Surface" of a route, or "unknown route".</summary>
    public static string RouteText(WorldSession session, Guid routeId) => session.Route(routeId) is { } r
        ? $"{(r.RoadName.Length == 0 ? "(no road)" : r.RoadName)} · {r.Category} · {r.Surface}"
        : "unknown route";

    /// <summary>Title and rows for a caravan at the layer's tick.</summary>
    public static (string Title, IReadOnlyList<DetailRow> Rows) Caravan(CaravanOnMap c, CaravanLayer layer, WorldSession session)
    {
        var onRoute = layer.TryPosition(c.Id, out var legIndex, out var metres);
        var total = c.Legs.Sum(l => l.LengthMetres);
        string route = "not on a route", leg = "—", progress = "—";
        if (onRoute)
        {
            var l = c.Legs[legIndex];
            route = RouteText(session, l.RouteId);
            leg = string.Create(Inv, $"{legIndex + 1} of {c.Legs.Count}");
            var along = c.Legs.Take(legIndex).Sum(x => x.LengthMetres) + (metres - l.OffsetMetres);
            progress = string.Create(Inv, $"{along / 1000.0:F1} of {total / 1000.0:F1} km");
        }

        var currency = session.Data.World.Currency;
        return ($"Caravan: {c.GoodName} × {c.Quantity}",
        [
            new("Owner", $"merchant {c.OwnerId.ToString()[..8]}"),
            new("Seat", c.SeatName),
            new("From → To", $"{c.OriginName} → {c.DestinationName}"),
            new("Goods", $"{c.GoodName} × {c.Quantity}"),
            new("Vehicle", c.VehicleClass),
            new("Route", route),
            new("Leg", leg),
            new("Progress", progress),
            new("Departs", Date(session, c.DepartTick)),
            new("Arrives", Date(session, c.ArriveTick)),
            new("Returns", Date(session, c.ReturnTick)),
            new("Cost basis", $"{currency.Format(new Money(c.UnitCostBasis))} per unit"),
        ]);
    }

    /// <summary>One link row per caravan: "good × quantity, origin → destination".</summary>
    public static IReadOnlyList<DetailRow> CaravanLinks(IEnumerable<CaravanOnMap> caravans, Action<Guid> open) =>
        caravans.Select(c => new DetailRow("Caravan", $"{c.GoodName} × {c.Quantity}, {c.OriginName} → {c.DestinationName}", () => open(c.Id))).ToList();

    /// <summary>Title and rows for a junction or site: kind, name, tile, position, routes and caravans on them.</summary>
    public static (string Title, IReadOnlyList<DetailRow> Rows) Node(GlobeNode n, WorldSession session, CaravanLayer? layer, Action<Guid> openCaravan)
    {
        var (lat, lon) = LatLon(session.Data.TileGrid.Centre(n.TileId));
        var rows = new List<DetailRow>
        {
            new("Kind", n.Kind.ToString()),
            new("Name", n.Name ?? "unnamed"),
            new("Tile", n.TileId.ToString(Inv)),
            new("Lat / lon", $"{lat.ToString("F4", Inv)}°, {lon.ToString("F4", Inv)}°"),
        };
        foreach (var id in n.RouteIds)
            if (session.Route(id) is { } r) rows.Add(new("Route", RoutesText([r])));
        var here = new HashSet<Guid>(n.RouteIds);
        var caravans = layer is null ? [] : layer.All.Where(c => layer.TryPosition(c.Id, out var i, out _) && here.Contains(c.Legs[i].RouteId)).ToList();
        rows.Add(new("Caravans on these routes", caravans.Count.ToString(Inv)));
        rows.AddRange(CaravanLinks(caravans, openCaravan));
        return ($"{n.Kind}: {n.Label}", rows);
    }

    /// <summary>The junction or site on a tile: "Junction @ tile n" when unnamed, else "Kind Name"; "none" without one.</summary>
    public static string NodeText(WorldSession session, int tile) => session.NodeOnTile(tile) is { } n
        ? n.Name is null ? n.Label : $"{n.Kind} {n.Name}"
        : "none";

    /// <summary>Hover text for a caravan: "good × quantity, origin → destination".</summary>
    public static string HoverText(CaravanOnMap c) => $"{c.GoodName} × {c.Quantity}, {c.OriginName} → {c.DestinationName}";
}
