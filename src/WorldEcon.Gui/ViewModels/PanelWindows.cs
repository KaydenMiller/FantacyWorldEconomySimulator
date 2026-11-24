using System.ComponentModel;

namespace WorldEcon.Gui.ViewModels;

/// <summary>
/// Shows panel view models in their own top-level windows. Desktop only: on single-window platforms (the browser) there
/// is no host and pop-outs are not offered. The panel is any view model the window has a template for, so other
/// panels (caravan or node details) can use the same host later.
/// </summary>
public interface IPanelWindowHost
{
    /// <summary>Opens a window showing <paramref name="panel"/>; <paramref name="onClosed"/> runs once when it closes (by the user or <see cref="Close"/>).</summary>
    void Show(object panel, string title, Action onClosed);

    /// <summary>Brings the panel's window to the front.</summary>
    void Activate(object panel);

    /// <summary>Closes the panel's window.</summary>
    void Close(object panel);

    void SetTitle(object panel, string title);

    /// <summary>Brings the main window to the front (after a pop-out selected something on its map).</summary>
    void ActivateMain();
}

/// <summary>
/// The open pop-out city windows: one per city (opening a city again brings its window forward), each pinned to its
/// city (its own search re-pins it), titled "City — World". <see cref="CloseAll"/> closes them before their world's
/// session is disposed. Framework-free.
/// </summary>
public sealed class PanelWindows(IPanelWindowHost? host, Func<WorldSession, Guid, CityPanelViewModel>? newCity = null)
{
    private readonly List<CityPanelViewModel> _open = [];

    /// <summary>Makes the city panel of a new window; set by the main view model so row clicks act on its map.</summary>
    public Func<WorldSession, Guid, CityPanelViewModel>? CityFactory { get; set; } = newCity;

    /// <summary>True when separate windows exist (a desktop host).</summary>
    public bool Supported => host is not null;

    public int OpenCount => _open.Count;

    /// <summary>Brings the main window to the front (no-op without a host).</summary>
    public void ActivateMain() => host?.ActivateMain();

    /// <summary>
    /// Opens a window for <paramref name="settlementId"/>, or brings forward the window already showing it. Completes
    /// when the city is loaded. Throws without a host.
    /// </summary>
    public async Task<CityPanelViewModel> OpenCityAsync(WorldSession session, Guid settlementId)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (host is null) throw new InvalidOperationException("Pop-out windows are not supported here.");
        if (_open.FirstOrDefault(p => p.SettlementId == settlementId) is { } existing)
        {
            host.Activate(existing);
            return existing;
        }

        var panel = CityFactory?.Invoke(session, settlementId) ?? new CityPanelViewModel(session, _ => { }, _ => { });
        panel.CanPopOut = false; // already in its own window
        _open.Add(panel);
        var world = session.Data.World.Name;
        var name = session.Data.Settlements.FirstOrDefault(s => s.Id == settlementId)?.Name ?? "City";
        PropertyChangedEventHandler retitle = (_, e) =>
        {
            if (e.PropertyName == nameof(CityPanelViewModel.HeaderText) && _open.Contains(panel) && panel.Details is { } d)
                host.SetTitle(panel, $"{d.Header.Name} — {world}");
        };
        panel.PropertyChanged += retitle;
        host.Show(panel, $"{name} — {world}", () =>
        {
            panel.PropertyChanged -= retitle;
            panel.Detach();
            _open.Remove(panel);
        });
        await panel.ShowAsync(settlementId);
        return panel;
    }

    /// <summary>Closes every pop-out window (their panels stop following the world).</summary>
    public void CloseAll()
    {
        foreach (var panel in _open.ToList()) host?.Close(panel);
        _open.Clear();
    }
}
