# TUI Filter / Hints / Help Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make `/` filter any list view client-side (k9s-style), show context-sensitive action hints, and replace the unreadable help MessageBox with a left-aligned NavView pushed onto the nav stack.

**Architecture:** All three goals touch only `TuiShell.cs`. Per-frame filter state (`string? Filter` and `IReadOnlyList<NavRow>? UnfilteredRows`) is added to the existing `NavFrame` private class. `ApplyTop()` applies the filter when rendering: if a filter is set it regex-matches rows before building the `DataTableSource`. The title suffix `  /pattern/` is appended during `ApplyTop()`. The old log-specific `/` handler is replaced with a unified one. Help is rendered as a `NavView` pushed on the stack (with a special `NavKind.Leaf` key so `DrillAsync` returns null for it).

**Tech Stack:** C# 13 / .NET 10, Terminal.Gui 2.x, `System.Text.RegularExpressions.Regex`, existing `NavView`/`NavRow`/`NavKind` types.

---

## File map

| File | Change |
|---|---|
| `src/WorldEcon.Tui/Shell/TuiShell.cs` | All three goals |
| `tests/WorldEcon.Tui.Tests.Unit/ShellSmokeTests.cs` | Add filter + help tests |

No other files change. `Navigator.cs` and `NavView.cs` are read-only for this plan.

---

### Task 1: Add per-frame filter fields to NavFrame and wire into ApplyTop

**Files:**
- Modify: `src/WorldEcon.Tui/Shell/TuiShell.cs`

- [ ] **Step 1: Add `Filter` and `UnfilteredRows` fields to `NavFrame`**

In `TuiShell.cs`, find the `NavFrame` private sealed class (line 29-34):

```csharp
private sealed class NavFrame(NavView view, Func<Task<NavView?>> reload)
{
    public NavView View = view;
    public int Selection;
    public readonly Func<Task<NavView?>> Reload = reload;
}
```

Replace it with:

```csharp
private sealed class NavFrame(NavView view, Func<Task<NavView?>> reload)
{
    public NavView View = view;
    public int Selection;
    public readonly Func<Task<NavView?>> Reload = reload;
    public string? Filter;
    public IReadOnlyList<NavRow>? UnfilteredRows; // null means no filter has been set yet
}
```

- [ ] **Step 2: Add `using System.Text.RegularExpressions;` at the top of `TuiShell.cs`**

The file currently has these using statements at the top (lines 1-10). Add the regex using:

```csharp
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
using WorldEcon.Tui.Navigation;
```

- [ ] **Step 3: Update `ApplyTop()` to apply per-frame filter**

Replace the existing `ApplyTop()` method (lines 132-140):

```csharp
private void ApplyTop()
{
    var frame = _stack[^1];
    _table.Table = BuildTableSource(frame.View);
    Title = $"WorldEcon — {frame.View.Title}";
    _breadcrumb.Text = " " + string.Join("  ›  ", _stack.Select(f => f.View.Title));
    RefreshHeader();
    RefreshStatus();
}
```

With:

```csharp
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
}

private static IReadOnlyList<NavRow> GetFilteredRows(NavFrame frame)
{
    if (frame.Filter is not { } pattern || frame.UnfilteredRows is null)
        return frame.View.Rows;
    Regex? re = null;
    try { re = new Regex(pattern, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)); }
    catch { return frame.View.Rows; } // invalid regex: show all
    return frame.UnfilteredRows.Where(r => r.Cells.Any(c => re.IsMatch(c))).ToList();
}
```

- [ ] **Step 4: Rename `BuildTableSource` to `BuildTableSourceFromRows` and accept an explicit rows list**

Replace the existing `BuildTableSource(NavView view)` method (lines 142-159):

```csharp
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
```

With:

```csharp
private static DataTableSource BuildTableSourceFromRows(NavView view, IReadOnlyList<NavRow> rows)
{
    var dt = new DataTable();
    if (view.Columns.Count == 0)
        dt.Columns.Add(" ");
    else
        foreach (var col in view.Columns)
            dt.Columns.Add(col);

    foreach (var row in rows)
    {
        var cells = new object[dt.Columns.Count];
        for (var i = 0; i < cells.Length; i++)
            cells[i] = i < row.Cells.Count ? row.Cells[i] : string.Empty;
        dt.Rows.Add(cells);
    }
    return new DataTableSource(dt);
}
```

- [ ] **Step 5: Fix `SelectedRow` to look into filtered rows**

The current `SelectedRow` property (lines 120-128) reads `_stack[^1].View.Rows[i]`. When a filter is active the table shows only filtered rows, but `View.Rows` still has all rows. We must look into the filtered list:

```csharp
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
```

And `SelectedKey` (lines 110-118) similarly:

```csharp
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
```

- [ ] **Step 6: Build and run tests to verify nothing broken so far**

```bash
dotnet build src/WorldEcon.Tui -c Release 2>&1 | tail -5
dotnet run --project tests/WorldEcon.Tui.Tests.Unit -c Release 2>&1 | tail -5
```

Expected: `Build succeeded. 0 Warning(s) 0 Error(s)` and `Passed!`

---

### Task 2: Implement unified `/` filter (remove log-specific path)

**Files:**
- Modify: `src/WorldEcon.Tui/Shell/TuiShell.cs`

- [ ] **Step 1: Remove the log-specific `/` handler in `OnTableKey` and replace with generic one**

Find lines 265-281 in `OnTableKey`:

```csharp
// '/' opens a regex filter prompt when viewing a log view.
if (key.AsRune.Value == '/' && Title.StartsWith("WorldEcon — Log — ", StringComparison.Ordinal))
{
    key.Handled = true;
    Dispatch(async () =>
    {
        var pattern = await PromptAsync("/", null);
        var filter = string.IsNullOrEmpty(pattern) ? null : pattern;
        _logFilter = filter;
        if (_currentLogScope is { } sc)
        {
            var view = await _nav.LogViewForScopeAsync(sc.Kind, sc.Id, sc.Title, filter, _ctx);
            Post(() => { _stack[^1] = new NavFrame(view, () => _nav.LogViewForScopeAsync(sc.Kind, sc.Id, sc.Title, filter, _ctx)!); ApplyTop(); });
        }
    });
    return;
}
```

Replace with the generic client-side filter:

```csharp
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
```

- [ ] **Step 2: Add the `ApplyFilter` helper method**

Add a new private method after `Back()`:

```csharp
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
        // Validate regex before storing
        try { _ = new Regex(pattern, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)); }
        catch
        {
            Post(() => Dispatch(() => Ui!.ShowMessageAsync("Filter", [$"Invalid regex: {pattern}"])));
            return;
        }
        frame.UnfilteredRows ??= frame.View.Rows;
        frame.Filter = pattern;
    }
    ApplyTop();
}
```

Note: `ApplyFilter` is called from inside `Post()`, so it runs on the UI thread — no additional `Post()` needed inside it. Adjust the Dispatch/Post in Step 1:

The `Dispatch` in the `/` handler is needed because `PromptAsync` must be called from a background thread (it creates a TCS and posts back to the UI). After the pattern is received, `Post(() => ApplyFilter(pattern))` puts the filter application on the UI thread. The `ApplyFilter` body must NOT call `Post()` again — it is already on the UI thread. Correct version of `ApplyFilter`:

```csharp
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
```

- [ ] **Step 3: Reset filter when Esc-back and when a new view is pushed**

In `Back()` (lines 209-221), after `_stack.RemoveAt(...)` the new top frame already has its own `Filter`/`UnfilteredRows` (set when it was first created — initially null). No extra code needed; each frame carries its own state.

In `DrillSelected()` (lines 191-207), when pushing the child frame via `_stack.Add(new NavFrame(child, ...))`, the new frame starts with `Filter = null` and `UnfilteredRows = null`. Already correct.

In `SetRoot()`, `_stack.Clear()` then `_stack.Add(new NavFrame(...))` — new frame starts clean. Already correct.

The old log-specific `_logFilter` field needs to be kept for `PushView` (it's still used in the reload lambda). However, since we're removing the server-side log filter path and only keeping `_logFilter = null` assignments in `ShowLog()` and `Back()`, we can keep `_logFilter` only for the reload — but since the reload no longer passes a filter, we can pass `null` always. Clean up:

- Remove the `_logFilter = filter;` line from the old `/` handler (already removed in Step 1).
- In `Back()` (lines 212-219), remove the `_logFilter = null;` line (only keep `_currentLogScope = null;`):

```csharp
public void Back()
{
    if (_stack.Count <= 1) return;
    _stack.RemoveAt(_stack.Count - 1);
    // If the new top is not a log view, clear stale log state so `:summary` targets the world.
    if (!_stack[^1].View.Title.StartsWith("Log — ", StringComparison.Ordinal))
        _currentLogScope = null;
    ApplyTop();
}
```

- In `ShowLog()` (lines 351-365), remove `_logFilter = null;` line.
- In `PushView()` (lines 367-371), change the reload lambda to pass `null` instead of `_logFilter`:

```csharp
private void PushView(NavView view, LogScopeKind kind, Guid scopeId, string title)
{
    _stack.Add(new NavFrame(view, () => _nav.LogViewForScopeAsync(kind, scopeId, title, null, _ctx)!));
    ApplyTop();
}
```

- In the `:log` command handler in `OnBarKey` (lines 506-509), remove `_logFilter = null;` line.

- [ ] **Step 4: Remove the now-unused `_logFilter` field**

After cleaning up all `_logFilter` assignments and reads, remove the field declaration (line 51):

```csharp
private string? _logFilter;
```

Check with `grep -n "_logFilter" src/WorldEcon.Tui/Shell/TuiShell.cs` — must return zero hits after removal.

- [ ] **Step 5: Build and test**

```bash
dotnet build src/WorldEcon.Tui -c Release 2>&1 | tail -5
dotnet run --project tests/WorldEcon.Tui.Tests.Unit -c Release 2>&1 | tail -5
```

Expected: `Build succeeded. 0 Warning(s) 0 Error(s)` and `Passed!`

---

### Task 3: Context-sensitive action hints in `RefreshStatus()`

**Files:**
- Modify: `src/WorldEcon.Tui/Shell/TuiShell.cs`

- [ ] **Step 1: Update `RefreshStatus()` to conditionally show `l log`**

Replace the existing `RefreshStatus()` (lines 168-173):

```csharp
private void RefreshStatus()
{
    var hints = new List<string> { ":cmd", "hjk move", "enter drill", "esc back", "d details", "l log", "/ filter", "a advance", "S snapshot", "? help", "q quit" };
    if (SelectedRow?.Kind == NavKind.City)
        hints.AddRange(_cityActions.Select(a => $"{a.Key} {a.Label.ToLowerInvariant()}"));
    _status.Text = " " + string.Join("  ", hints);
}
```

With:

```csharp
private void RefreshStatus()
{
    var row = SelectedRow;
    var hints = new List<string> { ":cmd", "hjk move", "enter drill", "esc back", "d details" };
    if (LogScopeFor(row?.Kind ?? NavKind.Leaf) is not null)
        hints.Add("l log");
    hints.AddRange(["/filter", "a advance", "S snapshot", "? help", "q quit"]);
    if (row?.Kind == NavKind.City)
        hints.AddRange(_cityActions.Select(a => $"{a.Key} {a.Label.ToLowerInvariant()}"));
    _status.Text = " " + string.Join("  ", hints);
}
```

Note: `/ filter` is always shown (no space between `/` and `filter` to keep it compact like k9s); `l log` appears only when `LogScopeFor` returns non-null.

- [ ] **Step 2: Build and test**

```bash
dotnet build src/WorldEcon.Tui -c Release 2>&1 | tail -5
dotnet run --project tests/WorldEcon.Tui.Tests.Unit -c Release 2>&1 | tail -5
```

Expected: `Build succeeded. 0 Warning(s) 0 Error(s)` and `Passed!`

---

### Task 4: Replace `ShowHelp()` with a NavView pushed onto the nav stack

**Files:**
- Modify: `src/WorldEcon.Tui/Shell/TuiShell.cs`

- [ ] **Step 1: Replace `ShowHelp()` with a NavView push**

Replace the existing `ShowHelp()` method (lines 400-423):

```csharp
private void ShowHelp()
{
    var lines = new List<string>
    {
        "Navigation:",
        "  :        command bar — jump to a resource (autocomplete; Enter to go)",
        "  hjk      move (vim) — also arrow keys  (l is repurposed — see below)",
        "  Enter    drill into the selected row",
        "  Esc/⌫    go back up a level",
        "  d        details for the selected row",
        "  l        open scoped log for the selected row",
        "  /        regex-filter the current log view",
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
```

With:

```csharp
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
        Row("q / Ctrl+Q", "quit"),
        Sep(),
        Row("--- Actions ---", string.Empty),
        Row("a", "advance time"),
        Row("S", "snapshot"),
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
```

Note: `ShowHelp()` is called from `HandleActionKey` which may be called inline (tests) or from the UI thread (key handler). `Post()` is idempotent — when `_app is null`, it runs inline; otherwise marshals to UI thread. This is safe.

- [ ] **Step 2: Build and test**

```bash
dotnet build src/WorldEcon.Tui -c Release 2>&1 | tail -5
dotnet run --project tests/WorldEcon.Tui.Tests.Unit -c Release 2>&1 | tail -5
```

Expected: `Build succeeded. 0 Warning(s) 0 Error(s)` and `Passed!`

---

### Task 5: Add tests for filter and help behaviour

**Files:**
- Modify: `tests/WorldEcon.Tui.Tests.Unit/ShellSmokeTests.cs`

- [ ] **Step 1: Add filter test**

Append to `ShellSmokeTests`:

```csharp
[Test]
public async Task Filter_NarrowsRows_AndClearRestores()
{
    var path = await TestWorld.SeedTempDbAsync();
    try
    {
        await using var ctx = TestWorld.NewContext(path);
        var tui = await TuiContext.LoadAsync(ctx, path);
        using var shell = new TuiShell(tui, new Navigator(), new FakeUserInteraction());

        // Cities root has >=2 rows (Hammerfell + Riverwood in test seed).
        var totalRows = shell.CurrentView.Rows.Count;
        totalRows.Should().BeGreaterThan(1);

        // Apply a filter that matches only Hammerfell.
        shell.ApplyFilterForTest("hammer");
        shell.FilteredRowCount.Should().Be(1);

        // Clear the filter — all rows return.
        shell.ApplyFilterForTest(null);
        shell.FilteredRowCount.Should().Be(totalRows);
    }
    finally { File.Delete(path); }
}

[Test]
public async Task HelpView_PushesNavFrame_AndBackPops()
{
    var path = await TestWorld.SeedTempDbAsync();
    try
    {
        await using var ctx = TestWorld.NewContext(path);
        var tui = await TuiContext.LoadAsync(ctx, path);
        using var shell = new TuiShell(tui, new Navigator(), new FakeUserInteraction());

        shell.HandleActionKey('?').Should().BeTrue();
        shell.Depth.Should().Be(2);
        shell.CurrentView.Title.Should().Be("Help");
        shell.CurrentView.Columns.Should().BeEquivalentTo(["Key", "Action"]);

        shell.Back();
        shell.Depth.Should().Be(1);
        shell.CurrentView.Title.Should().Be("Cities");
    }
    finally { File.Delete(path); }
}
```

- [ ] **Step 2: Add test-friendly surface to `TuiShell` for filter inspection**

The tests call `shell.ApplyFilterForTest(pattern)` and `shell.FilteredRowCount`. Add these to `TuiShell.cs` as `internal` test helpers (or `public` since headless tests need access):

```csharp
/// <summary>Test helper: applies a filter to the current frame and re-renders.</summary>
public void ApplyFilterForTest(string? pattern) => ApplyFilter(pattern);

/// <summary>Test helper: returns the number of rows currently visible (after filter).</summary>
public int FilteredRowCount => GetFilteredRows(_stack[^1]).Count;
```

- [ ] **Step 3: Build and test**

```bash
dotnet build src/WorldEcon.Tui -c Release 2>&1 | tail -5
dotnet run --project tests/WorldEcon.Tui.Tests.Unit -c Release 2>&1 | tail -5
```

Expected: `Build succeeded. 0 Warning(s) 0 Error(s)` and `Passed!`

---

### Task 6: Full build verification and commit

**Files:**
- No changes

- [ ] **Step 1: Full solution build (warnings-as-errors)**

```bash
dotnet build /home/kayden/workspaces/dnd -c Release 2>&1 | tail -10
```

Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`

- [ ] **Step 2: Run all TUI tests**

```bash
dotnet run --project /home/kayden/workspaces/dnd/tests/WorldEcon.Tui.Tests.Unit -c Release 2>&1 | tail -10
```

Expected: `Passed!`

- [ ] **Step 3: tmux smoke test — `/` filter on cities view**

```bash
rm -f /tmp/ui.db
dotnet run --project /home/kayden/workspaces/dnd/src/WorldEcon.Cli -c Release -- new /tmp/ui.db
dotnet run --project /home/kayden/workspaces/dnd/src/WorldEcon.Cli -c Release -- advance /tmp/ui.db 1w
tmux kill-session -t ui 2>/dev/null; tmux new-session -d -s ui -x 200 -y 50
tmux send-keys -t ui "dotnet run --project /home/kayden/workspaces/dnd/src/WorldEcon.Tui -c Release -- /tmp/ui.db" Enter
sleep 12
# filter on Cities
tmux send-keys -t ui "/"; sleep 1; tmux send-keys -t ui "hammer" Enter; sleep 2
tmux capture-pane -t ui -p | head -12
```

Expected: only "Hammerfell" row visible; title contains `/hammer/`.

- [ ] **Step 4: tmux smoke — clear filter, open help**

```bash
tmux send-keys -t ui "Escape"; sleep 1
tmux send-keys -t ui "?"; sleep 2
tmux capture-pane -t ui -p | head -25
tmux send-keys -t ui "q"
tmux kill-session -t ui 2>/dev/null
rm -f /tmp/ui.db
```

Expected: help view shows Key/Action columns, left-aligned rows.

- [ ] **Step 5: Commit**

```bash
cd /home/kayden/workspaces/dnd
git add src/WorldEcon.Tui/Shell/TuiShell.cs tests/WorldEcon.Tui.Tests.Unit/ShellSmokeTests.cs
git commit -m "$(cat <<'EOF'
feat(tui): filter any list view with /, context-sensitive hints, readable help

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Self-Review

**Spec coverage:**

| Requirement | Covered by |
|---|---|
| `/` filters any list (client-side regex, case-insensitive, ReDoS guard) | Task 2 Step 1–2 |
| Per-frame filter state (filter + unfiltered rows on NavFrame) | Task 1 Step 1 |
| Filter resets per frame (fresh frame starts null) | Task 2 Step 3 |
| Title shows active filter suffix | Task 1 Step 3 |
| Invalid regex shows message, doesn't crash | Task 2 Step 2 |
| Empty/Esc clears filter | Task 2 Step 2 |
| Remove log-specific server-side rebuild path | Task 2 Step 1 |
| `LogViewForScopeAsync` still builds full unfiltered log view | Navigator unchanged (it receives `null` filter) |
| `:summary` still works | `_currentLogScope` kept; `RunSummary()` unchanged |
| `_logFilter` field removed | Task 2 Step 4 |
| `l log` hint only when `LogScopeFor` non-null | Task 3 Step 1 |
| `/ filter` always shown | Task 3 Step 1 |
| City row hints (b/x/e) kept | Task 3 Step 1 |
| Help as NavView pushed on stack | Task 4 Step 1 |
| Help has correct keys (incl. `/` for any view) | Task 4 Step 1 |
| Esc backs out of help | No extra code — `Back()` pops any frame |
| Tests pass | Task 5 |
| warnings-as-errors 0/0 | Task 6 Step 1 |

**Placeholder scan:** No TBDs, no "add appropriate error handling", all code is concrete.

**Type consistency:**
- `NavFrame.Filter` (string?) and `NavFrame.UnfilteredRows` (IReadOnlyList\<NavRow\>?) used consistently in `ApplyTop()`, `GetFilteredRows()`, `ApplyFilter()`.
- `GetFilteredRows(NavFrame)` is static and called in `ApplyTop()`, `SelectedRow`, `SelectedKey`, and `FilteredRowCount`.
- `ApplyFilterForTest` delegates to `ApplyFilter` — same signature.
- `BuildTableSourceFromRows(NavView, IReadOnlyList<NavRow>)` replaces `BuildTableSource(NavView)` — only caller is `ApplyTop()`.
