using WorldEcon.Engine;

namespace WorldEcon.Tui.Actions;

/// <summary>Advances the in-world clock by a human-readable duration (e.g. 1d, 2w, 1M) or a bare
/// tick count. Accepts the same unit suffixes as the CLI: m=minute, h=hour, d=day, w=week,
/// M=month, y=year (case-sensitive: m≠M). A bare integer is treated as raw ticks (minutes).</summary>
public sealed class AdvanceAction : IGlobalAction
{
    public char Key => 'a';
    public string Label => "Advance";

    public async Task ExecuteAsync(TuiContext ctx, IUserInteraction ui)
    {
        var input = await ui.AskTextAsync("Advance time", "Duration to advance (e.g. 1d, 2w, 1M, 1440):", "1d");
        if (string.IsNullOrWhiteSpace(input))
            return;

        if (!ctx.Calendar.TryParseDurationToTicks(input, out var ticks))
        {
            await ui.ShowMessageAsync("Invalid duration",
                [$"'{input}' is not a valid duration.",
                 "Use a bare integer (ticks) or <n><unit> where unit is:",
                 "  m=minute  h=hour  d=day  w=week  M=month  y=year",
                 "(note: m=minute, M=month — case-sensitive)"]);
            return;
        }

        var sim = await SimulationContext.LoadAsync(ctx.Db, ctx.World.Id);
        await new TickEngine(StandardPhases.All()).AdvanceAsync(sim, ticks);

        await ctx.ReloadWorldAsync();

        await ui.ShowMessageAsync("Advanced", [$"Advanced {ticks} ticks.", ctx.CurrentDateLabel]);
    }
}
