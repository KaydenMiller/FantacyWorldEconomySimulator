using WorldEcon.Domain.Geography;
using WorldEcon.Engine.Actions;

namespace WorldEcon.Tui.Actions;

/// <summary>Disables all production facilities in the selected settlement.</summary>
public sealed class DisableProductionAction : IRowAction
{
    public char Key => 'x';
    public string Label => "Disable production";
    public string ResourceName => "cities";

    public async Task ExecuteAsync(string rowKey, TuiContext ctx, IUserInteraction ui)
    {
        var settlementId = new SettlementId(Guid.Parse(rowKey));

        if (!await ui.ConfirmAsync("Disable production", "Disable all production in this settlement?"))
            return;

        var result = await new DmActionService(ctx.Db).SetSettlementProductionDisabledAsync(
            ctx.World.Id, settlementId, true, DateTimeOffset.UtcNow);

        await ui.ShowMessageAsync("Disable production",
            [result.IsError ? result.Errors[0].Description : result.Value.Description]);
    }
}
