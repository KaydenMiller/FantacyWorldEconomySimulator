namespace WorldEcon.Tui.Navigation;

/// <summary>
/// k9s-style navigation over the world. Roots are global flat lists you jump to with ':' (with
/// autocomplete); drilling a row descends into its children in the main view; details give a quick
/// per-row lookup. UI-agnostic — the shell renders the <see cref="NavView"/>s and manages the stack.
/// </summary>
public interface INavigator
{
    /// <summary>Canonical root names, for ':' autocomplete (e.g. cities, regions, shops, claims).</summary>
    IReadOnlyList<string> RootNames { get; }

    /// <summary>Resolve a typed ':' token (name or alias) to a canonical root name.</summary>
    bool TryResolveRoot(string token, out string canonical);

    /// <summary>Build a root (global) view for a canonical root name.</summary>
    Task<NavView> RootAsync(string canonicalRootName, TuiContext ctx);

    /// <summary>Drill into a row's children, or null if the row is a leaf.</summary>
    Task<NavView?> DrillAsync(NavRow row, TuiContext ctx);

    /// <summary>Quick "Field: value" lookup lines for a row.</summary>
    Task<IReadOnlyList<string>> DetailsAsync(NavRow row, TuiContext ctx);
}
