using WorldEcon.Domain.Geography;
using WorldEcon.Engine.Actions;

namespace WorldEcon.Tui.Actions;

/// <summary>Restores all production facilities in the selected settlement.</summary>
public sealed class EnableProductionAction : IRowAction
{
    public char Key => 'e';
    public string Label => "Enable production";
    public string ResourceName => "cities";

    public async Task ExecuteAsync(string rowKey, TuiContext ctx, IUserInteraction ui)
    {
        var settlementId = new SettlementId(Guid.Parse(rowKey));

        var result = await new DmActionService(ctx.Db).SetSettlementProductionDisabledAsync(
            ctx.World.Id, settlementId, false, DateTimeOffset.UtcNow);

        await ui.ShowMessageAsync("Enable production",
            [result.IsError ? result.Errors[0].Description : result.Value.Description]);
    }
}
