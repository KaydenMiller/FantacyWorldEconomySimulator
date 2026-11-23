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

    /// <summary>Renders the window into a PNG at its current size and scaling.</summary>
    public void SaveScreenshot(string path)
    {
        var scale = RenderScaling;
        var size = new PixelSize(Math.Max(1, (int)Math.Round(Bounds.Width * scale)), Math.Max(1, (int)Math.Round(Bounds.Height * scale)));
        using var bitmap = new RenderTargetBitmap(size, new Vector(96 * scale, 96 * scale));
        bitmap.Render(this);
        bitmap.Save(path, PngBitmapEncoderOptions.Default);
    }

    /// <summary>Saves a screenshot after the next layout and render pass (diagnostic <c>--screenshot</c>).</summary>
    public void SaveScreenshotWhenDrawn(string path, Action? done = null)
    {
        RequestAnimationFrame(_ => Dispatcher.UIThread.Post(() =>
        {
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

                Console.WriteLine($"Spin {frames} frames at {map.LevelName} View, {session.Renderer.LastDrawnCells} cells drawn: frame interval {Stats(intervals)}");
                Console.WriteLine($"  CPU build {Stats(builds)}; whole draw {Stats(draws)}");
                done?.Invoke();
                return;
            }

            map.Pan(4, 0);
            RequestAnimationFrame(Step);
        }

        RequestAnimationFrame(Step);
    }
}
