using System.Data;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using WorldEcon.Tui.Actions;

namespace WorldEcon.Tui.Shell;

/// <summary>
/// The k9s-style top-level window: a header, a <see cref="TableView"/> showing the current resource,
/// a command bar (focused with <c>:</c>), and a status/hint bar. All Terminal.Gui usage lives here and
/// in the sibling <see cref="DialogUserInteraction"/>; the rest of the app stays UI-agnostic.
/// </summary>
public sealed class TuiShell : Window
{
    private readonly TuiContext _ctx;
    private readonly CommandRegistry _registry;
    private readonly IUserInteraction _ui;

    private readonly Label _header;
    private readonly TableView _table;
    private readonly TextField _commandBar;
    private readonly Label _commandLabel;
    private readonly Label _status;

    private IResource _currentResource;
    private ResourceTable _currentTable = new([], []);

    public TuiShell(TuiContext ctx, CommandRegistry registry, IUserInteraction ui)
    {
        _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _ui = ui ?? throw new ArgumentNullException(nameof(ui));

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

        _commandLabel = new Label { X = 0, Y = Pos.AnchorEnd(2), Text = ":", Visible = false };
        _commandBar = new TextField
        {
            X = 1,
            Y = Pos.AnchorEnd(2),
            Width = Dim.Fill(),
            Visible = false,
        };

        _status = new Label { X = 0, Y = Pos.AnchorEnd(1), Width = Dim.Fill() };

        Add(_header, _table, _commandLabel, _commandBar, _status);

        _commandBar.KeyDown += OnCommandBarKey;
        _table.KeyDown += OnTableKey;

        // Build the initial view synchronously so the shell is usable the moment it is constructed
        // (and so the headless smoke test can assert the table is populated without a run loop).
        ReloadTable();
        RefreshHeader();
        RefreshStatus();

        _table.SetFocus();
    }

    /// <summary>The resource currently displayed in the table.</summary>
    public IResource CurrentResource => _currentResource;

    /// <summary>The materialized table currently displayed (for tests/inspection).</summary>
    public ResourceTable CurrentTable => _currentTable;

    /// <summary>The selected row's key, or null when the table is empty.</summary>
    public string? SelectedKey
    {
        get
        {
            var row = _table.Value?.SelectedCell.Y ?? -1;
            return row >= 0 && row < _currentTable.Rows.Count ? _currentTable.Rows[row].Key : null;
        }
    }

    // ---- Resource / data plumbing ------------------------------------------------------------

    /// <summary>Switches the displayed resource to <paramref name="resource"/> and reloads its table.</summary>
    public void SwitchResource(IResource resource)
    {
        _currentResource = resource;
        ReloadTable();
        RefreshStatus();
    }

    private void ReloadTable()
    {
        _currentTable = RunSync(() => _currentResource.LoadAsync(_ctx));
        _table.Table = BuildTableSource(_currentTable);
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

        var hints = new[] { ":cmd", "d details", "a advance", "S snapshot", "? help", "q quit" }
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

        if (key.KeyCode == KeyCode.Esc)
        {
            // Nothing to dismiss in the table view; let the framework handle it.
            return;
        }

        if (key == Key.Q.WithCtrl)
        {
            RequestStop();
            key.Handled = true;
            return;
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

        // Global actions (advance, snapshot, …) by their Key.
        var global = _registry.GlobalActions.FirstOrDefault(a => a.Key == ch);
        if (global is not null)
        {
            RunGlobalAction(global);
            return true;
        }

        // Row actions registered for the current resource.
        var rowAction = _registry.RowActionsFor(_currentResource.Name).FirstOrDefault(a => a.Key == ch);
        if (rowAction is not null)
        {
            RunRowAction(rowAction);
            return true;
        }

        return false;
    }

    private void RunGlobalAction(IGlobalAction action)
    {
        RunSync(() => action.ExecuteAsync(_ctx, _ui));
        RunSync(_ctx.ReloadWorldAsync);
        ReloadTable();
        RefreshHeader();
        RefreshStatus();
    }

    private void RunRowAction(IRowAction action)
    {
        var key = SelectedKey;
        if (key is null)
        {
            RunSync(() => _ui.ShowMessageAsync(action.Label, ["No row is selected."]));
            return;
        }

        RunSync(() => action.ExecuteAsync(key, _ctx, _ui));
        RunSync(_ctx.ReloadWorldAsync);
        ReloadTable();
        RefreshHeader();
        RefreshStatus();
    }

    private void ShowDetails()
    {
        var key = SelectedKey;
        if (key is null)
        {
            RunSync(() => _ui.ShowMessageAsync("Details", ["No row is selected."]));
            return;
        }

        var lines = RunSync(() => _currentResource.DetailsAsync(key, _ctx));
        RunSync(() => _ui.ShowMessageAsync($"{_currentResource.Name} details", lines));
    }

    private void ShowHelp()
    {
        var lines = new List<string>
        {
            "Commands:",
            "  :        open command bar (type a resource name, Enter to switch)",
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

        RunSync(() => _ui.ShowMessageAsync("Help", lines));
    }

    // ---- Command bar -------------------------------------------------------------------------

    private void ShowCommandBar()
    {
        _commandBar.Text = string.Empty;
        _commandLabel.Visible = true;
        _commandBar.Visible = true;
        _commandBar.SetFocus();
    }

    private void HideCommandBar()
    {
        _commandBar.Visible = false;
        _commandLabel.Visible = false;
        _table.SetFocus();
    }

    private void OnCommandBarKey(object? sender, Key key)
    {
        if (key.KeyCode == KeyCode.Esc)
        {
            HideCommandBar();
            key.Handled = true;
            return;
        }

        if (key.KeyCode == KeyCode.Enter)
        {
            var token = (_commandBar.Text ?? string.Empty).Trim();
            HideCommandBar();
            key.Handled = true;

            if (token.Length == 0)
                return;

            var resource = _registry.ResolveResource(token);
            if (resource is null)
            {
                RunSync(() => _ui.ShowMessageAsync("Unknown resource", [$"No resource matches '{token}'."]));
                return;
            }

            SwitchResource(resource);
        }
    }

    // ---- async-over-sync helpers (everything here runs on the UI thread) ---------------------

    private static T RunSync<T>(Func<Task<T>> work)
        => Task.Run(work).GetAwaiter().GetResult();

    private static void RunSync(Func<Task> work)
        => Task.Run(work).GetAwaiter().GetResult();
}
