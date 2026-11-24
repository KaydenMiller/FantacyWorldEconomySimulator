using System.Numerics;
using SkiaSharp;
using WorldEcon.WorldGen.Grid;

namespace WorldEcon.Rendering;

/// <summary>
/// Caravans, junctions and sites in the overlay, and object picks. Every drawn caravan, settlement marker and node is
/// recorded with the screen point it was drawn at (after Globe View grouping offsets), so
/// <see cref="PickObjects"/> hits exactly what is on screen. Caravans within <see cref="CaravanMergePx"/> of each other
/// are drawn as one marker with a count.
/// </summary>
public sealed partial class MapRenderer
{
    /// <summary>Site diamond colour (teal, distinct from settlement yellow and the route colours).</summary>
    public static readonly SKColor SiteColor = new(0x5F, 0xE0, 0xC8);

    /// <summary>Caravans closer than this (pixels) are drawn as one marker with a count.</summary>
    public const float CaravanMergePx = 6f;

    /// <summary>Pick radius of a caravan marker, pixels.</summary>
    public const float CaravanPickPx = 8f;

    /// <summary>Pick radius of a settlement marker, pixels.</summary>
    public const float SettlementPickPx = 6f;

    /// <summary>Pick radius of a junction or site, pixels.</summary>
    public const float NodePickPx = 6f;

    /// <summary>One drawn object: kind, id, screen point, pick radius, and the caravan group it was merged into (−1 otherwise).</summary>
    private readonly record struct Hit(MapObjectKind Kind, Guid Id, float X, float Y, float Radius, int Group);

    private readonly List<Hit> _hits = [];
    private readonly List<(SKPoint Anchor, string Label)> _nodeLabels = [];
    private readonly List<(float X, float Y, int Count, Vector3d Heading, MapRouteKind Kind, Vector3d At)> _caravanGroups = [];
    private readonly SKPath _triangle = new();
    private readonly SKPaint _objectFill = new() { IsAntialias = true, Style = SKPaintStyle.Fill };
    private readonly SKPaint _objectCasing = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, StrokeJoin = SKStrokeJoin.Round, Color = new SKColor(0x2A, 0x1C, 0x0E, 0xE0) };
    private readonly SKPaint _selectRing = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2.5f, Color = SKColors.Yellow };

    /// <summary>
    /// The objects under (x, y) as drawn by the last draw: every caravan of the nearest caravan marker within
    /// <see cref="CaravanPickPx"/> (several when merged), else the nearest settlement within <see cref="SettlementPickPx"/>,
    /// else the nearest junction or site within <see cref="NodePickPx"/>; empty when none. With <paramref name="only"/>,
    /// only objects of that kind are considered (a settlement under a caravan, for example).
    /// </summary>
    public IReadOnlyList<MapObject> PickObjects(double x, double y, MapObjectKind? only = null)
    {
        lock (_gate)
        {
            foreach (var kind in (ReadOnlySpan<MapObjectKind>)[MapObjectKind.Caravan, MapObjectKind.Settlement, MapObjectKind.Node])
            {
                if (only is { } o && o != kind) continue;
                var best = -1;
                var bestD = double.MaxValue;
                for (var i = 0; i < _hits.Count; i++)
                {
                    var h = _hits[i];
                    if (h.Kind != kind) continue;
                    var d = ((h.X - x) * (h.X - x)) + ((h.Y - y) * (h.Y - y));
                    if (d <= h.Radius * h.Radius && d < bestD) (best, bestD) = (i, d);
                }

                if (best < 0) continue;
                var hit = _hits[best];
                return hit.Group < 0
                    ? [new MapObject(hit.Kind, hit.Id)]
                    : _hits.Where(h => h.Kind == kind && h.Group == hit.Group).Select(h => new MapObject(h.Kind, h.Id)).ToList();
            }

            return [];
        }
    }

    /// <summary>Screen points of the objects drawn by the last draw, in draw order (tests and diagnostics).</summary>
    public IReadOnlyList<SKPoint> LastObjectPoints
    {
        get
        {
            lock (_gate) return _hits.Select(h => new SKPoint(h.X, h.Y)).ToList();
        }
    }

    /// <summary>Screen point of a world direction at the drawn level, with the group offset of its globe cell (null when hidden or off screen).</summary>
    private SKPoint? ObjectPoint(ref Frame f, Vector3d p, int globeCell, float margin)
    {
        if (!f.View.ProjectFast(ToF(p), out var x, out var y)) return null;
        if (f.Level != MapLevel.Globe && (uint)globeCell < (uint)_gCentres.Length) (x, y) = Apply(ClusterCached(ref f, globeCell), x, y);
        return f.OnScreen(x, y, margin) ? new SKPoint(x, y) : null;
    }

    private void DrawNodes(SKCanvas canvas, ref Frame f, IReadOnlyList<MapNode> nodes, bool labels)
    {
        if (_w.Tiles is not { } tiles) return;
        var o = f.Options;
        foreach (var n in nodes)
        {
            var junction = n.Kind == MapNodeKind.Junction;
            if (junction ? !o.Junctions || f.Level == MapLevel.Globe : !o.Sites) continue;
            if ((uint)n.TileId >= (uint)tiles.Count) continue;
            var at = f.Level == MapLevel.Globe && (uint)n.GlobeCell < (uint)_gCentres.Length
                ? _l.Geometry.Centre(n.GlobeCell)
                : tiles.Centre(n.TileId);
            if (ObjectPoint(ref f, at, n.GlobeCell, 0) is not { } p) continue;
            if (junction)
            {
                var r = n.Label is null ? 2f : 2.5f;
                _objectFill.Color = LandRouteColor;
                canvas.DrawCircle(p, r + 1.5f, _objectCasing);
                canvas.DrawCircle(p, r, _objectFill);
            }
            else
            {
                _triangle.Rewind();
                _triangle.MoveTo(p.X, p.Y - 5f);
                _triangle.LineTo(p.X + 4f, p.Y);
                _triangle.LineTo(p.X, p.Y + 5f);
                _triangle.LineTo(p.X - 4f, p.Y);
                _triangle.Close();
                _objectFill.Color = SiteColor;
                canvas.DrawPath(_triangle, _objectFill);
                canvas.DrawPath(_triangle, _objectCasing);
            }

            _hits.Add(new Hit(MapObjectKind.Node, n.Id, p.X, p.Y, NodePickPx, -1));
            if (labels && n.Label is { Length: > 0 } label) _nodeLabels.Add((p, label));
        }
    }

    private void DrawCaravans(SKCanvas canvas, ref Frame f, IReadOnlyList<MapCaravan> caravans)
    {
        var o = f.Options;
        _caravanGroups.Clear();
        for (var i = 0; i < caravans.Count; i++)
        {
            var c = caravans[i];
            var on = c.Kind switch { MapRouteKind.Land => o.LandRoutes, MapRouteKind.Sea => o.SeaRoutes, _ => o.AirRoutes };
            if (!on) continue;
            var cell = f.Level == MapLevel.Globe ? -1 : _w.GlobeTiles.Nearest(c.Position);
            if (ObjectPoint(ref f, c.Position, cell, 8) is not { } p) continue;
            var group = -1;
            for (var g = 0; g < _caravanGroups.Count; g++)
            {
                var e = _caravanGroups[g];
                if (((e.X - p.X) * (e.X - p.X)) + ((e.Y - p.Y) * (e.Y - p.Y)) <= CaravanMergePx * CaravanMergePx)
                {
                    group = g;
                    _caravanGroups[g] = e with { Count = e.Count + 1 };
                    break;
                }
            }

            if (group < 0)
            {
                group = _caravanGroups.Count;
                _caravanGroups.Add((p.X, p.Y, 1, c.Heading, c.Kind, c.Position));
            }

            _hits.Add(new Hit(MapObjectKind.Caravan, c.Id, _caravanGroups[group].X, _caravanGroups[group].Y, CaravanPickPx, group));
        }

        foreach (var g in _caravanGroups)
        {
            // Direction on screen: project a point a little ahead along the heading.
            var ahead = (g.At + (g.Heading * 1e-3)).Normalize();
            float dx = 1, dy = 0;
            if (f.View.ProjectFast(ToF(ahead), out var ax, out var ay) && f.View.ProjectFast(ToF(g.At), out var bx, out var by))
            {
                var len = MathF.Sqrt(((ax - bx) * (ax - bx)) + ((ay - by) * (ay - by)));
                if (len > 1e-4f) (dx, dy) = ((ax - bx) / len, (ay - by) / len);
            }

            _triangle.Rewind();
            _triangle.MoveTo(g.X + (dx * 6f), g.Y + (dy * 6f));
            _triangle.LineTo(g.X - (dx * 4f) - (dy * 4.5f), g.Y - (dy * 4f) + (dx * 4.5f));
            _triangle.LineTo(g.X - (dx * 4f) + (dy * 4.5f), g.Y - (dy * 4f) - (dx * 4.5f));
            _triangle.Close();
            _objectFill.Color = g.Kind switch { MapRouteKind.Land => LandRouteColor, MapRouteKind.Sea => SeaRouteColor.WithAlpha(255), _ => AirRouteColor };
            canvas.DrawPath(_triangle, _objectFill);
            canvas.DrawPath(_triangle, _objectCasing);
            if (g.Count > 1 && _font is not null)
            {
                var text = $"×{g.Count}";
                var rect = new SKRect(g.X + 7, g.Y - _font.Size, g.X + 8 + _font.MeasureText(text), g.Y + 1);
                LabelLayout.Draw(canvas, text, rect, _font, _text, _halo);
            }
        }
    }

    private void DrawSelectedObject(SKCanvas canvas, MapObject obj)
    {
        foreach (var h in _hits)
        {
            if (h.Kind != obj.Kind || h.Id != obj.Id) continue;
            canvas.DrawCircle(h.X, h.Y, 10f, _selectRing);
            return;
        }
    }

    private void DisposeObjects()
    {
        _triangle.Dispose();
        _objectFill.Dispose();
        _objectCasing.Dispose();
        _selectRing.Dispose();
    }
}
