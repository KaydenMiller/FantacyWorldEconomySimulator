using SkiaSharp;
using WorldEcon.WorldGen.Grid;

namespace WorldEcon.Rendering;

/// <summary>How a <see cref="RiverChain"/> ends.</summary>
public enum RiverChainEnd
{
    /// <summary>Its last cell is ocean or lake; it is drawn to the midpoint toward that cell (the shore).</summary>
    Water,

    /// <summary>It joins a river with more flow at its last cell, where that river continues.</summary>
    Confluence,

    /// <summary>Its last cell has no drawn outflow.</summary>
    Land,
}

/// <summary>
/// One river from its source, downstream through region cells. <see cref="Flow"/>[i] is the flow of the edge leaving
/// <see cref="Cells"/>[i]; the last entry repeats the last edge's flow, so both arrays have the same length.
/// <see cref="StartsAtSource"/> is false for a chain walked from inside a drainage loop (its first cell has inflow).
/// </summary>
public sealed record RiverChain(int[] Cells, long[] Flow, RiverChainEnd End, bool StartsAtSource = true);

/// <summary>
/// A <see cref="RiverChain"/> after simplification and corner cutting: unit <see cref="Points"/> from the source centre
/// to the end (the shore midpoint for <see cref="RiverChainEnd.Water"/>), with the flow interpolated at each point.
/// </summary>
public sealed record SmoothedRiver(RiverChain Chain, Vector3d[] Points, double[] Flow);

/// <summary>
/// Joins river edges into chains and smooths them (design 2026-11-22-river-drawing, approach B): Douglas–Peucker
/// simplification on the sphere, then Chaikin corner cutting, never moving endpoints or confluence points.
/// </summary>
public static class RiverChains
{
    /// <summary>
    /// Chains over the edges from[i] → to[i] (each from cell has at most one outflow). A chain starts at each from cell
    /// that no edge flows into, in ascending id order, and walks downstream. At a cell with several inflows the one with
    /// the larger flow (ties: lower from cell) continues; the others end there with <see cref="RiverChainEnd.Confluence"/>.
    /// A chain also ends after a cell where <paramref name="isWater"/> is true, or at a cell with no outflow. Edges no
    /// source reaches (drainage loops) are walked afterwards from their lowest from cell, each chain stopping at an edge
    /// already drawn, so every edge is in exactly one chain.
    /// </summary>
    public static IReadOnlyList<RiverChain> Build(IReadOnlyList<int> from, IReadOnlyList<int> to, IReadOnlyList<long> flow, Func<int, bool> isWater)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(isWater);
        if (to.Count != from.Count || flow.Count != from.Count) throw new ArgumentException("from, to and flow must have the same length.");

        var outflow = new Dictionary<int, int>();
        var mainInflow = new Dictionary<int, int>();
        for (var e = 0; e < from.Count; e++)
        {
            if (!outflow.TryAdd(from[e], e)) throw new ArgumentException($"Cell {from[e]} has more than one outflow edge.");
            if (!mainInflow.TryGetValue(to[e], out var m) || flow[e] > flow[m] || (flow[e] == flow[m] && from[e] < from[m])) mainInflow[to[e]] = e;
        }

        var chains = new List<RiverChain>();
        var walked = new bool[from.Count];

        void Walk(int start)
        {
            var cells = new List<int> { start };
            var flows = new List<long>();
            var end = RiverChainEnd.Land;
            var cur = start;
            while (outflow.TryGetValue(cur, out var e))
            {
                if (walked[e]) { end = RiverChainEnd.Confluence; break; } // a drainage loop closes on a drawn edge
                walked[e] = true;
                flows.Add(flow[e]);
                var next = to[e];
                cells.Add(next);
                if (isWater(next)) { end = RiverChainEnd.Water; break; }
                if (mainInflow[next] != e) { end = RiverChainEnd.Confluence; break; }
                cur = next;
            }

            if (flows.Count == 0) return;
            flows.Add(flows[^1]);
            chains.Add(new RiverChain([.. cells], [.. flows], end, !mainInflow.ContainsKey(start)));
        }

        foreach (var source in from.Where(c => !mainInflow.ContainsKey(c)).Distinct().Order()) Walk(source);

        // Edges no source reaches lie in drainage loops (two cells recorded as draining into each other): walk them from
        // the lowest unwalked cell so every edge is drawn once.
        foreach (var e in Enumerable.Range(0, from.Count).OrderBy(e => from[e]))
            if (!walked[e]) Walk(from[e]);

        return chains;
    }

    /// <summary>Cells that two or more edges flow into.</summary>
    public static ISet<int> Confluences(IReadOnlyList<int> to)
    {
        ArgumentNullException.ThrowIfNull(to);
        var seen = new HashSet<int>();
        var result = new HashSet<int>();
        foreach (var c in to)
            if (!seen.Add(c)) result.Add(c);
        return result;
    }

    /// <summary>
    /// Simplifies <paramref name="chain"/> with Douglas–Peucker (angular <paramref name="toleranceRad"/>) and cuts corners
    /// <paramref name="chaikinIterations"/> times (new points at 1/4 and 3/4 of each piece, renormalised). The first and
    /// last points and the centres of <paramref name="fixedCells"/> are kept exactly. For a <see cref="RiverChainEnd.Water"/>
    /// chain the last point is the normalised midpoint of the last land centre and the water centre.
    /// </summary>
    public static SmoothedRiver Smooth(RiverChain chain, Func<int, Vector3d> centre, ISet<int> fixedCells, double toleranceRad, int chaikinIterations = 2)
    {
        ArgumentNullException.ThrowIfNull(chain);
        ArgumentNullException.ThrowIfNull(centre);
        ArgumentNullException.ThrowIfNull(fixedCells);
        var n = chain.Cells.Length;
        var pts = new Vector3d[n];
        var isFixed = new bool[n];
        for (var i = 0; i < n; i++)
        {
            pts[i] = centre(chain.Cells[i]);
            isFixed[i] = i == 0 || i == n - 1 || fixedCells.Contains(chain.Cells[i]);
        }

        if (chain.End == RiverChainEnd.Water && n >= 2) pts[^1] = (pts[^2] + pts[^1]).Normalize();

        // Douglas–Peucker between consecutive fixed points; the parameter s of a point is its index in the chain.
        var keep = new bool[n];
        var lo = 0;
        for (var i = 1; i < n; i++)
        {
            if (!isFixed[i]) continue;
            keep[lo] = keep[i] = true;
            Simplify(pts, keep, lo, i, toleranceRad);
            lo = i;
        }

        var points = new List<Vector3d>();
        var param = new List<double>();
        var fixedOut = new List<bool>();
        for (var i = 0; i < n; i++)
        {
            if (!keep[i]) continue;
            points.Add(pts[i]);
            param.Add(i);
            fixedOut.Add(isFixed[i]);
        }

        for (var it = 0; it < chaikinIterations; it++) (points, param, fixedOut) = Chaikin(points, param, fixedOut);

        var flow = new double[points.Count];
        for (var i = 0; i < flow.Length; i++)
        {
            var s = Math.Clamp(param[i], 0, n - 1);
            var k = Math.Min((int)Math.Floor(s), n - 2);
            flow[i] = n == 1 ? chain.Flow[0] : chain.Flow[k] + ((chain.Flow[k + 1] - chain.Flow[k]) * (s - k));
        }

        return new SmoothedRiver(chain, [.. points], flow);
    }

    private static void Simplify(Vector3d[] pts, bool[] keep, int lo, int hi, double tol)
    {
        if (hi - lo < 2) return;
        var worst = -1;
        var worstAngle = tol;
        for (var i = lo + 1; i < hi; i++)
        {
            var a = AngleToSegment(pts[i], pts[lo], pts[hi]);
            if (a > worstAngle)
            {
                worstAngle = a;
                worst = i;
            }
        }

        if (worst < 0) return;
        keep[worst] = true;
        Simplify(pts, keep, lo, worst, tol);
        Simplify(pts, keep, worst, hi, tol);
    }

    /// <summary>
    /// One corner-cutting pass over each piece between consecutive fixed points. A piece with interior points gets new
    /// points at 1/4 and 3/4 of every segment in place of its interior points; a piece that is a single segment has no
    /// corner and is copied. Fixed points are kept exactly.
    /// </summary>
    private static (List<Vector3d>, List<double>, List<bool>) Chaikin(List<Vector3d> p, List<double> s, List<bool> fixedIn)
    {
        var outP = new List<Vector3d>(p.Count * 2) { p[0] };
        var outS = new List<double>(p.Count * 2) { s[0] };
        var outF = new List<bool>(p.Count * 2) { true };
        var lo = 0;
        for (var hi = 1; hi < p.Count; hi++)
        {
            if (!fixedIn[hi] && hi != p.Count - 1) continue;
            if (hi - lo >= 2)
            {
                for (var i = lo; i < hi; i++)
                {
                    outP.Add(((0.75 * p[i]) + (0.25 * p[i + 1])).Normalize());
                    outS.Add((0.75 * s[i]) + (0.25 * s[i + 1]));
                    outF.Add(false);
                    outP.Add(((0.25 * p[i]) + (0.75 * p[i + 1])).Normalize());
                    outS.Add((0.25 * s[i]) + (0.75 * s[i + 1]));
                    outF.Add(false);
                }
            }

            outP.Add(p[hi]);
            outS.Add(s[hi]);
            outF.Add(true);
            lo = hi;
        }

        return (outP, outS, outF);
    }

    /// <summary>
    /// Where chevrons go along a screen polyline, upstream to downstream: the first at <paramref name="spacingPx"/> / 2
    /// of accumulated length, then one every <paramref name="spacingPx"/>. (Dx, Dy) is the unit direction of the piece
    /// the chevron lies on.
    /// </summary>
    public static IReadOnlyList<(float X, float Y, float Dx, float Dy)> ArrowPlacements(ReadOnlySpan<SKPoint> screenPath, float spacingPx)
    {
        var result = new List<(float, float, float, float)>();
        if (spacingPx <= 0) return result;
        var next = spacingPx / 2;
        var walked = 0f;
        for (var i = 0; i + 1 < screenPath.Length; i++)
        {
            var a = screenPath[i];
            var b = screenPath[i + 1];
            float dx = b.X - a.X, dy = b.Y - a.Y;
            var len = MathF.Sqrt((dx * dx) + (dy * dy));
            if (len <= 0) continue;
            while (next <= walked + len)
            {
                var t = (next - walked) / len;
                result.Add((a.X + (dx * t), a.Y + (dy * t), dx / len, dy / len));
                next += spacingPx;
            }

            walked += len;
        }

        return result;
    }

    /// <summary>Angle from <paramref name="p"/> to the great-circle segment a–b.</summary>
    public static double AngleToSegment(Vector3d p, Vector3d a, Vector3d b)
    {
        var n = Vector3d.Cross(a, b);
        if (n.Length > 1e-15)
        {
            n = n.Normalize();
            var foot = (p - (n * Vector3d.Dot(p, n))).Normalize();
            if (Vector3d.Dot(Vector3d.Cross(a, foot), n) >= 0 && Vector3d.Dot(Vector3d.Cross(foot, b), n) >= 0)
                return Math.Asin(Math.Min(1, Math.Abs(Vector3d.Dot(p, n))));
        }

        return Math.Min(Angle(p, a), Angle(p, b));
    }

    private static double Angle(Vector3d a, Vector3d b) => Math.Atan2(Vector3d.Cross(a, b).Length, Vector3d.Dot(a, b));
}
