using System.Numerics;

namespace WorldEcon.Rendering.Gpu;

/// <summary>
/// Everything the GPU needs per frame (design 2026-11-23-gpu-map-rendering §8): the camera from a
/// <see cref="MapViewState"/>, the layer mode, gap and shading. About 100 bytes; the only per-frame CPU work of the
/// GPU path besides issuing the draw calls. The background pass paints <see cref="OuterColour"/> outside the visible
/// cap (radius <see cref="MaxRadius"/>) and <see cref="DiscColour"/> inside it (it shows in the gaps between cells). Lengths are in device-independent pixels; <see cref="PixelRatio"/> converts
/// to framebuffer pixels in the vertex shader.
/// </summary>
public readonly record struct GpuFrame(
    Vector3 Right,
    Vector3 Up,
    Vector3 Centre,
    float ScalePxPerRad,
    float Flatness,
    float MaxAngle,
    Vector2 Viewport,
    float PixelRatio,
    GpuLayerMode Mode,
    float EdgeInsetPx,
    bool Shaded,
    float MaxRadius,
    Vector3 DiscColour,
    Vector3 OuterColour)
{
    /// <summary>Light direction of the shaded globe (as the CPU renderer's).</summary>
    public static readonly Vector3 Light = Vector3.Normalize(new Vector3(-0.4f, 0.5f, 1f));

    /// <summary>
    /// Uniforms for <paramref name="view"/> drawn with <paramref name="options"/> at <paramref name="level"/>. Edge inset:
    /// half the 1 px cell gap at Globe View; at Region and Detail View the group step (1.5 px) plus half the cell gap
    /// (0.5 px), the CPU renderer's 4 px between globe-cell groups.
    /// </summary>
    public static GpuFrame For(MapViewState view, MapDrawOptions options, MapLevel level, float pixelRatio)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(options);
        var inset = !options.Borders ? 0f
            : level == MapLevel.Globe ? MapRenderer.GlobeGapPx / 2
            : ((MapRenderer.GroupGapPx - MapRenderer.CellGapPx) / 2) + (MapRenderer.CellGapPx / 2);
        static Vector3 Rgb(SkiaSharp.SKColor c) => new(c.Red / 255f, c.Green / 255f, c.Blue / 255f);
        return new GpuFrame(
            view.RightF, view.UpF, view.CentreF,
            (float)view.ScalePxPerRad, (float)view.Flatness, (float)view.MaxAngle,
            new Vector2((float)view.Width, (float)view.Height), pixelRatio,
            GpuPalette.ModeOf(options.Layer), inset, options.Shaded && view.Flatness < 1,
            (float)view.MaxRadius, Rgb(MapRenderer.GapColor), Rgb(MapRenderer.BackgroundColor));
    }
}

/// <summary>
/// C# mirror of the GLSL vertex shader (<c>globe.vert</c>): the same single-precision steps, so tests can pin the
/// shader's math against <see cref="MapViewState.ProjectFast"/> and the CPU inset and shading.
/// </summary>
public static class GpuProjection
{
    /// <summary>
    /// Screen position (device-independent pixels, y down) of <paramref name="p"/>, clamped to the visible cap; returns
    /// false when it was clamped (as <see cref="MapViewState.ProjectClamped"/>).
    /// </summary>
    public static bool Project(in GpuFrame f, Vector3 p, out Vector2 screen)
    {
        var x = Vector3.Dot(p, f.Right);
        var y = Vector3.Dot(p, f.Up);
        var z = Vector3.Dot(p, f.Centre);
        var s = MathF.Sqrt((x * x) + (y * y));
        var theta = MathF.Atan2(s, z);
        var visible = theta <= f.MaxAngle;
        theta = MathF.Min(theta, f.MaxAngle);
        var r = f.ScalePxPerRad * (((1 - f.Flatness) * MathF.Sin(theta)) + (f.Flatness * theta));
        var dir = s > 1e-20f ? new Vector2(x / s, y / s) : new Vector2(z > 0 ? 0 : 1, 0);
        screen = new Vector2((f.Viewport.X / 2) + (r * dir.X), (f.Viewport.Y / 2) - (r * dir.Y));
        return visible && (s > 1e-20f || z > 0);
    }

    /// <summary>
    /// Vertex shader position of a cell vertex: <paramref name="p"/> projected, moved toward the projected cell centre so
    /// the cell's edge moves in by <see cref="GpuFrame.EdgeInsetPx"/> (scale clamped to 0.3–1 as the CPU inset);
    /// null when the cell centre is hidden (the whole cell is dropped, as the CPU renderer culls by centre).
    /// </summary>
    public static Vector2? CellVertex(in GpuFrame f, Vector3 p, Vector3 cellCentre)
    {
        if (!Project(f, cellCentre, out var c)) return null;
        Project(f, p, out var v);
        var d = v - c;
        var radius = d.Length();
        if (radius <= 0 || f.EdgeInsetPx <= 0) return v;
        var s = Math.Clamp(1f - (f.EdgeInsetPx / (0.866f * radius)), 0.3f, 1f);
        return c + (d * s);
    }

    /// <summary>The shade factor of a cell (as the CPU renderer's <c>ShadeFactor</c>, 33 levels).</summary>
    public static float Shade(in GpuFrame f, Vector3 cellCentre)
    {
        if (!f.Shaded) return 1f;
        var n = new Vector3(Vector3.Dot(cellCentre, f.Right), Vector3.Dot(cellCentre, f.Up), Vector3.Dot(cellCentre, f.Centre));
        var shade = 0.35f + (0.65f * MathF.Max(0, Vector3.Dot(n, GpuFrame.Light)));
        shade += (1 - shade) * f.Flatness;
        return MathF.Round(shade * 32) / 32;
    }
}
