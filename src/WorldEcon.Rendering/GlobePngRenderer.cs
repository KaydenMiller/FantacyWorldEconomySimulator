using System.Numerics;
using SkiaSharp;
using WorldEcon.WorldGen.Pipeline;

namespace WorldEcon.Rendering;

/// <summary>
/// Renders a world to a PNG: a thin wrapper that draws through <see cref="MapRenderer"/> onto an offscreen surface and
/// encodes it; also yaw/pitch helpers for picking the view centre.
/// </summary>
public static class GlobePngRenderer
{
    public static readonly SKColor BackgroundColor = MapRenderer.BackgroundColor;

    /// <summary>
    /// Renders a <see cref="MapRenderer"/> view to a PNG. At Detail View the visible tile blocks are loaded first
    /// (<see cref="MapRenderer.LoadVisibleAsync"/>), so the image shows tiles, not the region-cell fallback.
    /// </summary>
    /// <exception cref="InvalidDataException">At Detail View, a visible tile block failed to load or is not stored (the
    /// image would show region cells there); the CLI then exits with code 1 instead of writing an incomplete image.</exception>
    public static byte[] RenderMap(MapRenderer renderer, MapViewState view, MapDrawOptions options)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentNullException.ThrowIfNull(view);
        renderer.LoadVisibleAsync(view).GetAwaiter().GetResult();
        if (renderer.Cache is { } cache && renderer.EffectiveLevel(view) == MapLevel.Detail)
        {
            var cells = new List<int>();
            renderer.VisibleGlobeCells(view, cells);
            var failed = cells.Where(c => cache.StateOf(c) == TileBlockState.Failed).ToList();
            var missing = cells.Where(c => cache.StateOf(c) == TileBlockState.Missing).ToList();
            if (failed.Count > 0)
                throw new InvalidDataException($"{failed.Count} visible tile block(s) failed to load (first: globe cell {failed[0]}): {cache.LastError?.Message}", cache.LastError);
            if (missing.Count > 0)
                throw new InvalidDataException($"{missing.Count} visible globe cell(s) have no stored tile block (first: {missing[0]}); regenerate the world.");
        }

        using var surface = SKSurface.Create(new SKImageInfo((int)view.Width, (int)view.Height));
        renderer.Draw(surface.Canvas, view, options);
        // Block textures are built a few per frame; draw again until every visible one is built (bounded).
        for (var i = 0; i < 500 && renderer.LastFramePending; i++) renderer.Draw(surface.Canvas, view, options);
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>
    /// Renders a world at yaw/pitch (yaw about +Y, then pitch about +X, as <see cref="GlobeViewState"/>) and <paramref name="zoom"/> (1 = whole globe; the level
    /// follows from the on-screen cell size, see <see cref="MapViewState"/>).
    /// </summary>
    public static byte[] RenderMap(MapWorld world, int width, int height, double yawDeg, double pitchDeg, double zoom, MapDrawOptions options)
    {
        ArgumentNullException.ThrowIfNull(world);
        using var renderer = new MapRenderer(world);
        return RenderMap(renderer, world.ViewFromYawPitch(width, height, yawDeg, pitchDeg, zoom), options);
    }

    /// <summary>World-space unit vector at the image centre: the rotated +Z axis.</summary>
    public static Vector3 ViewDirection(double yawDeg, double pitchDeg) => new GlobeViewState(1, 1, yawDeg, pitchDeg).ViewDirection;

    /// <summary>The cell whose centre is nearest <see cref="ViewDirection"/> (ties: lowest id).</summary>
    public static int NearestCell(WorldLayers l, double yawDeg, double pitchDeg)
    {
        var view = ViewDirection(yawDeg, pitchDeg);
        var best = 0;
        var bestDot = float.MinValue;
        for (var c = 0; c < l.Grid.CellCount; c++)
        {
            var p = l.Geometry.Centre(c);
            var d = Vector3.Dot(new Vector3((float)p.X, (float)p.Y, (float)p.Z), view);
            if (d > bestDot)
            {
                bestDot = d;
                best = c;
            }
        }

        return best;
    }

    /// <summary>Pixel position of a cell centre; false when the cell faces away from the viewer.</summary>
    public static bool TryProjectCentre(WorldLayers l, int cell, int width, int height, double yawDeg, double pitchDeg, out float x, out float y) =>
        new GlobeViewState(width, height, yawDeg, pitchDeg).TryProject(l.Geometry.Centre(cell), out x, out y);
}
