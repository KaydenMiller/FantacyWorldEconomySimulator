using WorldEcon.Persistence.Snapshots;

namespace WorldEcon.Tui.Actions;

/// <summary>Captures a consistent file snapshot of the current SQLite database.</summary>
public sealed class SnapshotAction : IGlobalAction
{
    public char Key => 'S';
    public string Label => "Snapshot";

    public async Task ExecuteAsync(TuiContext ctx, IUserInteraction ui)
    {
        if (string.IsNullOrEmpty(ctx.DbPath))
        {
            await ui.ShowMessageAsync("Snapshot", ["No on-disk database path is known; cannot snapshot."]);
            return;
        }

        var dest = await ui.AskTextAsync("Snapshot", "Destination path:");
        if (string.IsNullOrWhiteSpace(dest))
            return;

        await new SqliteSnapshotService().CaptureAsync(ctx.DbPath, dest);

        await ui.ShowMessageAsync("Snapshot", [$"Snapshot written to {dest}."]);
    }
}
