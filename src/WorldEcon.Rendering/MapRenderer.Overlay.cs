using SkiaSharp;
using WorldEcon.WorldGen.Grid;

namespace WorldEcon.Rendering;

/// <summary>
/// Hexes as tools over the continuous imagery: the optional hex grid of the view's level (globe cells, region cells or
/// tiles) and the hover hex, whose neighbours fade out over <see cref="HoverRings"/> rings.
/// </summary>
public sealed partial class MapRenderer
{
    /// <summary>Rings of neighbours outlined around the hovered hex.</summary>
    public const int HoverRings = 3;

    /// <summary>Hex grid outlines: white at 18 % opacity, 1 px.</summary>
    public static readonly SKColor HexGridColor = new(255, 255, 255, 46);

    /// <summary>Opacity of the hover outline at ring distance 0 … <see cref="HoverRings"/>.</summary>
    public static readonly float[] HoverRingAlpha = [1f, 0.55f, 0.25f, 0.10f];

    private readonly SKPath _gridPath = new();
    private readonly SKPath[] _ringPaths = [new(), new(), new(), new()];
    private readonly List<int> _ringFrontier = [];
    private readonly List<int> _ringNext = [];
    private readonly HashSet<int> _ringSeen = [];

    /// <summary>Outlines every visible hex of the drawn level.</summary>
    private void DrawHexGrid(SKCanvas canvas, ref Frame f)
    {
        var view = f.View;
        _gridPath.Rewind();
        switch (f.Level)
        {
            case MapLevel.Globe:
            {
                var margin = (float)(_w.GlobeCellAngle * view.ScalePxPerRad);
                for (var c = 0; c < _gCentres.Length; c++)
                {
                    if (!view.ProjectFast(_gCentres[c], out var x, out var y) || !f.OnScreen(x, y, margin)) continue;
                    AddOutline(_gridPath, ref f, new MapPick(MapLevel.Globe, c, c, -1));
                }

                break;
            }

            case MapLevel.Region when _r is not null:
                foreach (var g in _visible)
                    foreach (var c in _regionsOf!.TilesOf(g))
                        AddOutline(_gridPath, ref f, new MapPick(MapLevel.Region, c, g, c));
                break;

            case MapLevel.Detail when _cache is not null:
                foreach (var g in _visible)
                {
                    if (_cache.TryGet(g, out _) is not { } e) continue;
                    var c = ClusterCached(ref f, g);
                    var margin = (float)(_w.TileAngle * view.ScalePxPerRad);
                    for (var i = 0; i < e.Centres.Length; i++)
                    {
                        if (!view.ProjectFast(e.Centres[i], out var cx, out var cy) || !f.OnScreen(cx, cy, margin)) continue;
                        int n = e.CornerCount[i], start = i * 6;
                        for (var k = 0; k < n; k++)
                        {
                            view.ProjectClamped(e.Corners[start + k], out var x, out var y);
                            (x, y) = Apply(c, x, y);
                            if (k == 0) _gridPath.MoveTo(x, y);
                            else _gridPath.LineTo(x, y);
                        }

                        _gridPath.Close();
                    }
                }

                break;
        }

        _stroke.Color = HexGridColor;
        _stroke.StrokeWidth = 1f;
        canvas.DrawPath(_gridPath, _stroke);
    }

    /// <summary>
    /// The hovered hex (full white outline, unless it is also the selection) and its neighbours out to
    /// <see cref="HoverRings"/> rings, each ring at <see cref="HoverRingAlpha"/> of full opacity.
    /// </summary>
    private void DrawHoverRings(SKCanvas canvas, ref Frame f, MapPick hover, bool skipCentre)
    {
        foreach (var p in _ringPaths) p.Rewind();
        _ringSeen.Clear();
        _ringFrontier.Clear();
        _ringFrontier.Add(hover.Id);
        _ringSeen.Add(hover.Id);
        Span<int> nb = stackalloc int[6];
        for (var ring = 0; ring <= HoverRings && _ringFrontier.Count > 0; ring++)
        {
            _ringNext.Clear();
            foreach (var id in _ringFrontier)
            {
                if (!(ring == 0 && skipCentre)) AddOutline(_ringPaths[ring], ref f, Neighbour(hover, id));
                if (ring == HoverRings) continue;
                var count = Neighbours(hover.Level, id, nb);
                for (var k = 0; k < count; k++)
                    if (_ringSeen.Add(nb[k])) _ringNext.Add(nb[k]);
            }

            _ringFrontier.Clear();
            _ringFrontier.AddRange(_ringNext);
        }

        for (var ring = HoverRings; ring >= 0; ring--)
        {
            if (_ringPaths[ring].IsEmpty) continue;
            _stroke.Color = new SKColor(255, 255, 255, (byte)Math.Round(255 * HoverRingAlpha[ring]));
            _stroke.StrokeWidth = ring == 0 ? 2f : 1.5f;
            canvas.DrawPath(_ringPaths[ring], _stroke);
        }
    }

    /// <summary>A pick for hex <paramref name="id"/> at the hovered level (with its globe cell, which group scaling needs).</summary>
    private MapPick Neighbour(MapPick hover, int id) => hover.Level switch
    {
        MapLevel.Region when _r is not null => new MapPick(MapLevel.Region, id, _r.GlobeParent[id], id),
        MapLevel.Detail when _w.Tiles is { } tiles => new MapPick(MapLevel.Detail, id, GridHierarchy.Parent(tiles, _w.GlobeTiles, id), -1),
        _ => new MapPick(hover.Level, id, id, -1),
    };

    private int Neighbours(MapLevel level, int id, Span<int> into)
    {
        ReadOnlySpan<int> ring;
        switch (level)
        {
            case MapLevel.Globe:
                ring = _l.Grid.Neighbours(id);
                break;
            case MapLevel.Region when _r is not null:
                ring = _r.Grid.Neighbours(id);
                break;
            case MapLevel.Detail when _w.Tiles is { } tiles:
                return tiles.Neighbours(id, into);
            default:
                return 0;
        }

        ring.CopyTo(into);
        return ring.Length;
    }
}
