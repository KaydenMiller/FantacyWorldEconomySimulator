using System.Data;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.Text;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using WorldEcon.Tui.Actions;
using WorldEcon.Tui.Navigation;

namespace WorldEcon.Tui.Shell;

/// <summary>
/// The k9s-style top-level window: header, breadcrumb, a <see cref="TableView"/> of the current drill
/// level, a command/prompt bar, and a status/hint bar. Enter drills into the selected row's children
/// (pushing a nav frame); Esc/Backspace goes back; ':' jumps to a root resource (with autocomplete).
/// All Terminal.Gui usage lives here and in <see cref="ShellUserInteraction"/>.
///
/// Threading: command work runs off the UI thread (EF async must not block the UI sync-context);
/// dialogs and view updates marshal back via <see cref="IApplication.Invoke"/>. Text/number input uses
/// the in-shell prompt bar (a modal Application.Run dialog does not receive keys in this TG build).
/// Headless (no IApplication) → everything runs inline (for tests).
/// </summary>
public sealed class TuiShell : Window
{
    private enum BarMode { None, Command, Prompt }

    private sealed class NavFrame(NavView view, Func<Task<NavView?>> reload)
    {
        public NavView View = view;
        public int Selection;
        public readonly Func<Task<NavView?>> Reload = reload;
    }

    private readonly IApplication? _app;
    private readonly TuiContext _ctx;
    private readonly INavigator _nav;

    private readonly Label _header;
    private readonly Label _breadcrumb;
    private readonly TableView _table;
    private readonly TextField _bar;
    private readonly Label _barLabel;
    private readonly Label _status;

    private readonly List<NavFrame> _stack = [];
    private BarMode _barMode = BarMode.None;
    private TaskCompletionSource<string?>? _promptTcs;

    private readonly SingleWordSuggestionGenerator _rootSuggest = new();
    private readonly IGlobalAction[] _globalActions = [new AdvanceAction(), new SnapshotAction()];
    private readonly IRowAction[] _cityActions = [new BuyOutAction(), new DisableProductionAction(), new EnableProductionAction()];

    public TuiShell(TuiContext ctx, INavigator nav, IUserInteraction? ui = null, IApplication? app = null)
    {
        _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
        _nav = nav ?? throw new ArgumentNullException(nameof(nav));
        Ui = ui;
        _app = app;

        Title = "WorldEcon";

        _header = new Label { X = 0, Y = 0, Width = Dim.Fill() };
        _breadcrumb = new Label { X = 0, Y = 1, Width = Dim.Fill() };

        _table = new TableView
        {
            X = 0,
            Y = 2,
            Width = Dim.Fill(),
            Height = Dim.Fill(3),
            FullRowSelect = true,
            MultiSelect = false,
        };

        _barLabel = new Label { X = 0, Y = Pos.AnchorEnd(2), Text = ":", Visible = false };
        _bar = new TextField { X = 1, Y = Pos.AnchorEnd(2), Width = Dim.Fill(), Visible = false };
        _status = new Label { X = 0, Y = Pos.AnchorEnd(1), Width = Dim.Fill() };

        Add(_header, _breadcrumb, _table, _barLabel, _bar, _status);

        _bar.KeyDown += OnBarKey;
        _table.KeyDown += OnTableKey;

        // Inline (ghost) autocomplete on the command bar — appends the most likely match after the
        // cursor (works at the bottom of the screen, unlike a popup). Suggestions are toggled on only
        // in command mode (resource names) via _rootSuggest.AllSuggestions.
        _bar.Autocomplete = new AppendAutocomplete(_bar) { SuggestionGenerator = _rootSuggest };

        // Initial root (synchronous: before Application.Run, so blocking is safe).
        var root = _nav.RootAsync("cities", _ctx).GetAwaiter().GetResult();
        _stack.Add(new NavFrame(root, () => _nav.RootAsync("cities", _ctx)!));
        ApplyTop();

        _table.SetFocus();
    }

    public IUserInteraction? Ui { get; set; }

    /// <summary>Current drill view (for tests/inspection).</summary>
    public NavView CurrentView => _stack[^1].View;

    /// <summary>Depth of the drill stack (1 = at a root).</summary>
    public int Depth => _stack.Count;

    public string? SelectedKey
    {
        get
        {
            var row = _table.Value?.SelectedCell.Y ?? -1;
            var rows = _stack[^1].View.Rows;
            return row >= 0 && row < rows.Count ? rows[row].Key : null;
        }
    }

    private NavRow? SelectedRow
    {
        get
        {
            var i = _table.Value?.SelectedCell.Y ?? -1;
            var rows = _stack[^1].View.Rows;
            return i >= 0 && i < rows.Count ? rows[i] : null;
        }
    }

    // ---- view rendering ----------------------------------------------------------------------

    private void ApplyTop()
    {
        var frame = _stack[^1];
        _table.Table = BuildTableSource(frame.View);
        Title = $"WorldEcon — {frame.View.Title}";
        _breadcrumb.Text = " " + string.Join("  ›  ", _stack.Select(f => f.View.Title));
        RefreshHeader();
        RefreshStatus();
    }

    private static DataTableSource BuildTableSource(NavView view)
    {
        var dt = new DataTable();
        if (view.Columns.Count == 0)
            dt.Columns.Add(" ");
        else
            foreach (var col in view.Columns)
                dt.Columns.Add(col);

        foreach (var row in view.Rows)
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
        var hints = new List<string> { ":cmd", "hjkl move", "enter drill", "esc back", "d details", "a advance", "S snapshot", "? help", "q quit" };
        if (SelectedRow?.Kind == NavKind.City)
            hints.AddRange(_cityActions.Select(a => $"{a.Key} {a.Label.ToLowerInvariant()}"));
        _status.Text = " " + string.Join("  ", hints);
    }

    // ---- navigation --------------------------------------------------------------------------

    /// <summary>Resets the stack to a single root view. (Inline-safe for tests.)</summary>
    public void SetRoot(string canonicalRootName)
        => Dispatch(async () =>
        {
            var v = await _nav.RootAsync(canonicalRootName, _ctx);
            Post(() =>
            {
                _stack.Clear();
                _stack.Add(new NavFrame(v, () => _nav.RootAsync(canonicalRootName, _ctx)!));
                ApplyTop();
            });
        });

    /// <summary>Drills into the selected row, pushing a child frame (no-op for a leaf row).</summary>
    public void DrillSelected()
    {
        var row = SelectedRow;
        var selection = _table.Value?.SelectedCell.Y ?? 0;
        if (row is null) return;
        Dispatch(async () =>
        {
            var child = await _nav.DrillAsync(row, _ctx);
            if (child is null) return; // leaf
            Post(() =>
            {
                _stack[^1].Selection = selection;
                _stack.Add(new NavFrame(child, () => _nav.DrillAsync(row, _ctx)));
                ApplyTop();
            });
        });
    }

    /// <summary>Pops one level (does nothing at a root).</summary>
    public void Back()
    {
        if (_stack.Count <= 1) return;
        _stack.RemoveAt(_stack.Count - 1);
        ApplyTop();
    }

    private async Task RefreshCurrentAsync()
    {
        var v = await _stack[^1].Reload();
        Post(() =>
        {
            if (v is not null) _stack[^1].View = v;
            ApplyTop();
        });
    }

    // ---- key handling ------------------------------------------------------------------------

    private void OnTableKey(object? sender, Key key)
    {
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

        if (key.KeyCode == KeyCode.Enter)
        {
            DrillSelected();
            key.Handled = true;
            return;
        }

        if (key.KeyCode is KeyCode.Esc or KeyCode.Backspace)
        {
            Back();
            key.Handled = true;
            return;
        }

        // vim-style navigation in the table: forward hjkl to arrow keys. (Confirm dialogs use a y/n
        // prompt rather than a button dialog, so hjkl button-switching isn't needed there.)
        switch (key.AsRune.Value)
        {
            case 'j': _table.NewKeyDownEvent(new Key(KeyCode.CursorDown)); RefreshStatus(); key.Handled = true; return;
            case 'k': _table.NewKeyDownEvent(new Key(KeyCode.CursorUp)); RefreshStatus(); key.Handled = true; return;
            case 'h': _table.NewKeyDownEvent(new Key(KeyCode.CursorLeft)); key.Handled = true; return;
            case 'l': _table.NewKeyDownEvent(new Key(KeyCode.CursorRight)); key.Handled = true; return;
        }

        var ch = (char)key.AsRune.Value;
        if (HandleActionKey(ch))
            key.Handled = true;
    }

    /// <summary>Maps a typed character to a shell command. Returns true if consumed. Test-friendly.</summary>
    public bool HandleActionKey(char ch)
    {
        switch (ch)
        {
            case 'q': RequestStop(); return true;
            case 'd': ShowDetails(); return true;
            case '?': ShowHelp(); return true;
        }

        var global = _globalActions.FirstOrDefault(a => a.Key == ch);
        if (global is not null) { RunGlobalAction(global); return true; }

        if (SelectedRow?.Kind == NavKind.City)
        {
            var rowAction = _cityActions.FirstOrDefault(a => a.Key == ch);
            if (rowAction is not null) { RunRowAction(rowAction); return true; }
        }
        return false;
    }

    private void RunGlobalAction(IGlobalAction action)
        => Dispatch(async () =>
        {
            await action.ExecuteAsync(_ctx, Ui!);
            await _ctx.ReloadWorldAsync();
            await RefreshCurrentAsync();
        });

    private void RunRowAction(IRowAction action)
    {
        var key = SelectedKey;
        Dispatch(async () =>
        {
            if (key is null) { await Ui!.ShowMessageAsync(action.Label, ["No row is selected."]); return; }
            await action.ExecuteAsync(key, _ctx, Ui!);
            await _ctx.ReloadWorldAsync();
            await RefreshCurrentAsync();
        });
    }

    private void ShowDetails()
    {
        var row = SelectedRow;
        if (row is null) return;
        Dispatch(async () =>
        {
            var lines = await _nav.DetailsAsync(row, _ctx);
            await Ui!.ShowMessageAsync($"{row.Kind} details", lines);
        });
    }

    private void ShowHelp()
    {
        var lines = new List<string>
        {
            "Navigation:",
            "  :        command bar — jump to a resource (autocomplete; Enter to go)",
            "  hjkl     move (vim) — also arrow keys",
            "  Enter    drill into the selected row",
            "  Esc/⌫    go back up a level",
            "  d        details for the selected row",
            "  q / ^Q   quit",
            "",
            "Actions:",
            "  a        advance time",
            "  S        snapshot",
            "  b/x/e    (on a city) buy out a good / disable / enable production",
            "",
            "Resources (':' targets):",
            "  " + string.Join(", ", _nav.RootNames),
        };
        Dispatch(() => Ui!.ShowMessageAsync("Help", lines));
    }

    // ---- command / prompt bar ----------------------------------------------------------------

    /// <summary>Shows the in-shell prompt bar; resolves with the entered text (Enter) or null (Esc).
    /// Used by <see cref="ShellUserInteraction"/> for text/number input (from a background thread).</summary>
    public Task<string?> PromptAsync(string prompt, string? initial = null)
    {
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() =>
        {
            _promptTcs = tcs;
            _barMode = BarMode.Prompt;
            _rootSuggest.AllSuggestions = []; // no suggestions for free-text prompts
            _barLabel.Text = ">";
            _bar.Text = initial ?? string.Empty;
            _barLabel.Visible = true;
            _bar.Visible = true;
            _status.Text = $" {prompt}   (Enter = accept, Esc = cancel)";
            _bar.SetFocus();
        });
        return tcs.Task;
    }

    private void ShowCommandBar()
    {
        _barMode = BarMode.Command;
        _barLabel.Text = ":";
        _rootSuggest.AllSuggestions = _nav.RootNames.ToList(); // suggest resource names
        _bar.Text = string.Empty;
        _barLabel.Visible = true;
        _bar.Visible = true;
        _status.Text = " resource: " + string.Join(", ", _nav.RootNames);
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
            if (mode == BarMode.Prompt) ResolvePrompt(null);
            return;
        }

        if (key.KeyCode == KeyCode.Enter)
        {
            var text = _bar.Text ?? string.Empty;
            var mode = _barMode;
            HideBar();
            key.Handled = true;

            if (mode == BarMode.Prompt) { ResolvePrompt(text); return; }

            var token = text.Trim();
            if (token.Length == 0) return;
            if (_nav.TryResolveRoot(token, out var canonical))
            {
                SetRoot(canonical);
                return;
            }
            // Unique-prefix fallback (so ":merc" + Enter jumps to merchants without accepting the ghost).
            var matches = _nav.RootNames.Where(r => r.StartsWith(token, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count == 1)
                SetRoot(matches[0]);
            else
                Dispatch(() => Ui!.ShowMessageAsync("Unknown resource", [$"No resource matches '{token}'. Try: {string.Join(", ", _nav.RootNames)}"]));
        }
    }

    // ---- threading helpers -------------------------------------------------------------------

    private void Dispatch(Func<Task> work)
    {
        if (_app is null) { work().GetAwaiter().GetResult(); return; }
        _ = Task.Run(async () =>
        {
            try { await work(); }
            catch (Exception ex) { if (Ui is not null) await Ui.ShowMessageAsync("Error", [ex.Message]); }
        });
    }

    private void Post(Action action)
    {
        if (_app is null) action();
        else _app.Invoke(action);
    }
}
