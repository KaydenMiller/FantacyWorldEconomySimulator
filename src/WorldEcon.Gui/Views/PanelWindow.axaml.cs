using Avalonia.Controls;
using WorldEcon.Gui.ViewModels;

namespace WorldEcon.Gui.Views;

/// <summary>A top-level window showing one panel view model (a pop-out city panel).</summary>
public partial class PanelWindow : Window
{
    public PanelWindow() => InitializeComponent();
}

/// <summary>
/// The desktop <see cref="IPanelWindowHost"/>: one <see cref="PanelWindow"/> per panel, 640 × 720 px, resizable.
/// Created by the main window only under a classic desktop lifetime.
/// </summary>
public sealed class PanelWindowHost(Window main) : IPanelWindowHost
{
    private readonly Dictionary<object, PanelWindow> _windows = [];

    /// <summary>Open windows (tests).</summary>
    internal IReadOnlyCollection<PanelWindow> Windows => _windows.Values;

    public void Show(object panel, string title, Action onClosed)
    {
        var window = new PanelWindow { DataContext = panel, Title = title };
        window.Closed += (_, _) =>
        {
            _windows.Remove(panel);
            onClosed();
        };
        _windows[panel] = window;
        window.Show();
    }

    public void Activate(object panel)
    {
        if (_windows.TryGetValue(panel, out var w)) w.Activate();
    }

    public void Close(object panel)
    {
        if (_windows.TryGetValue(panel, out var w)) w.Close();
    }

    public void SetTitle(object panel, string title)
    {
        if (_windows.TryGetValue(panel, out var w)) w.Title = title;
    }

    public void ActivateMain() => main.Activate();
}
