using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using WorldEcon.Gui.ViewModels;
using WorldEcon.Rendering;

namespace WorldEcon.Gui.Views;

/// <summary>
/// The map (Globe, Region and Detail View): draws <see cref="MapViewModel"/> through <see cref="MapRenderer"/> on the
/// Skia lease and forwards input to <see cref="MainViewModel"/> (its data context). Drag pans, the wheel zooms at the
/// pointer, a click selects, a double-click zooms to Region View. Redraws when the view-model reports a change and when
/// the renderer reports loaded tile blocks (raised on a worker thread, posted to the user interface thread).
/// </summary>
/// <remarks>
/// High-DPI (high dots per inch) displays: the view state is in device-independent pixels, so levels, gaps and label sizes look the same at any
/// scaling. At 1× the canvas carries only the control's integer offset and the renderer uses its frame bitmap fill; at
/// 2× the canvas is scaled and the renderer draws cells with <c>DrawVertices</c> (see <see cref="MapRenderer.UseVertexFill"/>).
/// </remarks>
public sealed class MapView : Control
{
    private MainViewModel? _vm;
    private MapViewModel? _map;
    private MapRenderer? _renderer;
    private DragTracker _drag;
    private bool _doubleClicked;
    private int _redrawPosted;

    public MapView()
    {
        Focusable = true;
        ClipToBounds = true;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null) _vm.PropertyChanged -= OnMainChanged;
        _vm = DataContext as MainViewModel;
        if (_vm is not null) _vm.PropertyChanged += OnMainChanged;
        HookMap(_vm?.Map);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        HookMap(_vm?.Map); // re-attached (for example moved into a tab or dock): redraw on view-model and tile events again
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        HookMap(null);
    }

    private void OnMainChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.Map)) HookMap(_vm?.Map);
    }

    private void HookMap(MapViewModel? map)
    {
        if (_map is not null) _map.Invalidated -= OnInvalidated;
        if (_renderer is not null) _renderer.TilesLoaded -= OnTilesLoaded;
        _map = map;
        _renderer = map?.Renderer;
        _drag.Release();
        if (_map is not null)
        {
            _map.Invalidated += OnInvalidated;
            _renderer!.TilesLoaded += OnTilesLoaded;
            if (Bounds.Width > 0) _map.SetViewport(Bounds.Width, Bounds.Height);
        }

        InvalidateVisual();
    }

    private void OnInvalidated(object? sender, EventArgs e) => InvalidateVisual();

    /// <summary>Worker thread: one redraw is posted for any number of blocks that finish before it runs.</summary>
    private void OnTilesLoaded(object? sender, int cell)
    {
        if (Interlocked.Exchange(ref _redrawPosted, 1) == 1) return;
        Dispatcher.UIThread.Post(() =>
        {
            Volatile.Write(ref _redrawPosted, 0);
            InvalidateVisual();
            _vm?.RefreshTileStatus(); // failed or missing blocks are named in the status bar
        }, DispatcherPriority.Background);
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        _map?.SetViewport(e.NewSize.Width, e.NewSize.Height);
    }

    public override void Render(DrawingContext context)
    {
        // The viewport is set in OnSizeChanged and HookMap, not here: changing view-model state during a render pass
        // would raise Invalidated (and InvalidateVisual) from inside the pass.
        if (_map is null || _renderer is null) return;
        var view = _map.View;
        var options = _map.DrawOptions;
        var renderer = _renderer;
        context.Custom(new SkiaDrawOperation(new Rect(Bounds.Size), canvas =>
        {
            try
            {
                renderer.Draw(canvas, view, options);
            }
            catch (ObjectDisposedException)
            {
                // The world was replaced between this frame's capture and its render; the next frame draws the new one.
            }
        }));
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (_vm is null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var p = e.GetPosition(this);
        Focus();
        _doubleClicked = e.ClickCount >= 2;
        if (_doubleClicked)
        {
            _vm.Activate(p.X, p.Y);
            e.Handled = true;
            return;
        }

        _drag.Press(p);
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_map is null) return;
        var p = e.GetPosition(this);
        if (_drag.Move(p) is { } delta) _map.Pan(delta.X, delta.Y);
        else if (!_drag.Pressed) _map.HoverAt(p.X, p.Y);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_vm is null) return;
        if (_drag.Pressed && !_drag.Dragging && !_doubleClicked)
        {
            var p = e.GetPosition(this);
            _vm.Click(p.X, p.Y);
        }

        _drag.Release();
        _doubleClicked = false;
        e.Pointer.Capture(null);
    }

    /// <summary>Capture lost (another window, a popup, or the system took the pointer): end any drag without a click.</summary>
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _drag.Release();
        _doubleClicked = false;
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _map?.ClearHover();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var p = e.GetPosition(this);
        _map?.ZoomBy(e.Delta.Y, p.X, p.Y);
        e.Handled = true;
    }
}
