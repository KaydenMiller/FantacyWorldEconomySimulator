using System.ComponentModel;
using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using WorldEcon.Gui.ViewModels;

namespace WorldEcon.Gui.Views;

/// <summary>
/// The time bar (see <see cref="TimeControlsViewModel"/>). While Play is on, a 100 ms timer feeds the real time elapsed
/// since its last tick to <see cref="TimeControlsViewModel.Tick"/>. Errors of the buttons go to the window's error text.
/// </summary>
public partial class TimeBar : UserControl
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly Stopwatch _elapsed = new();
    private TimeControlsViewModel? _vm;

    public TimeBar()
    {
        InitializeComponent();
        _timer.Tick += OnTimer;
        DataContextChanged += (_, _) => Follow(DataContext as TimeControlsViewModel);
        DetachedFromVisualTree += (_, _) => Follow(null);
    }

    /// <summary>The main window's view model (for error reports); set by the window.</summary>
    internal MainViewModel? Main { get; set; }

    private void Follow(TimeControlsViewModel? vm)
    {
        if (_vm is not null) _vm.PropertyChanged -= OnVmChanged;
        _vm = vm;
        if (_vm is not null) _vm.PropertyChanged += OnVmChanged;
        UpdateTimer();
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TimeControlsViewModel.IsPlaying)) UpdateTimer();
    }

    private void UpdateTimer()
    {
        if (_vm is { IsPlaying: true })
        {
            if (_timer.IsEnabled) return;
            _elapsed.Restart();
            _timer.Start();
        }
        else
        {
            _timer.Stop();
            _elapsed.Reset();
        }
    }

    private void OnTimer(object? sender, EventArgs e)
    {
        var elapsed = _elapsed.Elapsed;
        _elapsed.Restart();
        _vm?.Tick(elapsed);
    }

    private async void Run(string context, Func<TimeControlsViewModel, Task> action)
    {
        if (_vm is not { } vm) return;
        try
        {
            await action(vm);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Main?.ReportUnexpected(context, ex);
        }
    }

    private void OnPlay(object? sender, RoutedEventArgs e) => Run("Could not play", vm => vm.TogglePlayAsync());

    private void OnJump(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is string unit) Run("Could not advance time", vm => vm.JumpAsync(unit));
    }

    private void OnGo(object? sender, RoutedEventArgs e) => Run("Could not advance time", vm => vm.GoAsync());

    private void OnStop(object? sender, RoutedEventArgs e) => _vm?.Stop();
}
