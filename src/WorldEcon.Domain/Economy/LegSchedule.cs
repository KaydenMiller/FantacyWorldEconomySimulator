using ErrorOr;
using WorldEcon.Domain.Geography;
using WorldEcon.SharedKernel;

namespace WorldEcon.Domain.Economy;

/// <summary>Builds, checks and re-times a caravan's legs.</summary>
public static class LegSchedule
{
    /// <summary>Contiguous legs for <paramref name="path"/> starting at <paramref name="depart"/>, Sequence from 0.</summary>
    public static IReadOnlyList<CaravanLeg> Build(TravelPath path, Tick depart)
    {
        var legs = new List<CaravanLeg>(path.Edges.Count);
        long t = depart.Value;
        for (int i = 0; i < path.Edges.Count; i++)
        {
            var e = path.Edges[i];
            legs.Add(new CaravanLeg(i, e.Route, e.Direction, new Tick(t), new Tick(t + e.Ticks), e.LengthMetres));
            t += e.Ticks;
        }
        return legs;
    }

    public static ErrorOr<Success> Validate(IReadOnlyList<CaravanLeg> legs)
    {
        if (legs.Count == 0)
            return Error.Validation("legs.empty", "A caravan needs at least one leg.");
        for (int i = 0; i < legs.Count; i++)
        {
            var l = legs[i];
            if (l.Sequence != i)
                return Error.Validation("legs.sequence", $"Leg {i} has sequence {l.Sequence}; expected 0..{legs.Count - 1} in order.");
            if (l.ExitTick.Value <= l.EnterTick.Value)
                return Error.Validation("legs.nonpositive", $"Leg {i} exits at or before it enters.");
            if (i > 0 && legs[i - 1].ExitTick != l.EnterTick)
                return Error.Validation("legs.gap", $"Leg {i} enters at {l.EnterTick.Value} but leg {i - 1} exits at {legs[i - 1].ExitTick.Value}.");
        }
        return Result.Success;
    }

    /// <summary>Position at <paramref name="tick"/>: binary search for the leg with EnterTick ≤ tick &lt; ExitTick.</summary>
    public static LegPosition PositionAt(IReadOnlyList<CaravanLeg> legs, Tick tick)
    {
        if (TryOnRoute(legs, tick.Value, out var index, out var metres))
        {
            var l = legs[index];
            return new LegPosition(LegPositionKind.OnRoute, l.RouteId, l.Direction, metres, l.Sequence);
        }

        return legs.Count == 0 || tick.Value < legs[0].EnterTick.Value
            ? new LegPosition(LegPositionKind.AtOrigin, null, null, 0)
            : new LegPosition(LegPositionKind.AtDestination, null, null, 0);
    }

    /// <summary>
    /// The leg in progress at <paramref name="tick"/> (index into <paramref name="legs"/>) and the metres from that leg's
    /// travel-start end of its route; false before the first leg or from the last leg's exit on. Allocates nothing, so a
    /// map can place many caravans every tick.
    /// </summary>
    public static bool TryOnRoute(IReadOnlyList<CaravanLeg> legs, long tick, out int index, out long metresAlong)
    {
        index = -1;
        metresAlong = 0;
        if (legs.Count == 0 || tick < legs[0].EnterTick.Value || tick >= legs[^1].ExitTick.Value) return false;
        int lo = 0, hi = legs.Count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (legs[mid].EnterTick.Value <= tick) lo = mid; else hi = mid - 1;
        }
        var l = legs[lo];
        long span = l.ExitTick.Value - l.EnterTick.Value;
        index = lo;
        metresAlong = l.OffsetMetres + (long)((Int128)(tick - l.EnterTick.Value) * l.LengthMetres / span);
        return true;
    }

    /// <summary>
    /// Re-times legs from <paramref name="at"/> on. <paramref name="ticksFor"/> gives the travel time of a number of metres
    /// of a leg's route under current conditions. Legs with ExitTick ≤ at are unchanged. A leg in progress at
    /// <paramref name="at"/> is split in two on the same route: the covered part keeps its old timing and ends at
    /// <paramref name="at"/>, the rest starts there (OffsetMetres = covered) and takes ticksFor(remaining metres). Later legs
    /// take ticksFor(their metres). The result is contiguous and renumbered from 0, and the position at
    /// <paramref name="at"/> is unchanged.
    /// </summary>
    public static IReadOnlyList<CaravanLeg> Rebase(IReadOnlyList<CaravanLeg> legs, Tick at, Func<CaravanLeg, long, long> ticksFor)
    {
        var result = new List<CaravanLeg>(legs.Count + 1);
        long cursor = 0;
        foreach (var l in legs)
        {
            if (l.ExitTick.Value <= at.Value)
            {
                result.Add(l with { Sequence = result.Count });
                cursor = l.ExitTick.Value;
                continue;
            }
            if (l.EnterTick.Value < at.Value)
            {
                long span = l.ExitTick.Value - l.EnterTick.Value;
                long covered = (long)((Int128)(at.Value - l.EnterTick.Value) * l.LengthMetres / span);
                long remaining = l.LengthMetres - covered;
                if (covered > 0)
                    result.Add(l with { Sequence = result.Count, ExitTick = at, LengthMetres = covered });
                long rest = Math.Max(1, ticksFor(l, remaining));
                long start = covered > 0 ? at.Value : l.EnterTick.Value;
                result.Add(l with
                {
                    Sequence = result.Count, EnterTick = new Tick(start), ExitTick = new Tick(at.Value + rest),
                    LengthMetres = remaining, OffsetMetres = l.OffsetMetres + covered,
                });
                cursor = at.Value + rest;
                continue;
            }
            long begin = result.Count == 0 ? l.EnterTick.Value : cursor;
            long ticks = Math.Max(1, ticksFor(l, l.LengthMetres));
            result.Add(l with { Sequence = result.Count, EnterTick = new Tick(begin), ExitTick = new Tick(begin + ticks) });
            cursor = begin + ticks;
        }
        return result;
    }
}
