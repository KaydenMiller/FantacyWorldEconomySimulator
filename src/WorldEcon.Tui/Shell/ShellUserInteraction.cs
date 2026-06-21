using Terminal.Gui.App;
using Terminal.Gui.Views;

namespace WorldEcon.Tui.Shell;

/// <summary>
/// <see cref="IUserInteraction"/> for the running Terminal.Gui app. Text/number input uses the shell's
/// in-shell prompt bar (<see cref="TuiShell.PromptAsync"/>) — modal <c>Application.Run</c> dialogs do
/// not receive keystrokes in this TG v2 build. Messages and confirmations use <see cref="MessageBox"/>,
/// which works, marshalled onto the UI thread (these methods are called from background command tasks).
/// </summary>
internal sealed class ShellUserInteraction : IUserInteraction
{
    private readonly TuiShell _shell;
    private readonly IApplication _app;

    public ShellUserInteraction(TuiShell shell, IApplication app)
    {
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));
        _app = app ?? throw new ArgumentNullException(nameof(app));
    }

    public Task<string?> AskTextAsync(string title, string prompt, string? initial = null)
        => _shell.PromptAsync(prompt, initial);

    public async Task<long?> AskNumberAsync(string title, string prompt, long? initial = null)
    {
        while (true)
        {
            var text = await _shell.PromptAsync(prompt, initial?.ToString());
            if (text is null)
                return null; // cancelled

            if (long.TryParse(text.Trim(), out var value))
                return value;

            await ShowMessageAsync(title, [$"'{text}' is not a valid whole number."]);
            // loop and re-prompt
        }
    }

    public Task ShowMessageAsync(string title, IReadOnlyList<string> lines)
    {
        var message = lines.Count == 0 ? string.Empty : string.Join(Environment.NewLine, lines);
        InvokeOnUi(() => MessageBox.Query(_app, title, message, "OK"));
        return Task.CompletedTask;
    }

    public Task<bool> ConfirmAsync(string title, string message)
    {
        var choice = InvokeOnUi(() => MessageBox.Query(_app, title, message, "Yes", "No"));
        return Task.FromResult(choice == 0);
    }

    /// <summary>Runs <paramref name="f"/> on the UI thread and blocks the caller until it completes.</summary>
    private T InvokeOnUi<T>(Func<T> f)
    {
        T result = default!;
        Exception? error = null;
        using var done = new ManualResetEventSlim(false);
        _app.Invoke(() =>
        {
            try { result = f(); }
            catch (Exception ex) { error = ex; }
            finally { done.Set(); }
        });
        done.Wait();
        if (error is not null)
            throw error;
        return result;
    }
}
