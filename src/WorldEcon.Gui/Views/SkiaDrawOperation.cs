using Avalonia;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using SkiaSharp;

namespace WorldEcon.Gui.Views;

/// <summary>
/// Draws through Avalonia's SkiaSharp lease (the spike's technique). <paramref name="draw"/> runs on the render thread
/// with the canvas clipped to the control's bounds; it must only read state captured when the operation was created.
/// </summary>
internal sealed class SkiaDrawOperation(Rect bounds, Action<SKCanvas> draw) : ICustomDrawOperation
{
    public Rect Bounds => bounds;

    public bool HitTest(Point p) => bounds.Contains(p);

    public bool Equals(ICustomDrawOperation? other) => false;

    public void Dispose()
    {
    }

    public void Render(ImmediateDrawingContext context)
    {
        var lease = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
        if (lease is null) return; // not a Skia backend; nothing to draw with
        using var api = lease.Lease();
        var canvas = api.SkCanvas;
        canvas.Save();
        canvas.ClipRect(new SKRect(0, 0, (float)bounds.Width, (float)bounds.Height));
        draw(canvas);
        canvas.Restore();
    }
}

/// <summary>Pointer bookkeeping for the map control: press point, drag threshold, last position.</summary>
internal struct DragTracker
{
    private const double Threshold = 3;
    private Point _start;

    public bool Pressed { get; private set; }

    public bool Dragging { get; private set; }

    public Point Last { get; private set; }

    public void Press(Point p)
    {
        Pressed = true;
        Dragging = false;
        _start = p;
        Last = p;
    }

    /// <summary>Records a move; returns the delta since the last move once the pointer is past the drag threshold.</summary>
    public Vector? Move(Point p)
    {
        if (!Pressed) return null;
        if (!Dragging && Math.Abs(p.X - _start.X) + Math.Abs(p.Y - _start.Y) > Threshold) Dragging = true;
        var delta = p - Last;
        Last = p;
        return Dragging ? delta : null;
    }

    /// <summary>Ends the press (button released, or pointer capture lost to another control or window).</summary>
    public void Release()
    {
        Pressed = false;
        Dragging = false;
    }
}
