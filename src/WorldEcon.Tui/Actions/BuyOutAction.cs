using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Geography;
using WorldEcon.Engine.Actions;

namespace WorldEcon.Tui.Actions;

/// <summary>Party buys out a good off the shops of the selected settlement.</summary>
public sealed class BuyOutAction : IRowAction
{
    public char Key => 'b';
    public string Label => "Buy out good";
    public string ResourceName => "cities";

    public async Task ExecuteAsync(string rowKey, TuiContext ctx, IUserInteraction ui)
    {
        var settlementId = new SettlementId(Guid.Parse(rowKey));

        var goodName = await ui.AskTextAsync("Buy out good", "Good name:");
        if (string.IsNullOrWhiteSpace(goodName))
            return;

        var quantity = await ui.AskNumberAsync("Buy out good", $"Quantity of {goodName} to buy:");
        if (quantity is null || quantity <= 0)
            return;

        var good = await ctx.Db.Goods.FirstOrDefaultAsync(
            g => g.WorldId == ctx.World.Id && g.Name == goodName);
        if (good is null)
        {
            await ui.ShowMessageAsync("Buy out good", [$"No good named '{goodName}' in this world."]);
            return;
        }

        var result = await new LogEventService(ctx.Db).BuyFromShopsAsync(
            ctx.World.Id, settlementId, good.Id, quantity.Value, DateTimeOffset.UtcNow);

        if (result.IsError)
        {
            await ui.ShowMessageAsync("Buy out good", [result.Errors[0].Description]);
            return;
        }

        await ui.ShowMessageAsync("Buy out good", [result.Value.Message]);
    }
}
