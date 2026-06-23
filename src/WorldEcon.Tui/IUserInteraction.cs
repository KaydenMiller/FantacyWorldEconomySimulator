namespace WorldEcon.Tui;

/// <summary>
/// The prompt abstraction actions use to gather input and report results. The Terminal.Gui shell
/// implements this with dialogs; tests use a scripted fake. A null return means the user cancelled,
/// and the calling action must no-op.
/// </summary>
public interface IUserInteraction
{
    Task<string?> AskTextAsync(string title, string prompt, string? initial = null);
    Task<long?> AskNumberAsync(string title, string prompt, long? initial = null);

    /// <summary>Asks the user to pick one of <paramref name="options"/>. Returns the chosen 0-based
    /// index, or null if cancelled. Used by forms for enums and entity references.</summary>
    Task<int?> AskChoiceAsync(string title, string prompt, IReadOnlyList<string> options);

    Task ShowMessageAsync(string title, IReadOnlyList<string> lines);
    Task<bool> ConfirmAsync(string title, string message);
}
