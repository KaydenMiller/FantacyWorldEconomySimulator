using Terminal.Gui.App;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace WorldEcon.Tui.Shell;

/// <summary>
/// Implements <see cref="IUserInteraction"/> with Terminal.Gui modal dialogs. All four methods are
/// invoked synchronously from the shell's key handlers, which already run on the UI thread, so each
/// drives a nested modal loop and returns an already-completed task. A null/false result means the
/// user cancelled; the calling action then no-ops.
/// </summary>
internal sealed class DialogUserInteraction : IUserInteraction
{
    private readonly IApplication _app;

    public DialogUserInteraction(IApplication app)
        => _app = app ?? throw new ArgumentNullException(nameof(app));

    public Task<string?> AskTextAsync(string title, string prompt, string? initial = null)
        => Task.FromResult(PromptForText(title, prompt, initial));

    public Task<long?> AskNumberAsync(string title, string prompt, long? initial = null)
    {
        while (true)
        {
            var text = PromptForText(title, prompt, initial?.ToString());
            if (text is null)
                return Task.FromResult<long?>(null);

            if (long.TryParse(text.Trim(), out var value))
                return Task.FromResult<long?>(value);

            MessageBox.Query(_app, title, $"'{text}' is not a valid whole number.", "OK");
            // loop and re-prompt
        }
    }

    public Task ShowMessageAsync(string title, IReadOnlyList<string> lines)
    {
        var message = lines.Count == 0 ? string.Empty : string.Join(Environment.NewLine, lines);
        MessageBox.Query(_app, title, message, "OK");
        return Task.CompletedTask;
    }

    public Task<bool> ConfirmAsync(string title, string message)
    {
        // Query returns the index of the chosen button; button 0 ("Yes") => true.
        var choice = MessageBox.Query(_app, title, message, "Yes", "No");
        return Task.FromResult(choice == 0);
    }

    /// <summary>Runs a modal text-entry dialog. Returns the entered text, or null if cancelled.</summary>
    private string? PromptForText(string title, string prompt, string? initial)
    {
        string? result = null;

        var dialog = new Dialog
        {
            Title = title,
            Width = Dim.Percent(70),
            Height = Dim.Absolute(8),
        };

        var label = new Label
        {
            Text = prompt,
            X = 1,
            Y = 0,
        };

        var input = new TextField
        {
            Text = initial ?? string.Empty,
            X = 1,
            Y = 2,
            Width = Dim.Fill(1),
        };

        var ok = new Button { Text = "OK", IsDefault = true };
        ok.Accepting += (_, e) =>
        {
            result = input.Text;
            e.Handled = true;
            dialog.RequestStop();
        };

        var cancel = new Button { Text = "Cancel" };
        cancel.Accepting += (_, e) =>
        {
            result = null;
            e.Handled = true;
            dialog.RequestStop();
        };

        dialog.Add(label, input);
        dialog.AddButton(ok);
        dialog.AddButton(cancel);

        input.SetFocus();

        _app.Run(dialog, null);
        dialog.Dispose();

        return result;
    }
}
