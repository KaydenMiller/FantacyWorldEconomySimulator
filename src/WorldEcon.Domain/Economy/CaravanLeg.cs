using WorldEcon.Domain.Geography;
using WorldEcon.SharedKernel;

namespace WorldEcon.Domain.Economy;

/// <summary>
/// One route of a caravan's trip: the caravan enters at <see cref="EnterTick"/> and leaves at <see cref="ExitTick"/>.
/// <see cref="LengthMetres"/> is the distance this leg covers, copied from the route so a position can be computed without
/// loading the route. <see cref="OffsetMetres"/> is how far along the route (from the travel start end) the leg begins:
/// 0 for a whole route, more for the remainder of a leg split by <see cref="LegSchedule.Rebase"/>.
/// </summary>
public sealed record CaravanLeg(int Sequence, RouteId RouteId, TravelDirection Direction, Tick EnterTick, Tick ExitTick, long LengthMetres,
    long OffsetMetres = 0);

public enum LegPositionKind { AtOrigin, OnRoute, AtDestination }

/// <summary>Where a caravan is: before departure, on a route (metres from the leg's start end), or arrived.</summary>
public sealed record LegPosition(LegPositionKind Kind, RouteId? Route, TravelDirection? Direction, long MetresAlong, int? Sequence = null);
