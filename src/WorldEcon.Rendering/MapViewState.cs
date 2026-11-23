using System.Numerics;
using WorldEcon.WorldGen.Grid;

namespace WorldEcon.Rendering;

/// <summary>The three zoom levels of the map: whole globe cells, region (in-between) cells, 5 km tiles.</summary>
public enum MapLevel
{
    Globe,
    Region,
    Detail,
}

/// <summary>Pixel thresholds that pick the <see cref="MapLevel"/> and the sphere-to-flat ramp.</summary>
/// <param name="DetailMinTilePx">Detail level when a tile spans at least this many pixels.</param>
/// <param name="RegionMinCellPx">Region level when a region cell spans at least this many pixels.</param>
/// <param name="FlatStartPx">Region-cell size at which flatness starts to rise from 0.</param>
/// <param name="FlatFullPx">Region-cell size at which flatness reaches 1.</param>
public sealed record MapLevelThresholds(
    double DetailMinTilePx = 22,
    double RegionMinCellPx = 30,
    double FlatStartPx = 30,
    double FlatFullPx = 90);

/// <summary>
/// Immutable camera for the continuous globe → region → detail zoom. The view centre is a unit vector, the screen axes
/// are derived from it and a fixed <see cref="North"/> reference (screen up = north projected onto the tangent plane, no
/// roll). A point at angle θ from the centre lands at radius <c>scale × ((1 − f)·sin θ + f·θ)</c> from the canvas
/// centre, where f is <see cref="Flatness"/> (0 sphere, 1 azimuthal equidistant). Screen y points down.
/// </summary>
public sealed class MapViewState
{
    private readonly Vector3d _right;
    private readonly Vector3d _up;
    private readonly Vector3 _rightF;
    private readonly Vector3 _upF;
    private readonly Vector3 _centreF;
    private readonly double _maxAngle;
    private readonly double _maxRadius;
    private readonly float _scaleF;
    private readonly float _flatF;
    private readonly float _maxAngleF;

    public MapViewState(
        Vector3d centre,
        double scalePxPerRad,
        double width,
        double height,
        double regionCellAngle,
        double tileAngle,
        MapLevelThresholds? thresholds = null,
        Vector3d? north = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(regionCellAngle);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(tileAngle);
        Centre = centre.Normalize();
        North = (north ?? new Vector3d(0, 1, 0)).Normalize();
        Width = width;
        Height = height;
        RegionCellAngle = regionCellAngle;
        TileAngle = tileAngle;
        Thresholds = thresholds ?? new MapLevelThresholds();
        ScalePxPerRad = Math.Clamp(scalePxPerRad, MinScale, Math.Max(MinScale, MaxScale));

        (_up, _right) = Basis(Centre, North);

        var regionPx = RegionCellAngle * ScalePxPerRad;
        var tilePx = TileAngle * ScalePxPerRad;
        var span = Thresholds.FlatFullPx - Thresholds.FlatStartPx;
        var t = span <= 0 ? (regionPx >= Thresholds.FlatFullPx ? 1 : 0) : Math.Clamp((regionPx - Thresholds.FlatStartPx) / span, 0, 1);
        Flatness = t * t * (3 - (2 * t));
        Level = tilePx >= Thresholds.DetailMinTilePx ? MapLevel.Detail
            : regionPx >= Thresholds.RegionMinCellPx ? MapLevel.Region
            : MapLevel.Globe;

        _maxAngle = MaxAngleFor(Flatness);
        _maxRadius = RadiusAt(_maxAngle);

        _rightF = ToF(_right);
        _upF = ToF(_up);
        _centreF = ToF(Centre);
        _scaleF = (float)ScalePxPerRad;
        _flatF = (float)Flatness;
        _maxAngleF = (float)_maxAngle;
    }

    public Vector3d Centre { get; }

    /// <summary>World direction that points up on screen when the centre is not at a pole.</summary>
    public Vector3d North { get; }

    public double ScalePxPerRad { get; }

    public double Width { get; }

    public double Height { get; }

    /// <summary>Angular size (radians) of a region cell; supplied by the caller, see <see cref="MeanAngularSpacing"/>.</summary>
    public double RegionCellAngle { get; }

    /// <summary>Angular size (radians) of a tile; supplied by the caller.</summary>
    public double TileAngle { get; }

    public MapLevelThresholds Thresholds { get; }

    /// <summary>0 = sphere, 1 = flat; smoothstep of the region-cell pixel size between the flatness thresholds.</summary>
    public double Flatness { get; }

    public MapLevel Level { get; }

    public double CentreX => Width / 2;

    public double CentreY => Height / 2;

    /// <summary>Smallest scale: the whole globe fits (disc radius 0.45 × the shorter canvas side).</summary>
    public double MinScale => 0.45 * Math.Min(Width, Height);

    /// <summary>Largest scale: a tile spans 200 px.</summary>
    public double MaxScale => 200 / TileAngle;

    /// <summary>
    /// Largest angle from the centre that is drawn: min(90° + 180°·f, θ where screen radius stops increasing). The
    /// second bound keeps the projection one-to-one; for f ≥ 0.5 it is π, for f = 0 it is 90°.
    /// </summary>
    public double MaxAngle => _maxAngle;

    /// <summary>Mean angular spacing of <paramref name="count"/> cells that tile the sphere: sqrt(4π / count).</summary>
    public static double MeanAngularSpacing(long count) => Math.Sqrt(4 * Math.PI / count);

    /// <summary>Maximum visible angle at flatness <paramref name="f"/>; see <see cref="MaxAngle"/>.</summary>
    public static double MaxAngleFor(double f)
    {
        if (f >= 0.5) return Math.PI;
        var peak = Math.Acos(-f / (1 - f));
        return Math.Min(Math.Min((Math.PI / 2) + (Math.PI * f), Math.PI), peak);
    }

    /// <summary>State whose camera matches a <see cref="GlobeViewState"/> (same centre; scale = its disc radius per radian).</summary>
    public static MapViewState FromGlobeView(GlobeViewState view, double regionCellAngle, double tileAngle, MapLevelThresholds? thresholds = null)
    {
        var d = view.ViewDirection;
        return new MapViewState(new Vector3d(d.X, d.Y, d.Z), view.Radius, view.Width, view.Height, regionCellAngle, tileAngle, thresholds);
    }

    public static MapViewState FromYawPitch(
        double width, double height, double yawDeg, double pitchDeg, double zoom, double regionCellAngle, double tileAngle, MapLevelThresholds? thresholds = null) =>
        FromGlobeView(new GlobeViewState(width, height, yawDeg, pitchDeg, zoom), regionCellAngle, tileAngle, thresholds);

    public MapViewState WithSize(double width, double height) => Copy(Centre, ScalePxPerRad, width, height);

    public MapViewState WithScale(double scalePxPerRad) => Copy(Centre, scalePxPerRad, Width, Height);

    public MapViewState CentreOn(Vector3d centre) => Copy(centre, ScalePxPerRad, Width, Height);

    /// <summary>Screen radius in pixels of a point at angle <paramref name="theta"/> from the centre.</summary>
    public double RadiusAt(double theta) => ScalePxPerRad * (((1 - Flatness) * Math.Sin(theta)) + (Flatness * theta));

    /// <summary>
    /// Pixel position of a unit vector, or null when it is hidden (beyond <see cref="MaxAngle"/>).
    /// </summary>
    public (double X, double Y)? Project(Vector3d p)
    {
        var x = Vector3d.Dot(p, _right);
        var y = Vector3d.Dot(p, _up);
        var z = Vector3d.Dot(p, Centre);
        var s = Math.Sqrt((x * x) + (y * y));
        var theta = Math.Atan2(s, z);
        if (theta > _maxAngle) return null;
        if (s < 1e-300) return z > 0 ? (CentreX, CentreY) : null; // the antipode has no direction on screen
        var r = RadiusAt(theta);
        return (CentreX + (r * x / s), CentreY - (r * y / s));
    }

    /// <summary>Inverse of <see cref="Project"/>: the unit vector under a pixel, or null beyond the visible cap.</summary>
    public Vector3d? Unproject(double sx, double sy)
    {
        var dx = sx - CentreX;
        var dy = -(sy - CentreY);
        var r = Math.Sqrt((dx * dx) + (dy * dy));
        if (r > _maxRadius * (1 + 1e-12)) return null;
        if (r < 1e-12) return Centre;

        // r(theta) is increasing on [0, MaxAngle]; bisect (deterministic, 64 halvings).
        double lo = 0, hi = _maxAngle;
        for (var i = 0; i < 64; i++)
        {
            var mid = 0.5 * (lo + hi);
            if (RadiusAt(mid) < r) lo = mid;
            else hi = mid;
        }

        var theta = 0.5 * (lo + hi);
        var dir = (_right * (dx / r)) + (_up * (dy / r));
        return ((Centre * Math.Cos(theta)) + (dir * Math.Sin(theta))).Normalize();
    }

    /// <summary>
    /// Single-precision projection for bulk use. Returns false when hidden (x and y are then 0). Precision is about
    /// 1e-7 rad × <see cref="ScalePxPerRad"/> pixels, so 0.03 px at the maximum scale for 5 km tiles.
    /// </summary>
    public bool ProjectFast(Vector3 p, out float x, out float y)
    {
        var px = Vector3.Dot(p, _rightF);
        var py = Vector3.Dot(p, _upF);
        var pz = Vector3.Dot(p, _centreF);
        var s = MathF.Sqrt((px * px) + (py * py));
        var theta = MathF.Atan2(s, pz);
        if (theta > _maxAngleF)
        {
            x = 0;
            y = 0;
            return false;
        }

        if (s < 1e-20f)
        {
            // The centre, or the antipode (hidden: it has no direction on screen).
            x = (float)CentreX;
            y = (float)CentreY;
            return pz > 0;
        }

        var r = _scaleF * (((1 - _flatF) * MathF.Sin(theta)) + (_flatF * theta));
        x = (float)CentreX + (r * px / s);
        y = (float)CentreY - (r * py / s);
        return true;
    }

    /// <summary>
    /// Like <see cref="ProjectFast"/>, but a hidden point is placed on the edge of the visible cap (its angle clamped to
    /// <see cref="MaxAngle"/>) instead of being dropped, so a polygon that straddles the edge keeps all its vertices.
    /// Returns false when the point was clamped.
    /// </summary>
    public bool ProjectClamped(Vector3 p, out float x, out float y)
    {
        var px = Vector3.Dot(p, _rightF);
        var py = Vector3.Dot(p, _upF);
        var pz = Vector3.Dot(p, _centreF);
        var s = MathF.Sqrt((px * px) + (py * py));
        var theta = MathF.Atan2(s, pz);
        var visible = theta <= _maxAngleF;
        if (!visible) theta = _maxAngleF;
        if (s < 1e-20f)
        {
            if (pz > 0)
            {
                x = (float)CentreX;
                y = (float)CentreY;
                return visible;
            }

            // The antipode: placed at the right-hand edge of the cap.
            x = (float)(CentreX + _maxRadius);
            y = (float)CentreY;
            return false;
        }

        var r = _scaleF * (((1 - _flatF) * MathF.Sin(theta)) + (_flatF * theta));
        x = (float)CentreX + (r * px / s);
        y = (float)CentreY - (r * py / s);
        return visible;
    }

    /// <summary>Screen-right direction at the centre (world space, single precision).</summary>
    public Vector3 RightF => _rightF;

    /// <summary>Screen-up direction at the centre (world space, single precision).</summary>
    public Vector3 UpF => _upF;

    /// <summary><see cref="Centre"/> in single precision.</summary>
    public Vector3 CentreF => _centreF;

    /// <summary>Screen radius in pixels of the edge of the visible cap (<see cref="RadiusAt"/> of <see cref="MaxAngle"/>).</summary>
    public double MaxRadius => _maxRadius;

    /// <summary>
    /// Drag by (<paramref name="dxPx"/>, <paramref name="dyPx"/>) pixels: the centre rotates by the angle the drag spans
    /// at the screen centre (scale is d r / d θ at θ = 0 for every flatness), so the content follows the pointer exactly
    /// at the centre and approximately elsewhere.
    /// </summary>
    public MapViewState Pan(double dxPx, double dyPx)
    {
        var len = Math.Sqrt((dxPx * dxPx) + (dyPx * dyPx));
        if (len < 1e-12) return this;
        var alpha = Math.Min(len / ScalePxPerRad, Math.PI);
        var dir = ((_right * -dxPx) + (_up * dyPx)) * (1 / len);
        var c = (Centre * Math.Cos(alpha)) + (dir * Math.Sin(alpha));
        return Copy(c, ScalePxPerRad, Width, Height);
    }

    /// <summary>
    /// Multiplies the scale by <paramref name="factor"/> (clamped to [<see cref="MinScale"/>, <see cref="MaxScale"/>]) and
    /// re-centres so the point under (x, y) stays there; falls back to centre zoom when the pixel shows no point.
    /// Flatness changes with scale, so the re-centring iterates (at most 24 corrections, stops under 0.01 px).
    /// </summary>
    public MapViewState ZoomAt(double factor, double x, double y)
    {
        var anchor = Unproject(x, y);
        var zoomed = WithScale(ScalePxPerRad * factor);
        if (anchor is not { } p) return zoomed;

        var best = zoomed;
        var bestErr = double.MaxValue;
        var cur = zoomed;
        for (var i = 0; i < 24; i++)
        {
            if (cur.Project(p) is not { } q) break;
            var ex = x - q.X;
            var ey = y - q.Y;
            var err = Math.Sqrt((ex * ex) + (ey * ey));
            if (err < bestErr)
            {
                bestErr = err;
                best = cur;
            }

            if (err < 0.001) break;
            cur = cur.Pan(ex, ey);
        }

        return best;
    }

    private MapViewState Copy(Vector3d centre, double scale, double width, double height) =>
        new(centre, scale, width, height, RegionCellAngle, TileAngle, Thresholds, North);

    private static (Vector3d Up, Vector3d Right) Basis(Vector3d c, Vector3d north)
    {
        var u = north - (c * Vector3d.Dot(north, c));
        if (u.Length < 1e-9)
        {
            // Centre at a pole: use +X (or +Z when north is +X) as the up reference.
            var alt = Math.Abs(c.X) < 0.9 ? new Vector3d(1, 0, 0) : new Vector3d(0, 0, 1);
            u = alt - (c * Vector3d.Dot(alt, c));
        }

        u = u.Normalize();
        return (u, Vector3d.Cross(u, c));
    }

    private static Vector3 ToF(Vector3d v) => new((float)v.X, (float)v.Y, (float)v.Z);
}
