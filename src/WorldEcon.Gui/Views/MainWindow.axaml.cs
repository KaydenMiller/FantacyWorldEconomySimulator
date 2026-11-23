using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using WorldEcon.Gui.ViewModels;

namespace WorldEcon.Gui.Views;

/// <summary>Main window: menu, toolbar, map, details, layers and areas tabs, status bar. Keys: Enter zooms to Region View on the selection, Esc zooms out to Globe View.</summary>
public partial class MainWindow : Window
{
    private static readonly FilePickerFileType DbFiles = new("World database") { Patterns = ["*.db", "*.sqlite"] };

    public MainWindow()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        // --renderer cpu: no GPU control, the map stays on the CPU renderer.
        if (Program.Options.Renderer == "cpu" && GlMap.Parent is Panel panel) panel.Children.Remove(GlMap);
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    /// <summary>
    /// Enter zooms to Region View, Esc zooms out to Globe View, at window level (tunnelling), except when the key comes from a text box,
    /// combo box, list box or menu, or while a popup (an open drop-down or menu) is shown: those controls use the keys.
    /// </summary>
    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (Vm is not { } vm || e.Key is not (Key.Enter or Key.Return or Key.Escape)) return;
        if (KeyBelongsToControl(e.Source) || AnyPopupOpen()) return;
        if (e.Key is Key.Enter or Key.Return)
        {
            if (vm.ZoomToRegion()) e.Handled = true;
        }
        else if (vm.BackToGlobe())
        {
            e.Handled = true;
        }
    }

    /// <summary>True when the key source is (inside) a control that handles Enter or Esc itself.</summary>
    internal static bool KeyBelongsToControl(object? source)
    {
        for (var v = source as Visual; v is not null; v = v.GetVisualParent())
            if (v is TextBox or ComboBox or ListBox or MenuItem or Menu or Avalonia.Controls.ContextMenu) return true;
        return false;
    }

    /// <summary>True while any popup of this window (combo box drop-down, menu, context menu, flyout) is open.</summary>
    private bool AnyPopupOpen()
    {
        foreach (var popup in this.GetVisualDescendants().OfType<Popup>())
            if (popup.IsOpen) return true;
        foreach (var combo in this.GetVisualDescendants().OfType<ComboBox>())
            if (combo.IsDropDownOpen) return true;
        foreach (var item in this.GetVisualDescendants().OfType<MenuItem>())
            if (item.IsSubMenuOpen) return true;
        return false;
    }

    /// <summary>Runs an async action from an event handler; any exception is reported in the window, never rethrown.</summary>
    private async void Run(string context, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Vm?.ReportUnexpected(context, ex);
        }
    }

    private void Run(string context, Func<Task<bool>> action) => Run(context, async () => { await action(); });

    private void OnOpen(object? sender, RoutedEventArgs e) => Run("Could not open a database", async () =>
    {
        if (Vm is not { CanUseFileActions: true } vm) return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open world database",
            AllowMultiple = false,
            FileTypeFilter = [DbFiles, FilePickerFileTypes.All],
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path) await vm.OpenAsync(path);
    });

    /// <summary>File → New world → preset (the menu item's Tag): choose a file, then generate with progress in the status bar.</summary>
    private void OnNewWorld(object? sender, RoutedEventArgs e) => Run("Could not create the world", async () =>
    {
        if (Vm is not { CanUseFileActions: true } vm || (sender as MenuItem)?.Tag is not string preset) return;
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = $"Create world database ({preset})",
            SuggestedFileName = $"{preset}.db",
            DefaultExtension = "db",
            FileTypeChoices = [DbFiles],
            ShowOverwritePrompt = true,
        });
        // The save dialog asked before returning an existing file, so an existing path is a confirmed overwrite.
        if (file?.TryGetLocalPath() is { } path) await vm.CreateDemoWorldAsync(path, WorldEcon.WorldGen.Pipeline.WorldGenPresets.Get(preset), overwrite: true);
    });

    private void OnExit(object? sender, RoutedEventArgs e) => Close();

    private void OnDismissWarnings(object? sender, RoutedEventArgs e) => Vm?.DismissWarnings();

    /// <summary>Renders the window into a PNG at its current size and scaling.</summary>
    public void SaveScreenshot(string path)
    {
        var scale = RenderScaling;
        var size = new PixelSize(Math.Max(1, (int)Math.Round(Bounds.Width * scale)), Math.Max(1, (int)Math.Round(Bounds.Height * scale)));
        using var bitmap = new RenderTargetBitmap(size, new Vector(96 * scale, 96 * scale));
        bitmap.Render(this);
        bitmap.Save(path, PngBitmapEncoderOptions.Default);
    }

    /// <summary>
    /// Diagnostics: completes when the GPU control draws the map (its scene uploaded and the level covered), or after
    /// <paramref name="timeout"/>, or at once with <c>--renderer cpu</c>; so scripted screenshots and timings measure the
    /// renderer in use rather than the CPU frames drawn while the GPU scene is still being built.
    /// </summary>
    public async Task WaitForGpuAsync(TimeSpan timeout)
    {
        if (GlMap.Parent is null || Vm?.Map is not { } map) return;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!map.GpuActive && clock.Elapsed < timeout && Gpu.GlMapView.FailureReason is null)
        {
            GlMap.RequestNextFrameRendering();
            await Task.Delay(50);
        }

        // Then until the visible tile blocks have meshes (no build pending over 5 consecutive checks).
        var settled = 0;
        while (map.GpuActive && settled < 5 && clock.Elapsed < timeout)
        {
            GlMap.RequestNextFrameRendering();
            await Task.Delay(50);
            settled = GlMap.PendingTileBlocks == 0 ? settled + 1 : 0;
        }

        Console.WriteLine(map.GpuActive
            ? $"GPU renderer active after {clock.Elapsed.TotalMilliseconds:F0} ms, {GlMap.LastTileBlocks} tile blocks drawn"
            : $"GPU renderer not active ({Gpu.GlMapView.FailureReason ?? "level not covered or not ready"}); CPU renderer in use");
    }

    /// <summary>Saves a screenshot after the next layout and render pass (diagnostic <c>--screenshot</c>).</summary>
    public void SaveScreenshotWhenDrawn(string path, Action? done = null)
    {
        RequestAnimationFrame(_ => Dispatcher.UIThread.Post(() =>
        {
            // The window capture does not include the OpenGL layer: the GPU frame is written next to it.
            if (Vm?.Map is { GpuActive: true } && GlMap.Parent is not null) GlMap.CaptureNextFrame(Path.ChangeExtension(path, ".gpu.png"));
            SaveScreenshot(path);
            Console.WriteLine($"Wrote screenshot {path} ({Bounds.Width}x{Bounds.Height} at scaling {RenderScaling})");
            if (Vm?.Session?.Renderer is { } r)
                Console.WriteLine(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"Last map draw: {r.LastLevel} View, {r.LastDrawnCells} cells, {r.LastVisibleGroups} groups, {r.LastTileBlocksDrawn} tile blocks; CPU build {r.LastBuildMs:F2} ms, cells {r.LastCellsMs:F2} ms, whole draw {r.LastDrawMs:F2} ms"));
            done?.Invoke();
        }, DispatcherPriority.Background));
    }

    /// <summary>
    /// Diagnostic (<c>--spin</c>): pans the map by 4 px of drag on each of <paramref name="frames"/> animation frames
    /// and prints the median and worst frame interval and the renderer's CPU (central processing unit) build and whole-draw times.
    /// </summary>
    public void Spin(int frames, Action? done = null)
    {
        if (Vm?.Map is not { } map || Vm.Session is not { } session) return;
        var intervals = new List<double>();
        var builds = new List<double>();
        var draws = new List<double>();
        var submits = new List<double>();
        var overlays = new List<double>();
        var gpuFrames = 0;
        int gc0 = GC.CollectionCount(0), gc1 = GC.CollectionCount(1), gc2 = GC.CollectionCount(2);
        var gcPause0 = GC.GetTotalPauseDuration();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var last = -1.0;
        void Step(TimeSpan _)
        {
            var now = clock.Elapsed.TotalMilliseconds;
            if (last >= 0)
            {
                intervals.Add(now - last);
                builds.Add(session.Renderer.LastBuildMs);
                draws.Add(session.Renderer.LastDrawMs);
                if (map.GpuActive)
                {
                    gpuFrames++;
                    submits.Add(Gpu.GlMapView.LastSubmitMs);
                    overlays.Add(session.Renderer.LastOverlayMs);
                }
            }

            last = now;
            if (intervals.Count >= frames)
            {
                static string Stats(List<double> v)
                {
                    v.Sort();
                    return string.Create(System.Globalization.CultureInfo.InvariantCulture,
                        $"median {v[v.Count / 2]:F2} ms, p95 {v[(int)(v.Count * 0.95)]:F2} ms, max {v[^1]:F2} ms");
                }

                var worst = builds.IndexOf(builds.Max());
                var gcMs = (GC.GetTotalPauseDuration() - gcPause0).TotalMilliseconds;
                Console.WriteLine($"Spin {frames} frames at {map.LevelName} View, {session.Renderer.LastDrawnCells} cells drawn: frame interval {Stats(intervals)}");
                Console.WriteLine($"  CPU build {Stats(builds)}; whole draw {Stats(draws)}");
                if (gpuFrames > 0)
                    Console.WriteLine($"  GPU renderer on {gpuFrames}/{intervals.Count} frames: GL submit {Stats(submits)}; Skia overlay {Stats(overlays)}");
                Console.WriteLine(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"  worst build at frame {worst}; garbage collections gen0/1/2 {GC.CollectionCount(0) - gc0}/{GC.CollectionCount(1) - gc1}/{GC.CollectionCount(2) - gc2}, total pause {gcMs:F1} ms"));
                done?.Invoke();
                return;
            }

            map.Pan(4, 0);
            RequestAnimationFrame(Step);
        }

        RequestAnimationFrame(Step);
    }

    /// <summary>
    /// Diagnostic (<c>--sweep</c>): wheel-zooms at the map centre from the current scale to Detail View over the first half
    /// of <paramref name="frames"/> animation frames and back over the second half (equal zoom factor per frame), then
    /// prints frame interval and whole-draw times overall and per level. Tile blocks load as they would for a user.
    /// </summary>
    public void Sweep(int frames, Action? done = null)
    {
        if (Vm?.Map is not { } map || Vm.Session is not { } session) return;
        var startScale = map.View.ScalePxPerRad;
        map.ZoomToLevel(WorldEcon.Rendering.MapLevel.Detail);
        var detailScale = map.View.ScalePxPerRad;
        map.ZoomOutToGlobe();
        var half = frames / 2;
        var notchesPerFrame = Math.Log(detailScale / startScale) / Math.Log(MapViewModel.WheelStep) / half;
        double cx = map.View.Width / 2, cy = map.View.Height / 2;
        var byLevel = new SortedDictionary<string, (List<double> Intervals, List<double> Draws)>();
        var all = (Intervals: new List<double>(), Draws: new List<double>());
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var last = -1.0;
        var step = 0;
        var worst = (Ms: 0.0, Step: -1, Build: 0.0, Cells: 0.0, Level: "");
        void Step(TimeSpan _)
        {
            var now = clock.Elapsed.TotalMilliseconds;
            if (last >= 0)
            {
                // The level and CPU time of the renderer in use: GPU submit + Skia overlay, or the CPU renderer's whole draw.
                var level = map.LevelName;
                var r = session.Renderer;
                var cpuMs = map.GpuActive ? Gpu.GlMapView.LastSubmitMs + r.LastOverlayMs : r.LastDrawMs;
                if (!byLevel.TryGetValue(level, out var bucket)) byLevel[level] = bucket = ([], []);
                bucket.Intervals.Add(now - last);
                bucket.Draws.Add(cpuMs);
                all.Intervals.Add(now - last);
                all.Draws.Add(cpuMs);
                if (cpuMs > worst.Ms) worst = (cpuMs, step, map.GpuActive ? Gpu.GlMapView.LastSubmitMs : r.LastBuildMs, map.GpuActive ? r.LastOverlayMs : r.LastCellsMs, level);
            }

            last = now;
            if (step >= frames)
            {
                static string Stats(List<double> v)
                {
                    v.Sort();
                    var over = v.Count(x => x > 1000.0 / 60);
                    return string.Create(System.Globalization.CultureInfo.InvariantCulture,
                        $"median {v[v.Count / 2]:F2} ms, p95 {v[(int)(v.Count * 0.95)]:F2} ms, max {v[^1]:F2} ms, {over}/{v.Count} over 16.7 ms");
                }

                Console.WriteLine($"Sweep {frames} frames Globe -> Detail -> Globe ({(map.GpuActive ? "GPU" : "CPU")} renderer): frame interval {Stats(all.Intervals)}; CPU per frame {Stats(all.Draws)}");
                foreach (var (level, b) in byLevel)
                    Console.WriteLine($"  {level}: frame interval {Stats(b.Intervals)}; CPU per frame {Stats(b.Draws)}");
                Console.WriteLine(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"  worst CPU frame {worst.Ms:F1} ms at step {worst.Step} ({worst.Level}): GPU submit or CPU build {worst.Build:F1} ms, overlay or cells {worst.Cells:F1} ms"));
                done?.Invoke();
                return;
            }

            map.ZoomBy(step < half ? notchesPerFrame : -notchesPerFrame, cx, cy);
            step++;
            RequestAnimationFrame(Step);
        }

        RequestAnimationFrame(Step);
    }
}
