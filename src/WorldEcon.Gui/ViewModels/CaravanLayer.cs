using System.Collections;
using WorldEcon.Application.Queries;
using WorldEcon.Domain.Economy;
using WorldEcon.Domain.Geography;
using WorldEcon.Rendering;
using WorldEcon.SharedKernel;

namespace WorldEcon.Gui.ViewModels;

/// <summary>
/// The caravans the map draws. <see cref="Load"/> takes a <see cref="CaravanPositionsQuery"/> result; <see cref="SetTick"/>
/// places every caravan that is on a route at that tick on its route's drawn line, in memory (no database read, no
/// allocation per caravan), so the clock can move every tick or every few ticks. A caravan whose route has no drawn
/// line is listed (<see cref="Caravan"/>) but not placed. Framework-free.
/// </summary>
public sealed class CaravanLayer
{
    private readonly WorldSession _session;
    private readonly Dictionary<Guid, RoutePolyline?> _lines = [];
    private CaravanOnMap[] _caravans = [];
    private CaravanLeg[][] _legs = [];
    private Dictionary<Guid, CaravanOnMap> _byId = [];
    private MapCaravan[] _placed = [];
    private readonly Placed _onRoute;

    public CaravanLayer(WorldSession session)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _onRoute = new Placed(this);
    }

    /// <summary>Raised once after each <see cref="SetTick"/> (and so after <see cref="Load"/>).</summary>
    public event EventHandler? Changed;

    /// <summary>Caravans on a route at the last <see cref="SetTick"/>, in load order.</summary>
    public IReadOnlyList<MapCaravan> OnRoute => _onRoute;

    /// <summary>All loaded caravans (on a route or not).</summary>
    public IReadOnlyList<CaravanOnMap> All => _caravans;

    /// <summary>Tick of the last <see cref="SetTick"/>.</summary>
    public long Tick { get; private set; }

    /// <summary>
    /// Where a loaded caravan is at <see cref="Tick"/>: the index of the leg in progress and metres from that leg's
    /// travel-start end of its route; false when it is not on a route (or unknown).
    /// </summary>
    public bool TryPosition(Guid id, out int legIndex, out long metresAlong)
    {
        legIndex = -1;
        metresAlong = 0;
        var i = Array.FindIndex(_caravans, c => c.Id == id);
        return i >= 0 && LegSchedule.TryOnRoute(_legs[i], Tick, out legIndex, out metresAlong);
    }

    /// <summary>A loaded caravan, or null.</summary>
    public CaravanOnMap? Caravan(Guid id) => _byId.GetValueOrDefault(id);

    /// <summary>Replaces the caravans, sets the session's current tick to the one read with them, and places them at it.</summary>
    public void Load(CaravanPositions positions)
    {
        ArgumentNullException.ThrowIfNull(positions);
        _caravans = positions.Caravans.ToArray();
        _legs = _caravans.Select(c => c.Legs.Select(l => new CaravanLeg(l.Sequence, new RouteId(l.RouteId), l.Direction,
            new Tick(l.EnterTick), new Tick(l.ExitTick), l.LengthMetres, l.OffsetMetres)).ToArray()).ToArray();
        _byId = _caravans.ToDictionary(c => c.Id);
        _placed = new MapCaravan[_caravans.Length];
        _session.CurrentTick = positions.CurrentTick;
        SetTick(positions.CurrentTick);
    }

    /// <summary>Places every caravan on a route at <paramref name="tick"/> and raises <see cref="Changed"/> once.</summary>
    public void SetTick(long tick)
    {
        Tick = tick;
        var count = 0;
        for (var i = 0; i < _caravans.Length; i++)
        {
            var legs = _legs[i];
            if (!LegSchedule.TryOnRoute(legs, tick, out var index, out var metres)) continue;
            var leg = legs[index];
            var routeId = leg.RouteId.Value;
            if (Line(routeId) is not { } line || _session.Route(routeId) is not { } route) continue;
            var fraction = route.LengthMetres > 0 ? Math.Clamp((double)metres / route.LengthMetres, 0, 1) : 0;
            var reverse = leg.Direction == TravelDirection.BtoA;
            if (reverse) fraction = 1 - fraction;
            var heading = line.DirectionAt(fraction);
            _placed[count++] = new MapCaravan(_caravans[i].Id, line.PointAt(fraction), reverse ? heading * -1 : heading,
                (MapRouteKind)(int)route.Category);
        }

        _onRoute.Count = count;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private RoutePolyline? Line(Guid routeId)
    {
        // The miss path is a separate method: its lambda's closure would otherwise be allocated on every call.
        return _lines.TryGetValue(routeId, out var line) ? line : BuildLine(routeId);
    }

    private RoutePolyline? BuildLine(Guid routeId)
    {
        var line = _session.RouteLine(routeId) is { } r && _session.World.Tiles is { } tiles
            ? new RoutePolyline(r.Tiles.Select(t => tiles.Centre(t)).ToArray())
            : null;
        _lines[routeId] = line;
        return line;
    }

    /// <summary>A read-only view of the first <see cref="Count"/> placed markers (no copy per tick).</summary>
    private sealed class Placed(CaravanLayer owner) : IReadOnlyList<MapCaravan>
    {
        public int Count { get; set; }

        public MapCaravan this[int index] => (uint)index < (uint)Count ? owner._placed[index] : throw new ArgumentOutOfRangeException(nameof(index));

        public IEnumerator<MapCaravan> GetEnumerator()
        {
            for (var i = 0; i < Count; i++) yield return owner._placed[i];
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
