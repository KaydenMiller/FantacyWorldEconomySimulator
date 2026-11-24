using System.ComponentModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Threading;
using SkiaSharp;
using WorldEcon.Gui.ViewModels;
using WorldEcon.Rendering;
using WorldEcon.Rendering.Gpu;

namespace WorldEcon.Gui.Views.Gpu;

/// <summary>
/// The GPU half of the map (design 2026-11-23-gpu-map-rendering §5): an OpenGL control under <see cref="MapView"/> that
/// draws the background, globe cells and rivers with <see cref="GlMapRenderer"/>. While it covers the current level it
/// sets <see cref="MapViewModel.GpuActive"/>, and <see cref="MapView"/> draws only the Skia overlay (markers, labels,
/// hover, selection, lines) on top; otherwise (no GL context, data not ready, a level not yet on the GPU) the map stays
/// on the CPU renderer. Input is handled by <see cref="MapView"/>, which sits above this control.
/// </summary>
public sealed class GlMapView : OpenGlControlBase
{
    private MainViewModel? _vm;
    private MapViewModel? _map;
    private GlMapRenderer? _gl;
    private GpuMapData? _pending;
    private MapViewModel? _uploadedFor;
    private string? _capturePath;
    private bool _failed;

    public GlMapView()
    {
        IsHitTestVisible = false; // MapView above handles the pointer
    }

    /// <summary>CPU time of the last GPU frame submission (uniforms and draw calls), ms.</summary>
    public static double LastSubmitMs { get; private set; }

    /// <summary>True when the GPU renderer is in use (a GL context, shaders compiled); false after a GL failure.</summary>
    public static bool Available { get; private set; }

    /// <summary>The reason the GPU path is off, for the status line and diagnostics; null when it is on.</summary>
    public static string? FailureReason { get; private set; }

    /// <summary>
    /// True when the GPU draws <paramref name="level"/>: every level (Globe View from the texel atlas; Region and Detail
    /// View from tile block meshes where the blocks are loaded, the atlas elsewhere).
    /// </summary>
    public static bool Covers(MapLevel level) => true;

    /// <summary>Writes the next GPU frame (the GL layer only, without the Skia overlay) to <paramref name="path"/> as PNG.</summary>
    public void CaptureNextFrame(string path)
    {
        _capturePath = path;
        RequestNextFrameRendering();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null) _vm.PropertyChanged -= OnMainChanged;
        _vm = DataContext as MainViewModel;
        if (_vm is not null) _vm.PropertyChanged += OnMainChanged;
        HookMap(_vm?.Map);
    }

    private void OnMainChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.Map)) HookMap(_vm?.Map);
    }

    private void HookMap(MapViewModel? map)
    {
        if (ReferenceEquals(map, _map)) return;
        if (_map is not null) _map.Invalidated -= OnInvalidated;
        _map = map;
        if (map is null) return;
        map.Invalidated += OnInvalidated;
        if (_failed) return;
        if (!map.GpuAllowed)
        {
            // Stage previews have no imagery: the CPU map control draws them (GpuActive stays false).
            _pending = null;
            map.GpuActive = false;
            RequestNextFrameRendering();
            return;
        }

        var renderer = map.Renderer;
        // Building the scene takes about 0.5 s at 40,962 cells: off the UI thread; uploaded on the next GL frame.
        _ = Task.Run(() =>
        {
            try
            {
                var data = GpuMapData.Prepare(renderer);
                Dispatcher.UIThread.Post(() =>
                {
                    if (!ReferenceEquals(_map, map)) return;
                    _pending = data;
                    RequestNextFrameRendering();
                });
            }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Post(() => Fail($"GPU scene build failed: {ex.Message}"));
            }
        });
    }

    private void OnInvalidated(object? sender, EventArgs e) => RequestNextFrameRendering();

    private void Fail(string reason)
    {
        _failed = true;
        Available = false;
        FailureReason = reason;
        if (_map is not null) _map.GpuActive = false;
        Console.Error.WriteLine($"GPU map renderer off: {reason}");
    }

    protected override void OnOpenGlInit(GlInterface gl)
    {
        base.OnOpenGlInit(gl);
        if (_failed) return;
        try
        {
            var dialect = GlVersion.Type == GlProfileType.OpenGLES ? GlslDialect.Es300 : GlslDialect.Core330;
            _gl = NewRenderer(gl, dialect);
            Available = true;
            FailureReason = null;
            Console.WriteLine($"GPU map renderer: {GlVersion.Type} {GlVersion.Major}.{GlVersion.Minor}, {gl.Renderer}, GLSL {dialect}");
        }
        catch (Exception ex)
        {
            Fail(ex.Message);
        }
    }

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        _gl?.Dispose();
        _gl = null;
        _uploadedFor = null;
        if (_map is not null) _map.GpuActive = false;
        base.OnOpenGlDeinit(gl);
    }

    protected override void OnOpenGlLost()
    {
        _gl = null;
        _uploadedFor = null;
        if (_map is not null) _map.GpuActive = false;
        base.OnOpenGlLost();
    }

    protected override void OnOpenGlRender(GlInterface gl, int fb)
    {
        if (_gl is null || _map is not { } map) return;
        var sw = Stopwatch.StartNew();
        try
        {
            if (_pending is { } data && ReferenceEquals(data.Renderer, map.Renderer))
            {
                if (_uploadedFor is not null)
                {
                    // Another world was uploaded before: start from fresh GL objects.
                    var dialect = _gl.Dialect;
                    _gl.Dispose();
                    _gl = NewRenderer(gl, dialect);
                }

                _gl.Upload(data);
                _uploadedFor = map;
                _pending = null;
                Console.WriteLine($"GPU map scene uploaded: {data.Scene.VertexCount:N0} vertices, {data.Scene.Indices.Length / 3:N0} triangles, {_gl.RiverPieces:N0} river pieces, atlas layers {data.Scene.AtlasLayers}");
            }

            var view = map.View;
            var level = map.Renderer.EffectiveLevel(view);
            var active = ReferenceEquals(_uploadedFor, map) && Covers(level);
            if (map.GpuActive != active)
            {
                map.GpuActive = active; // MapView switches between the full CPU picture and the overlay
            }

            if (!active) return;
            var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
            var pw = Math.Max(1, (int)Math.Round(Bounds.Width * scaling));
            var ph = Math.Max(1, (int)Math.Round(Bounds.Height * scaling));
            var options = map.DrawOptions;
            var frame = GpuFrame.For(view, options, level, (float)scaling);
            _gl.Render(fb, pw, ph, frame, options, view, level);
            LastSubmitMs = sw.Elapsed.TotalMilliseconds;

            if (_capturePath is { } path)
            {
                _capturePath = null;
                SavePng(path, _gl.ReadPixels(fb, pw, ph), pw, ph);
                Console.WriteLine($"Wrote GPU frame {path} ({pw}x{ph})");
            }
        }
        catch (Exception ex)
        {
            Fail(ex.Message);
        }
    }

    private GlMapRenderer NewRenderer(GlInterface gl, GlslDialect dialect)
    {
        var r = new GlMapRenderer(gl, dialect);
        // A tile block mesh finished on a worker: draw again to show it.
        r.TileBlockBuilt += () => Dispatcher.UIThread.Post(RequestNextFrameRendering, DispatcherPriority.Background);
        return r;
    }

    /// <summary>Tile block meshes drawn in the last GPU frame.</summary>
    public int LastTileBlocks => _gl?.LastTileBlocks ?? 0;

    /// <summary>Tile block meshes being built after the last GPU frame.</summary>
    public int PendingTileBlocks => _gl?.PendingTileBlocks ?? 0;

    private static void SavePng(string path, byte[] rgba, int width, int height)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        System.Runtime.InteropServices.Marshal.Copy(rgba, 0, bitmap.GetPixels(), rgba.Length);
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        using var file = File.Create(path);
        data.SaveTo(file);
    }
}
