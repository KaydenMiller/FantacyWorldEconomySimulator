using WorldEcon.Engine;

namespace WorldEcon.Tui.Actions;

/// <summary>Advances the in-world clock by a number of ticks (minutes).</summary>
public sealed class AdvanceAction : IGlobalAction
{
    public char Key => 'a';
    public string Label => "Advance";

    public async Task ExecuteAsync(TuiContext ctx, IUserInteraction ui)
    {
        var ticks = await ui.AskNumberAsync("Advance time", "Number of ticks to advance:", 1440);
        if (ticks is null || ticks <= 0)
            return;

        var sim = await SimulationContext.LoadAsync(ctx.Db, ctx.World.Id);
        await new TickEngine(StandardPhases.All()).AdvanceAsync(sim, ticks.Value);

        await ctx.ReloadWorldAsync();

        await ui.ShowMessageAsync("Advanced", [$"Advanced {ticks.Value} ticks.", ctx.CurrentDateLabel]);
    }
}
