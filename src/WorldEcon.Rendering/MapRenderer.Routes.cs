using System.Numerics;
using System.Runtime.CompilerServices;
using SkiaSharp;
using WorldEcon.WorldGen.Grid;

namespace WorldEcon.Rendering;

/// <summary>
/// Route lines over the map: a polyline through each route's tile centres, drawn in the overlay (above the GPU picture)
/// at every level. Land is a solid tan line on a dark casing, Sea a dashed blue line, Air a dotted violet line.
/// At Globe View markers sit at their globe cell centre rather than on their tile, so a route end on a marker's tile
/// gets one more segment to that centre and the line meets the dot. Points closer than <see cref="RouteMinStepPx"/> to the previous drawn point are skipped, so a long route costs about
/// one segment per few pixels on screen.
/// </summary>
public sealed partial class MapRenderer
{
    /// <summary>Land route colour (tan).</summary>
    public static readonly SKColor LandRouteColor = new(0xFF, 0xD0, 0x8A);

    /// <summary>Sea route colour (light blue, about 45 % opaque so overlapping voyages read as busier lanes).</summary>
    public static readonly SKColor SeaRouteColor = new(0x7F, 0xD8, 0xFF, 0x73);

    /// <summary>Air route colour (violet).</summary>
    public static readonly SKColor AirRouteColor = new(0xE8, 0xB8, 0xFF);

    /// <summary>Smallest on-screen step between drawn route points, pixels.</summary>
    public const float RouteMinStepPx = 2f;

    private sealed class RouteGeometry(Vector3[] points, int[] globeCells)
    {
        public readonly Vector3[] Points = points;
        public readonly int[] GlobeCells = globeCells;
    }

    private readonly ConditionalWeakTable<MapRoute, RouteGeometry> _routeGeometry = new();
    private readonly HashSet<int> _markerTiles = [];
    private readonly SKPath[] _routePaths = [new(), new(), new()];
    private readonly SKPath _highlightPath = new();
    private readonly SKPaint _highlight = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round, Color = SKColors.Yellow };
    private readonly SKPaint _routeCasing = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round, Color = new SKColor(0x2A, 0x1C, 0x0E, 0xC0) };
    private readonly SKPaint _routeLand = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round, Color = LandRouteColor };
    private readonly SKPaint _routeSea = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeJoin = SKStrokeJoin.Round, Color = SeaRouteColor, PathEffect = SKPathEffect.CreateDash([9f, 6f], 0) };
    private readonly SKPaint _routeAir = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round, Color = AirRouteColor, PathEffect = SKPathEffect.CreateDash([0.1f, 6f], 0) };

    /// <summary>Number of routes whose geometry is cached (tests).</summary>
    internal int CachedRouteCount => _routeGeometry.Count();

    private RouteGeometry? Geometry(MapRoute route)
    {
        if (_routeGeometry.TryGetValue(route, out var g)) return g;
        if (_w.Tiles is not { } tiles || route.Tiles.Count < 2) return null;
        var points = new Vector3[route.Tiles.Count];
        var cells = new int[route.Tiles.Count];
        for (var i = 0; i < points.Length; i++)
        {
            var t = route.Tiles[i];
            if ((uint)t >= (uint)tiles.Count) return null;
            points[i] = ToF(tiles.Centre(t));
            cells[i] = GridHierarchy.Parent(tiles, _w.GlobeTiles, t);
        }

        g = new RouteGeometry(points, cells);
        _routeGeometry.AddOrUpdate(route, g);
        return g;
    }

    private void DrawRoutes(SKCanvas canvas, ref Frame f, IReadOnlyList<MapRoute> routes)
    {
        foreach (var p in _routePaths) p.Rewind();
        _highlightPath.Rewind();
        var grouped = f.Level != MapLevel.Globe;
        _markerTiles.Clear();
        if (!grouped && f.Options.Settlements && f.Options.Markers is { } markers)
            foreach (var m in markers)
                if (m.TileId >= 0) _markerTiles.Add(m.TileId);
        var opts = f.Options;
        foreach (var route in routes)
        {
            var on = route.Kind switch { MapRouteKind.Land => opts.LandRoutes, MapRouteKind.Sea => opts.SeaRoutes, _ => opts.AirRoutes };
            if (!on || Geometry(route) is not { } g) continue;
            AppendRoute(_routePaths[(int)route.Kind], ref f, route, g, grouped);
            if (opts.HighlightedRoute == route.RouteId) AppendRoute(_highlightPath, ref f, route, g, grouped);
        }

        if (!_highlightPath.IsEmpty)
        {
            _highlight.StrokeWidth = 6.5f;
            canvas.DrawPath(_highlightPath, _highlight);
        }

        if (!_routePaths[0].IsEmpty)
        {
            _routeCasing.StrokeWidth = 4.5f;
            canvas.DrawPath(_routePaths[0], _routeCasing);
            _routeLand.StrokeWidth = 2.5f;
            canvas.DrawPath(_routePaths[0], _routeLand);
        }

        if (!_routePaths[1].IsEmpty)
        {
            _routeSea.StrokeWidth = 2.5f;
            canvas.DrawPath(_routePaths[1], _routeSea);
        }

        if (!_routePaths[2].IsEmpty)
        {
            _routeAir.StrokeWidth = 3f;
            canvas.DrawPath(_routePaths[2], _routeAir);
        }
    }

    /// <summary>Appends one route's line to <paramref name="path"/> (see <see cref="DrawRoutes"/>).</summary>
    private void AppendRoute(SKPath path, ref Frame f, MapRoute route, RouteGeometry g, bool grouped)
    {
        var view = f.View;
        var open = false;
        float lx = 0, ly = 0;
        if (_markerTiles.Contains(route.Tiles[0]) && view.ProjectFast(_gCentres[g.GlobeCells[0]], out var sx, out var sy))
        {
            path.MoveTo(sx, sy);
            open = true;
            (lx, ly) = (sx, sy);
        }

        for (var i = 0; i < g.Points.Length; i++)
        {
            if (!view.ProjectFast(g.Points[i], out var x, out var y))
            {
                open = false; // behind the globe: break the line
                continue;
            }

            if (grouped) (x, y) = Apply(ClusterCached(ref f, g.GlobeCells[i]), x, y);
            if (!open)
            {
                path.MoveTo(x, y);
                open = true;
            }
            else if (i == g.Points.Length - 1 || MathF.Abs(x - lx) + MathF.Abs(y - ly) >= RouteMinStepPx)
            {
                path.LineTo(x, y);
            }
            else
            {
                continue;
            }

            lx = x;
            ly = y;
        }

        if (open && _markerTiles.Contains(route.Tiles[^1]) && view.ProjectFast(_gCentres[g.GlobeCells[^1]], out var ex, out var ey))
            path.LineTo(ex, ey);
    }

    /// <summary>Port ring colour (the sea route blue, opaque).</summary>
    public static readonly SKColor PortRingColor = new(0x7F, 0xD8, 0xFF);

    private readonly SKPaint _portRing = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, Color = PortRingColor };

    /// <summary>A ring around a port's dot: 1 px for a river port, 2.5 px for a lake or ocean port.</summary>
    private void DrawPortRing(SKCanvas canvas, SKPoint p, MapPortKind kind)
    {
        _portRing.StrokeWidth = kind == MapPortKind.Coastal ? 2.5f : 1f;
        canvas.DrawCircle(p, 7.5f, _portRing);
    }

    private void DisposeRoutes()
    {
        _portRing.Dispose();
        foreach (var p in _routePaths) p.Dispose();
        _highlightPath.Dispose();
        _highlight.Dispose();
        _routeCasing.Dispose();
        _routeLand.Dispose();
        _routeSea.PathEffect?.Dispose();
        _routeSea.Dispose();
        _routeAir.PathEffect?.Dispose();
        _routeAir.Dispose();
    }
}
