namespace WorldEcon.Tui;

/// <summary>
/// A browsable, UI-agnostic view over one kind of entity. The shell renders the
/// <see cref="ResourceTable"/> as a table and the <see cref="DetailsAsync"/> lines as a detail pane.
/// </summary>
public interface IResource
{
    /// <summary>Canonical name, e.g. "cities".</summary>
    string Name { get; }

    /// <summary>Alternate tokens that resolve to this resource, e.g. ["city","settlements"].</summary>
    IReadOnlyList<string> Aliases { get; }

    /// <summary>Loads all rows for this resource, scoped to the context's world, in a stable order.</summary>
    Task<ResourceTable> LoadAsync(TuiContext ctx);

    /// <summary>"Field: value" lines describing the row identified by <paramref name="key"/>.</summary>
    Task<IReadOnlyList<string>> DetailsAsync(string key, TuiContext ctx);
}
