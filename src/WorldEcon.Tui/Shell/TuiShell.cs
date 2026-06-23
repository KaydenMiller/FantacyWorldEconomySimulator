using System.Data;
using System.Text.RegularExpressions;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.Text;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using WorldEcon.Domain.Logging;
using WorldEcon.Tui.Actions;
using WorldEcon.Tui.Forms;
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
        public string? Filter;
        public IReadOnlyList<NavRow>? UnfilteredRows; // null means no filter has been set yet
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
    private int _sortColumn = -1;    // -1 = no sort; cycles on 'o'
    private bool _sortDescending;   // false = ascending; toggled per 'o' press

    private (LogScopeKind Kind, Guid Id, string Title)? _currentLogScope;

    private readonly SingleWordSuggestionGenerator _rootSuggest = new();
    private readonly IGlobalAction[] _globalActions = [new AdvanceAction(), new SnapshotAction()];
    private readonly IRowAction[] _cityActions = [new BuyOutAction(), new DisableProductionAction(), new EnableProductionAction()];
    private readonly IReadOnlyList<IEntityForm> _forms = FormRegistry.All;

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

    /// <summary>Test helper: applies a filter to the current frame and re-renders.</summary>
    public void ApplyFilterForTest(string? pattern) => ApplyFilter(pattern);

    /// <summary>Test helper: returns the number of rows currently visible (after filter).</summary>
    public int FilteredRowCount => GetFilteredRows(_stack[^1]).Count;

    public string? SelectedKey
    {
        get
        {
            var row = _table.Value?.SelectedCell.Y ?? -1;
            var frame = _stack[^1];
            var rows = GetFilteredRows(frame);
            return row >= 0 && row < rows.Count ? rows[row].Key : null;
        }
    }

    private NavRow? SelectedRow
    {
        get
        {
            var i = _table.Value?.SelectedCell.Y ?? -1;
            var frame = _stack[^1];
            var rows = GetFilteredRows(frame);
            return i >= 0 && i < rows.Count ? rows[i] : null;
        }
    }

    // ---- view rendering ----------------------------------------------------------------------

    private void ApplyTop()
    {
        var frame = _stack[^1];
        var rows = GetFilteredRows(frame);
        _table.Table = BuildTableSourceFromRows(frame.View, rows);
        var filterSuffix = frame.Filter is { } f ? $"  /{f}/" : string.Empty;
        Title = $"WorldEcon — {frame.View.Title}{filterSuffix}";
        _breadcrumb.Text = " " + string.Join("  ›  ", _stack.Select(fr => fr.View.Title));
        RefreshHeader();
        RefreshStatus();

        // Restore the saved cursor position, clamped to the visible row count.
        if (_app is not null)
        {
            try
            {
                var count = rows.Count;
                var target = Math.Clamp(frame.Selection, 0, Math.Max(0, count - 1));
                _table.SetSelection(0, target, false, null);
                _table.EnsureCursorIsVisible();
                _table.SetNeedsDraw();
            }
            catch { /* headless or no driver — ignore */ }
        }
    }

    private IReadOnlyList<NavRow> GetFilteredRows(NavFrame frame)
    {
        IReadOnlyList<NavRow> rows;
        if (frame.Filter is not { } pattern || frame.UnfilteredRows is null)
        {
            rows = frame.View.Rows;
        }
        else
        {
            Regex? re = null;
            try { re = new Regex(pattern, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)); }
            catch { return frame.View.Rows; } // invalid regex: show all
            rows = frame.UnfilteredRows.Where(r => r.Cells.Any(c => re.IsMatch(c))).ToList();
        }

        // Apply column sort if active (Ordinal string sort; Number-aware is not required for Phase 1).
        if (_sortColumn >= 0 && _sortColumn < frame.View.Columns.Count)
        {
            rows = _sortDescending
                ? rows.OrderByDescending(r => _sortColumn < r.Cells.Count ? r.Cells[_sortColumn] : string.Empty,
                    StringComparer.Ordinal).ToList()
                : rows.OrderBy(r => _sortColumn < r.Cells.Count ? r.Cells[_sortColumn] : string.Empty,
                    StringComparer.Ordinal).ToList();
        }

        return rows;
    }

    private DataTableSource BuildTableSourceFromRows(NavView view, IReadOnlyList<NavRow> rows)
    {
        var dt = new DataTable();
        if (view.Columns.Count == 0)
        {
            dt.Columns.Add(" ");
        }
        else
        {
            for (var i = 0; i < view.Columns.Count; i++)
            {
                var colName = view.Columns[i];
                if (i == _sortColumn)
                    colName += _sortDescending ? " ▼" : " ▲"; // ▼ or ▲
                dt.Columns.Add(colName);
            }
        }

        foreach (var row in rows)
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
        var row = SelectedRow;
        var hints = new List<string> { ":cmd", "hjk move", "enter drill", "esc back", "d details" };
        if (LogScopeFor(row?.Kind ?? NavKind.Leaf) is not null)
            hints.Add("l log");
        hints.AddRange(["/filter", "o sort", "n new", "a advance", "S snapshot", "? help", "q quit"]);
        if (row is not null && EditRegistry.ForKind(row.Kind) is { } ef)
            hints.Add($"E edit {ef.Label}");
        if (row?.Kind == NavKind.City)
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
                _sortColumn = -1;
                _sortDescending = false;
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
                _sortColumn = -1;
                _sortDescending = false;
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
        // If the new top is not a log view, clear stale log state so `:summary` targets the world.
        if (!_stack[^1].View.Title.StartsWith("Log — ", StringComparison.Ordinal))
            _currentLogScope = null;
        _sortColumn = -1;
        _sortDescending = false;
        ApplyTop();
    }

    private void ApplyFilter(string? pattern)
    {
        var frame = _stack[^1];
        if (string.IsNullOrEmpty(pattern))
        {
            frame.Filter = null;
            frame.UnfilteredRows = null;
        }
        else
        {
            // Validate regex; show brief message and bail on invalid pattern
            try { _ = new Regex(pattern, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)); }
            catch
            {
                Dispatch(() => Ui!.ShowMessageAsync("Filter", [$"Invalid regex: {pattern}"]));
                return;
            }
            frame.UnfilteredRows ??= frame.View.Rows;
            frame.Filter = pattern;
        }
        ApplyTop();
    }

    private async Task RefreshCurrentAsync()
    {
        // Capture current cursor before reloading so ApplyTop restores it.
        var savedSelection = _table.Value?.SelectedCell.Y ?? 0;
        var v = await _stack[^1].Reload();
        Post(() =>
        {
            _stack[^1].Selection = savedSelection;
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

        // '/' opens a regex filter prompt on ANY view (k9s-style, client-side).
        if (key.AsRune.Value == '/')
        {
            key.Handled = true;
            Dispatch(async () =>
            {
                var frame = _stack[^1];
                var currentFilter = frame.Filter;
                var pattern = await PromptAsync("/", currentFilter);
                Post(() => ApplyFilter(pattern));
            });
            return;
        }

        // 'o' cycles sort: unsorted → col0 asc → col0 desc → col1 asc → col1 desc → … → unsorted.
        if (key.AsRune.Value == 'o')
        {
            var cols = _stack[^1].View.Columns.Count;
            if (cols > 0)
            {
                if (_sortColumn < 0)
                {
                    // unsorted → first column ascending
                    _sortColumn = 0;
                    _sortDescending = false;
                }
                else if (!_sortDescending)
                {
                    // ascending → descending (same column)
                    _sortDescending = true;
                }
                else
                {
                    // descending → next column ascending; if past last column → unsorted
                    var next = _sortColumn + 1;
                    if (next >= cols)
                    {
                        _sortColumn = -1;
                        _sortDescending = false;
                    }
                    else
                    {
                        _sortColumn = next;
                        _sortDescending = false;
                    }
                }
                ApplyTop();
            }
            key.Handled = true;
            return;
        }

        // vim-style navigation in the table: forward hjk to arrow keys. (l is now log; use arrow keys
        // for cursor-right. Confirm dialogs use a y/n prompt rather than a button dialog, so hjkl
        // button-switching isn't needed there.)
        switch (key.AsRune.Value)
        {
            case 'j': _table.NewKeyDownEvent(new Key(KeyCode.CursorDown)); RefreshStatus(); key.Handled = true; return;
            case 'k': _table.NewKeyDownEvent(new Key(KeyCode.CursorUp)); RefreshStatus(); key.Handled = true; return;
            case 'h': _table.NewKeyDownEvent(new Key(KeyCode.CursorLeft)); key.Handled = true; return;
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
            case 'l': ShowLog(); return true;
            case 'n': ShowNewEntityForm(); return true;
            case 'E': ShowEditForm(); return true;
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

    /// <summary>'n' (new): choose an entity type, run its create-form, then refresh and navigate to
    /// the new entity's resource so the result is visible.</summary>
    private void ShowNewEntityForm()
        => Dispatch(async () =>
        {
            var labels = _forms.Select(f => f.Label).ToList();
            var idx = await Ui!.AskChoiceAsync("New", "Create what?", labels);
            if (idx is null)
                return; // cancelled

            var form = _forms[idx.Value];
            var outcome = await form.RunAsync(_ctx, Ui!);
            await _ctx.ReloadWorldAsync();

            if (!outcome.Created)
            {
                // Surface validation/precondition failures; stay put on cancel without a popup.
                if (!outcome.Message.Equals("Cancelled.", StringComparison.Ordinal))
                    await Ui!.ShowMessageAsync(form.Label, [outcome.Message]);
                await RefreshCurrentAsync();
                return;
            }

            await Ui!.ShowMessageAsync(form.Label, [outcome.Message]);
            if (form.ResourceName is { } root)
                SetRoot(root);          // jump to the resource so the new row is visible
            else
                await RefreshCurrentAsync();
        });

    /// <summary>'E' (edit): if the selected row's kind has an edit-form, run it on that entity.</summary>
    private void ShowEditForm()
    {
        var row = SelectedRow;
        if (row is null) return;
        var form = EditRegistry.ForKind(row.Kind);
        if (form is null) return;                       // nothing editable for this kind
        if (!Guid.TryParse(row.Key, out var id)) return; // composite/non-entity row

        Dispatch(async () =>
        {
            var outcome = await form.RunAsync(id, _ctx, Ui!);
            await _ctx.ReloadWorldAsync();
            if (outcome.Created || !outcome.Message.Equals("Cancelled.", StringComparison.Ordinal))
                await Ui!.ShowMessageAsync($"Edit {form.Label}", [outcome.Message]);
            await RefreshCurrentAsync();
        });
    }

    private void ShowDetails()
    {
        var row = SelectedRow;
        if (row is null) return;
        var name = row.Cells.Count > 0 ? row.Cells[0] : row.Kind.ToString();
        Dispatch(async () =>
        {
            var lines = await _nav.DetailsAsync(row, _ctx);
            var detailRows = lines.Select(line =>
            {
                var sep = line.IndexOf(": ", StringComparison.Ordinal);
                if (sep >= 0)
                    return new NavRow(line, NavKind.Leaf, [line[..sep], line[(sep + 2)..]]);
                return new NavRow(line, NavKind.Leaf, [line, string.Empty]);
            }).ToList();
            var detailView = new NavView($"Details — {name}", ["Field", "Value"], detailRows);
            Post(() =>
            {
                _sortColumn = -1;
                _sortDescending = false;
                _stack.Add(new NavFrame(detailView, () => Task.FromResult<NavView?>(detailView)));
                ApplyTop();
            });
        });
    }

    private void ShowLog()
    {
        var row = SelectedRow;
        if (row is null) return;
        var scope = LogScopeFor(row.Kind);
        if (scope is null) return;
        var title = row.Cells.Count > 0 ? row.Cells[0] : row.Kind.ToString();
        _currentLogScope = (scope.Value, Guid.Parse(row.Key), title);
        Dispatch(async () =>
        {
            var view = await _nav.LogViewForScopeAsync(scope.Value, Guid.Parse(row.Key), title, null, _ctx);
            Post(() => { PushView(view, scope.Value, Guid.Parse(row.Key), title); });
        });
    }

    private void PushView(NavView view, LogScopeKind kind, Guid scopeId, string title)
    {
        _sortColumn = -1;
        _sortDescending = false;
        _stack.Add(new NavFrame(view, () => _nav.LogViewForScopeAsync(kind, scopeId, title, null, _ctx)!));
        ApplyTop();
    }

    private static LogScopeKind? LogScopeFor(NavKind kind) => kind switch
    {
        NavKind.Continent => LogScopeKind.Continent,
        NavKind.Country => LogScopeKind.Country,
        NavKind.Region => LogScopeKind.Region,
        NavKind.City => LogScopeKind.Settlement,
        NavKind.Merchant => LogScopeKind.Merchant,
        NavKind.Shop => LogScopeKind.Shop,
        NavKind.Factory => LogScopeKind.Factory,
        _ => null,
    };

    private void RunSummary()
    {
        var sc = _currentLogScope ?? (LogScopeKind.World, _ctx.World.Id.Value, "World");
        Dispatch(async () =>
        {
            var sum = await new WorldEcon.Application.Logging.SummaryService(_ctx.Db)
                .SummarizeAsync(_ctx.World.Id, sc.Kind, sc.Id,
                    new WorldEcon.SharedKernel.Tick(0), _ctx.World.CurrentTick);
            var lines = new List<string> { $"Total events: {sum.TotalEvents}" };
            foreach (var kv in sum.CountByType)
                lines.Add($"{kv.Key}: {kv.Value}");
            await Ui!.ShowMessageAsync($"Summary — {sc.Title}", lines);
        });
    }

    private void ShowHelp()
    {
        static NavRow Row(string key, string action) =>
            new(key, NavKind.Leaf, [key, action]);
        static NavRow Sep() =>
            new(string.Empty, NavKind.Leaf, [string.Empty, string.Empty]);

        var rows = new List<NavRow>
        {
            Row("--- Navigation ---", string.Empty),
            Row(":", "command bar — jump to a resource (autocomplete; Enter to go)"),
            Row("hjk / arrows", "move cursor"),
            Row("Enter", "drill into the selected row"),
            Row("Esc / Backspace", "go back up one level"),
            Row("d", "details for the selected row"),
            Row("l", "open scoped log (only on loggable rows)"),
            Row("/", "regex-filter the current view (any view, client-side)"),
            Row("q / Ctrl+Q / :q", "quit"),
            Sep(),
            Row("--- Actions ---", string.Empty),
            Row("n", "new — create an entity (good, settlement, shop, recipe, …)"),
            Row("E", "edit the selected row (settlement state, region, merchant capital, shop till)"),
            Row("a", "advance time"),
            Row("S", "snapshot"),
            Row("o", "cycle sort: unsorted → col0 ▲ → col0 ▼ → col1 ▲ → … → unsorted; active column header shows ▲/▼"),
            Row("b", "(on a city) buy out a good"),
            Row("x", "(on a city) disable production"),
            Row("e", "(on a city) enable production"),
            Sep(),
            Row("--- Resources (: targets) ---", string.Empty),
        };
        foreach (var name in _nav.RootNames)
            rows.Add(Row(name, string.Empty));

        var helpView = new NavView("Help", ["Key", "Action"], rows);
        Post(() =>
        {
            _stack.Add(new NavFrame(helpView, () => Task.FromResult<NavView?>(helpView)));
            ApplyTop();
        });
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
            if (token is "q" or "quit") { RequestStop(); return; }   // vim-style :q
            if (_nav.TryResolveRoot(token, out var canonical))
            {
                if (canonical == "summary")
                {
                    RunSummary();
                    return;
                }
                if (canonical == "log")
                    _currentLogScope = (LogScopeKind.World, _ctx.World.Id.Value, "World");
                SetRoot(canonical);
                return;
            }
            // Unique-prefix fallback (so ":merc" + Enter jumps to merchants without accepting the ghost).
            var matches = _nav.RootNames.Where(r => r.StartsWith(token, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count == 1)
            {
                if (matches[0] == "summary") { RunSummary(); return; }
                if (matches[0] == "log")
                    _currentLogScope = (LogScopeKind.World, _ctx.World.Id.Value, "World");
                SetRoot(matches[0]);
            }
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
