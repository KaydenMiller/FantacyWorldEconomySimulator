using System.Data;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using WorldEcon.Tui.Actions;

namespace WorldEcon.Tui.Shell;

/// <summary>
/// The k9s-style top-level window: a header, a <see cref="TableView"/> showing the current resource,
/// a command/prompt bar (focused with <c>:</c> for commands, or by an action asking for input via
/// <see cref="PromptAsync"/>), and a status/hint bar. All Terminal.Gui usage lives here and in
/// <see cref="ShellUserInteraction"/>; the rest of the app stays UI-agnostic.
///
/// Threading: Terminal.Gui is single-threaded and v2 installs a UI sync-context, so EF async must not
/// block the UI thread. Command work is dispatched to a background thread; dialogs and view mutations
/// marshal back via <see cref="IApplication.Invoke"/>. Text/number input does NOT use modal dialogs
/// (a nested <c>Application.Run</c> dialog does not receive keystrokes in this TG build); instead the
/// in-shell prompt bar collects input and resolves a <see cref="TaskCompletionSource{T}"/>. When no
/// <see cref="IApplication"/> is supplied (headless tests) the shell runs everything inline.
/// </summary>
public sealed class TuiShell : Window
{
    private enum BarMode { None, Command, Prompt }

    private readonly IApplication? _app;
    private readonly TuiContext _ctx;
    private readonly CommandRegistry _registry;

    private readonly Label _header;
    private readonly TableView _table;
    private readonly TextField _bar;
    private readonly Label _barLabel;
    private readonly Label _status;

    private IResource _currentResource;
    private ResourceTable _currentTable = new([], []);

    private BarMode _barMode = BarMode.None;
    private TaskCompletionSource<string?>? _promptTcs;

    public TuiShell(TuiContext ctx, CommandRegistry registry, IUserInteraction? ui = null, IApplication? app = null)
    {
        _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        Ui = ui;
        _app = app;

        // Default to the cities resource (falls back to the first registered resource).
        _currentResource = _registry.ResolveResource("cities") ?? _registry.Resources[0];

        Title = "WorldEcon";

        _header = new Label { X = 0, Y = 0, Width = Dim.Fill() };

        _table = new TableView
        {
            X = 0,
            Y = 1,
            Width = Dim.Fill(),
            Height = Dim.Fill(3),
            FullRowSelect = true,
            MultiSelect = false,
        };

        _barLabel = new Label { X = 0, Y = Pos.AnchorEnd(2), Text = ":", Visible = false };
        _bar = new TextField
        {
            X = 1,
            Y = Pos.AnchorEnd(2),
            Width = Dim.Fill(),
            Visible = false,
        };

        _status = new Label { X = 0, Y = Pos.AnchorEnd(1), Width = Dim.Fill() };

        Add(_header, _table, _barLabel, _bar, _status);

        _bar.KeyDown += OnBarKey;
        _table.KeyDown += OnTableKey;

        // Initial load runs synchronously: before Application.Run (no loop / sync-context yet), so
        // blocking is safe and the table is populated the moment the shell is constructed.
        _currentTable = _currentResource.LoadAsync(_ctx).GetAwaiter().GetResult();
        ApplyTable(_currentTable);
        RefreshHeader();
        RefreshStatus();

        _table.SetFocus();
    }

    /// <summary>The user-interaction implementation actions use. Set after construction in production
    /// (it references this shell's prompt bar); injected directly in headless tests.</summary>
    public IUserInteraction? Ui { get; set; }

    /// <summary>The resource currently displayed in the table.</summary>
    public IResource CurrentResource => _currentResource;

    /// <summary>The materialized table currently displayed (for tests/inspection).</summary>
    public ResourceTable CurrentTable => _currentTable;

    /// <summary>The selected row's key, or null when the table is empty. Read on the UI thread.</summary>
    public string? SelectedKey
    {
        get
        {
            var row = _table.Value?.SelectedCell.Y ?? -1;
            return row >= 0 && row < _currentTable.Rows.Count ? _currentTable.Rows[row].Key : null;
        }
    }

    // ---- in-shell prompt (used by ShellUserInteraction for text/number input) ----------------

    /// <summary>
    /// Shows the in-shell prompt bar and resolves with the entered text (Enter) or null (Esc).
    /// Safe to call from a background thread: it marshals UI work via <see cref="Post"/> and the
    /// returned task completes asynchronously when the user submits.
    /// </summary>
    public Task<string?> PromptAsync(string prompt, string? initial = null)
    {
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() =>
        {
            _promptTcs = tcs;
            _barMode = BarMode.Prompt;
            _barLabel.Text = ">";
            _bar.Text = initial ?? string.Empty;
            _barLabel.Visible = true;
            _bar.Visible = true;
            _status.Text = $" {prompt}   (Enter = accept, Esc = cancel)";
            _bar.SetFocus();
        });
        return tcs.Task;
    }

    // ---- Resource / data plumbing ------------------------------------------------------------

    /// <summary>Switches the displayed resource to <paramref name="resource"/> and reloads its table.</summary>
    public void SwitchResource(IResource resource)
    {
        _currentResource = resource;
        Dispatch(ReloadAndApplyAsync);
    }

    private async Task ReloadAndApplyAsync()
    {
        var table = await _currentResource.LoadAsync(_ctx);
        Post(() =>
        {
            ApplyTable(table);
            RefreshHeader();
            RefreshStatus();
        });
    }

    private void ApplyTable(ResourceTable table)
    {
        _currentTable = table;
        _table.Table = BuildTableSource(table);
        Title = $"WorldEcon — {_currentResource.Name}";
    }

    private static DataTableSource BuildTableSource(ResourceTable table)
    {
        var dt = new DataTable();
        if (table.Columns.Count == 0)
            dt.Columns.Add(" ");
        else
            foreach (var col in table.Columns)
                dt.Columns.Add(col);

        foreach (var row in table.Rows)
        {
            var cells = new object[dt.Columns.Count];
            for (var i = 0; i < cells.Length; i++)
                cells[i] = i < row.Cells.Count ? row.Cells[i] : string.Empty;
            dt.Rows.Add(cells);
        }

        return new DataTableSource(dt);
    }

    private void RefreshHeader()
    {
        var dbPath = _ctx.DbPath ?? "(in-memory)";
        _header.Text = $" {_ctx.World.Name}  •  {_ctx.CurrentDateLabel}  •  {dbPath}";
    }

    private void RefreshStatus()
    {
        var rowActions = _registry.RowActionsFor(_currentResource.Name)
            .Select(a => $"{a.Key} {a.Label.ToLowerInvariant()}");

        var hints = new[] { ":cmd", "hjkl move", "d details", "a advance", "S snapshot", "? help", "q quit" }
            .Concat(rowActions);

        _status.Text = " " + string.Join("  ", hints);
    }

    // ---- Key handling ------------------------------------------------------------------------

    private void OnTableKey(object? sender, Key key)
    {
        // ':' opens the command bar regardless of the typed rune's casing.
        if (key.AsRune.Value == ':')
        {
            ShowCommandBar();
            key.Handled = true;
            return;
        }

        if (key == Key.Q.WithCtrl)
        {
            RequestStop();
            key.Handled = true;
            return;
        }

        // vim-style navigation: forward hjkl to the table as arrow keys.
        switch (key.AsRune.Value)
        {
            case 'j': _table.NewKeyDownEvent(new Key(KeyCode.CursorDown)); key.Handled = true; return;
            case 'k': _table.NewKeyDownEvent(new Key(KeyCode.CursorUp)); key.Handled = true; return;
            case 'h': _table.NewKeyDownEvent(new Key(KeyCode.CursorLeft)); key.Handled = true; return;
            case 'l': _table.NewKeyDownEvent(new Key(KeyCode.CursorRight)); key.Handled = true; return;
        }

        var ch = (char)key.AsRune.Value;
        if (HandleActionKey(ch))
            key.Handled = true;
    }

    /// <summary>
    /// Maps a typed character to a shell command and runs it. Returns true if the key was consumed.
    /// Kept free of Terminal.Gui event types so it is unit-testable in isolation.
    /// </summary>
    public bool HandleActionKey(char ch)
    {
        switch (ch)
        {
            case 'q':
                RequestStop();
                return true;

            case 'd':
                ShowDetails();
                return true;

            case '?':
                ShowHelp();
                return true;
        }

        var global = _registry.GlobalActions.FirstOrDefault(a => a.Key == ch);
        if (global is not null)
        {
            RunGlobalAction(global);
            return true;
        }

        var rowAction = _registry.RowActionsFor(_currentResource.Name).FirstOrDefault(a => a.Key == ch);
        if (rowAction is not null)
        {
            RunRowAction(rowAction);
            return true;
        }

        return false;
    }

    private void RunGlobalAction(IGlobalAction action)
        => Dispatch(async () =>
        {
            await action.ExecuteAsync(_ctx, Ui!);
            await _ctx.ReloadWorldAsync();
            await ReloadAndApplyAsync();
        });

    private void RunRowAction(IRowAction action)
    {
        var key = SelectedKey; // captured on the UI thread before dispatching
        Dispatch(async () =>
        {
            if (key is null)
            {
                await Ui!.ShowMessageAsync(action.Label, ["No row is selected."]);
                return;
            }

            await action.ExecuteAsync(key, _ctx, Ui!);
            await _ctx.ReloadWorldAsync();
            await ReloadAndApplyAsync();
        });
    }

    private void ShowDetails()
    {
        var key = SelectedKey; // captured on the UI thread before dispatching
        Dispatch(async () =>
        {
            if (key is null)
            {
                await Ui!.ShowMessageAsync("Details", ["No row is selected."]);
                return;
            }

            var lines = await _currentResource.DetailsAsync(key, _ctx);
            await Ui!.ShowMessageAsync($"{_currentResource.Name} details", lines);
        });
    }

    private void ShowHelp()
    {
        var lines = new List<string>
        {
            "Commands:",
            "  :        open command bar (type a resource name, Enter to switch)",
            "  hjkl     move (vim) — also arrow keys",
            "  d        details for the selected row",
            "  ?        this help",
            "  q / ^Q   quit",
            "",
            "Global actions:",
        };
        lines.AddRange(_registry.GlobalActions.Select(a => $"  {a.Key}        {a.Label}"));

        lines.Add("");
        lines.Add($"Row actions for '{_currentResource.Name}':");
        var rowActions = _registry.RowActionsFor(_currentResource.Name).ToList();
        if (rowActions.Count == 0)
            lines.Add("  (none)");
        else
            lines.AddRange(rowActions.Select(a => $"  {a.Key}        {a.Label}"));

        lines.Add("");
        lines.Add("Resources:");
        foreach (var r in _registry.Resources)
        {
            var aliases = r.Aliases.Count == 0 ? string.Empty : $"  (aliases: {string.Join(", ", r.Aliases)})";
            lines.Add($"  {r.Name}{aliases}");
        }

        Dispatch(() => Ui!.ShowMessageAsync("Help", lines));
    }

    // ---- Command / prompt bar ----------------------------------------------------------------

    private void ShowCommandBar()
    {
        _barMode = BarMode.Command;
        _barLabel.Text = ":";
        _bar.Text = string.Empty;
        _barLabel.Visible = true;
        _bar.Visible = true;
        _bar.SetFocus();
    }

    private void HideBar()
    {
        _barMode = BarMode.None;
        _bar.Visible = false;
        _barLabel.Visible = false;
        RefreshStatus();
        _table.SetFocus();
    }

    private void ResolvePrompt(string? value)
    {
        var tcs = _promptTcs;
        _promptTcs = null;
        tcs?.SetResult(value);
    }

    private void OnBarKey(object? sender, Key key)
    {
        if (key.KeyCode == KeyCode.Esc)
        {
            var mode = _barMode;
            HideBar();
            key.Handled = true;
            if (mode == BarMode.Prompt)
                ResolvePrompt(null);
            return;
        }

        if (key.KeyCode == KeyCode.Enter)
        {
            var text = _bar.Text ?? string.Empty;
            var mode = _barMode;
            HideBar();
            key.Handled = true;

            if (mode == BarMode.Prompt)
            {
                ResolvePrompt(text); // empty allowed; null only on Esc/cancel
                return;
            }

            // Command mode: resolve a resource token.
            var token = text.Trim();
            if (token.Length == 0)
                return;

            var resource = _registry.ResolveResource(token);
            if (resource is null)
            {
                Dispatch(() => Ui!.ShowMessageAsync("Unknown resource", [$"No resource matches '{token}'."]));
                return;
            }

            SwitchResource(resource);
        }
    }

    // ---- threading helpers -------------------------------------------------------------------

    /// <summary>
    /// Runs command work off the UI thread (so EF async can't deadlock the UI sync-context). Dialogs
    /// and view updates inside <paramref name="work"/> marshal back via <see cref="Post"/> / the UI
    /// layer. In headless mode (no IApplication) the work runs inline.
    /// </summary>
    private void Dispatch(Func<Task> work)
    {
        if (_app is null)
        {
            work().GetAwaiter().GetResult();
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await work();
            }
            catch (Exception ex)
            {
                if (Ui is not null)
                    await Ui.ShowMessageAsync("Error", [ex.Message]);
            }
        });
    }

    /// <summary>Runs <paramref name="action"/> on the UI thread (inline when headless).</summary>
    private void Post(Action action)
    {
        if (_app is null)
            action();
        else
            _app.Invoke(action);
    }
}
