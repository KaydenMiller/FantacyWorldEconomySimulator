using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using WorldEcon.Gui.ViewModels;
using WorldEcon.Gui.Views;

namespace WorldEcon.Gui;

/// <summary>The Avalonia application: Fluent theme (dark), one main window.</summary>
public sealed class App : Avalonia.Application
{
    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Dark;
        Styles.Add(new FluentTheme());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var options = Program.Options;
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm };
            desktop.MainWindow = window;
            if (options.ExitAfterSeconds is { } seconds)
                DispatcherTimer.RunOnce(() => desktop.Shutdown(), TimeSpan.FromSeconds(seconds));
            if (options.DbPath is { } db)
                window.Opened += async (_, _) =>
                {
                    // async void handler: nothing may escape it, or the process ends.
                    try
                    {
                        var ok = await vm.OpenAsync(db);
                        if (ok && vm.Map is { } map)
                        {
                            if (options.Layer is { } layer) map.LayerIndex = (int)layer;
                            if (options.Centre is { } c) map.CentreOn(UnitVector(c.LatDeg, c.LonDeg));
                            if (options.ZoomTo is { } level) map.ZoomToLevel(level);
                            // Detail View draws region cells until the tile blocks arrive; a scripted screenshot waits for them.
                            await map.LoadVisibleTilesAsync();
                        }

                        Console.WriteLine(ok ? $"Loaded {db}: {vm.StatusText}" : vm.StatusText);
                        if (options.ScreenshotPath is { } png) window.SaveScreenshotWhenDrawn(png);
                        if (ok && options.SpinFrames is { } frames) window.Spin(frames);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        vm.ReportUnexpected($"Could not open {db}", ex);
                        Console.Error.WriteLine(vm.StatusText);
                    }
                };
            else if (options.ScreenshotPath is { } png)
                window.Opened += (_, _) => window.SaveScreenshotWhenDrawn(png);
        }

        base.OnFrameworkInitializationCompleted();
    }

    // Same convention as GlobeGeometry.LatLon: latitude from Y, longitude atan2(X, Z).
    private static WorldEcon.WorldGen.Grid.Vector3d UnitVector(double latDeg, double lonDeg)
    {
        double lat = latDeg * Math.PI / 180, lon = lonDeg * Math.PI / 180;
        return new WorldEcon.WorldGen.Grid.Vector3d(Math.Cos(lat) * Math.Sin(lon), Math.Sin(lat), Math.Cos(lat) * Math.Cos(lon));
    }
}
