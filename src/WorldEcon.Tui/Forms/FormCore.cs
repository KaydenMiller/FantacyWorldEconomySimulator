namespace WorldEcon.Tui.Forms;

/// <summary>Result of running an entity form.</summary>
public sealed record FormOutcome(bool Created, string Message)
{
    public static FormOutcome Cancelled { get; } = new(false, "Cancelled.");
    public static FormOutcome Fail(string message) => new(false, message);
    public static FormOutcome Ok(string message) => new(true, message);
}

/// <summary>
/// A guided create-form for one entity type. The shell's <c>n</c> (new) command lists every registered
/// form's <see cref="Label"/>, then runs the chosen one. Forms gather fields one at a time through
/// <see cref="IUserInteraction"/> (the in-shell prompt bar), validate via the domain's ErrorOr
/// factories, and persist with a single SaveChanges — so they are fully unit-testable with a fake UI.
/// </summary>
public interface IEntityForm
{
    /// <summary>Name shown in the create chooser, e.g. "Good", "Settlement".</summary>
    string Label { get; }

    /// <summary>Canonical resource root to navigate to after a successful create (e.g. "goods"), or
    /// null to stay on the current view (for entities with no dedicated root).</summary>
    string? ResourceName { get; }

    Task<FormOutcome> RunAsync(TuiContext ctx, IUserInteraction ui);
}

/// <summary>Shared field-prompt helpers used by the entity forms. Every helper returns null when the
/// user cancels (Esc), and the calling form aborts with <see cref="FormOutcome.Cancelled"/>.</summary>
internal static class FormPrompts
{
    /// <summary>Prompts for a non-blank string, re-asking on blank until a value or cancel.</summary>
    public static async Task<string?> RequiredTextAsync(IUserInteraction ui, string title, string label)
    {
        while (true)
        {
            var t = await ui.AskTextAsync(title, label);
            if (t is null)
                return null; // cancelled
            t = t.Trim();
            if (t.Length > 0)
                return t;
            await ui.ShowMessageAsync(title, ["A value is required (Esc to cancel)."]);
        }
    }

    /// <summary>Prompts for one value of an enum by name. Null on cancel.</summary>
    public static async Task<TEnum?> EnumAsync<TEnum>(IUserInteraction ui, string title, string label)
        where TEnum : struct, Enum
    {
        var names = Enum.GetNames<TEnum>();
        var idx = await ui.AskChoiceAsync(title, label, names);
        return idx is null ? null : Enum.Parse<TEnum>(names[idx.Value]);
    }

    /// <summary>Yes/No choice. Null on cancel.</summary>
    public static async Task<bool?> BoolAsync(IUserInteraction ui, string title, string label)
    {
        var idx = await ui.AskChoiceAsync(title, label, ["No", "Yes"]);
        return idx is null ? null : idx.Value == 1;
    }

    /// <summary>Picks an existing entity from <paramref name="options"/> (name → id). Null on cancel.
    /// Callers handle the empty-options case (e.g. "create a region first") before calling.</summary>
    public static async Task<Guid?> RefAsync(
        IUserInteraction ui, string title, string label, IReadOnlyList<(string Name, Guid Id)> options)
    {
        var idx = await ui.AskChoiceAsync(title, label, options.Select(o => o.Name).ToList());
        return idx is null ? null : options[idx.Value].Id;
    }

    /// <summary>Prompts for a whole number, optionally pre-filled with a default. Null on cancel.</summary>
    public static Task<long?> NumberAsync(IUserInteraction ui, string title, string label, long? initial = null)
        => ui.AskNumberAsync(title, label, initial);
}
