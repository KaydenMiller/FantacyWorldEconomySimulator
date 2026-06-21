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
    Task ShowMessageAsync(string title, IReadOnlyList<string> lines);
    Task<bool> ConfirmAsync(string title, string message);
}
