using WorldEcon.WorldGen.Grid;

namespace WorldEcon.Rendering;

/// <summary>
/// A route's drawn line on the unit sphere (its tile centres), measured by great-circle length so a fraction of the
/// route's length maps to a point on the line. Used to place caravans: <c>metresAlong / LengthMetres</c> is the fraction.
/// Lengths are computed once; lookups are a binary search and one spherical interpolation, with no allocation.
/// </summary>
public sealed class RoutePolyline
{
    private readonly Vector3d[] _points;
    private readonly double[] _cumulative; // radians from the first point to point i

    public RoutePolyline(IReadOnlyList<Vector3d> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count == 0) throw new ArgumentException("A polyline needs at least one point.", nameof(points));
        _points = points.Select(p => p.Normalize()).ToArray();
        _cumulative = new double[_points.Length];
        for (var i = 1; i < _points.Length; i++)
            _cumulative[i] = _cumulative[i - 1] + Angle(_points[i - 1], _points[i]);
    }

    /// <summary>Total great-circle length, radians.</summary>
    public double LengthRadians => _cumulative[^1];

    /// <summary>The point at <paramref name="fraction"/> of the length from the first point (clamped to [0, 1]; NaN is 0).</summary>
    public Vector3d PointAt(double fraction)
    {
        var (i, t) = Locate(fraction);
        return i + 1 >= _points.Length ? _points[i] : Slerp(_points[i], _points[i + 1], t);
    }

    /// <summary>
    /// Unit vector tangent to the sphere at <see cref="PointAt"/>, pointing toward the last point. A zero-length line
    /// returns some unit tangent (never NaN).
    /// </summary>
    public Vector3d DirectionAt(double fraction)
    {
        var (i, t) = Locate(fraction);
        var p = i + 1 >= _points.Length ? _points[i] : Slerp(_points[i], _points[i + 1], t);
        // The next distinct point ahead, else the previous one behind (reversed).
        for (var j = i + 1; j < _points.Length; j++)
            if (Tangent(p, _points[j]) is { } ahead) return ahead;
        for (var j = i; j >= 0; j--)
            if (Tangent(p, _points[j]) is { } behind) return behind * -1;
        var any = Vector3d.Cross(p, Math.Abs(p.Y) < 0.9 ? new Vector3d(0, 1, 0) : new Vector3d(1, 0, 0));
        return any.Normalize();
    }

    private (int Segment, double T) Locate(double fraction)
    {
        if (double.IsNaN(fraction) || fraction <= 0 || LengthRadians <= 0) return (0, 0);
        if (fraction >= 1) return (_points.Length - 1, 0);
        var target = fraction * LengthRadians;
        var i = Array.BinarySearch(_cumulative, target);
        if (i < 0) i = ~i - 1;
        i = Math.Clamp(i, 0, _points.Length - 2);
        var seg = _cumulative[i + 1] - _cumulative[i];
        return (i, seg <= 0 ? 0 : (target - _cumulative[i]) / seg);
    }

    private static Vector3d? Tangent(Vector3d p, Vector3d toward)
    {
        var t = toward - p * Vector3d.Dot(p, toward);
        var len = t.Length;
        return len < 1e-12 ? null : t * (1 / len);
    }

    private static double Angle(Vector3d a, Vector3d b) => Math.Atan2(Vector3d.Cross(a, b).Length, Vector3d.Dot(a, b));

    private static Vector3d Slerp(Vector3d a, Vector3d b, double t)
    {
        var omega = Angle(a, b);
        if (omega < 1e-12) return a;
        var s = Math.Sin(omega);
        return (a * (Math.Sin((1 - t) * omega) / s) + b * (Math.Sin(t * omega) / s)).Normalize();
    }
}
