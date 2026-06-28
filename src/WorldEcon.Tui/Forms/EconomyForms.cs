using WorldEcon.Domain.Economy;
using WorldEcon.Domain.Geography;
using WorldEcon.SharedKernel;
using WorldEcon.SharedKernel.Measure;

namespace WorldEcon.Tui.Forms;

/// <summary>Create a retail <see cref="Shop"/> in a settlement.</summary>
public sealed class ShopForm : IEntityForm
{
    public string Label => "Shop";
    public string? ResourceName => "shops";

    public async Task<FormOutcome> RunAsync(TuiContext ctx, IUserInteraction ui)
    {
        const string t = "New Shop";

        var settlements = await FormRefs.SettlementsAsync(ctx);
        if (settlements.Count == 0)
            return FormOutcome.Fail("Create a settlement first.");

        var name = await FormPrompts.RequiredTextAsync(ui, t, "Name:");
        if (name is null) return FormOutcome.Cancelled;

        var settlementId = await FormPrompts.RefAsync(ui, t, "Settlement:", settlements);
        if (settlementId is null) return FormOutcome.Cancelled;

        var markup = await FormPrompts.NumberAsync(ui, t, "Markup (basis points, e.g. 2000 = 20%):", 2000);
        if (markup is null) return FormOutcome.Cancelled;

        var till = await FormPrompts.NumberAsync(ui, t, "Starting till (in copper):", 0);
        if (till is null) return FormOutcome.Cancelled;

        var result = Shop.Create(ctx.World.Id, new SettlementId(settlementId.Value), name,
            (int)markup.Value, new Money(till.Value));
        if (result.IsError) return FormOutcome.Fail(result.Errors[0].Description);

        ctx.Db.Shops.Add(result.Value);
        await ctx.Db.SaveChangesAsync();
        return FormOutcome.Ok($"Created shop '{name}'.");
    }
}

/// <summary>Add a shop-owned <see cref="Stockpile"/> (stock a shop with a good at a cost basis).</summary>
public sealed class StockpileForm : IEntityForm
{
    public string Label => "Stock (add to a shop)";
    public string? ResourceName => "shops";

    public async Task<FormOutcome> RunAsync(TuiContext ctx, IUserInteraction ui)
    {
        const string t = "Add Stock";

        var shops = await FormRefs.ShopsAsync(ctx);
        if (shops.Count == 0)
            return FormOutcome.Fail("Create a shop first.");
        var goods = await FormRefs.GoodsAsync(ctx);
        if (goods.Count == 0)
            return FormOutcome.Fail("Create a good first.");

        var shopId = await FormPrompts.RefAsync(ui, t, "Shop:", shops);
        if (shopId is null) return FormOutcome.Cancelled;

        var goodId = await FormPrompts.RefAsync(ui, t, "Good:", goods);
        if (goodId is null) return FormOutcome.Cancelled;

        var quantity = await FormPrompts.NumberAsync(ui, t, "Quantity:");
        if (quantity is null) return FormOutcome.Cancelled;

        var costBasis = await FormPrompts.NumberAsync(ui, t, "Unit cost basis (in copper):");
        if (costBasis is null) return FormOutcome.Cancelled;

        var result = Stockpile.CreateForShop(ctx.World.Id, new ShopId(shopId.Value),
            new GoodId(goodId.Value), quantity.Value, new Money(costBasis.Value));
        if (result.IsError) return FormOutcome.Fail(result.Errors[0].Description);

        ctx.Db.Stockpiles.Add(result.Value);
        await ctx.Db.SaveChangesAsync();
        return FormOutcome.Ok($"Added {quantity.Value} stock to the shop.");
    }
}

/// <summary>Create a <see cref="RepresentativeMerchant"/> seated at a settlement.</summary>
public sealed class MerchantForm : IEntityForm
{
    public string Label => "Merchant";
    public string? ResourceName => "merchants";

    public async Task<FormOutcome> RunAsync(TuiContext ctx, IUserInteraction ui)
    {
        const string t = "New Merchant";

        var settlements = await FormRefs.SettlementsAsync(ctx);
        if (settlements.Count == 0)
            return FormOutcome.Fail("Create a settlement first.");

        var seatId = await FormPrompts.RefAsync(ui, t, "Seat (home settlement):", settlements);
        if (seatId is null) return FormOutcome.Cancelled;

        var capital = await FormPrompts.NumberAsync(ui, t, "Working capital (in copper):", 50000);
        if (capital is null) return FormOutcome.Cancelled;

        var weightText = await FormPrompts.RequiredTextAsync(ui, t, "Weight capacity (e.g. 600 kg):");
        if (weightText is null) return FormOutcome.Cancelled;
        if (!MeasurementFormat.TryParseMass(weightText, out var weightCap))
            return FormOutcome.Fail("Could not parse weight capacity (try e.g. '600 kg').");

        var volumeText = await FormPrompts.RequiredTextAsync(ui, t, "Volume capacity (e.g. 1000 L):");
        if (volumeText is null) return FormOutcome.Cancelled;
        if (!MeasurementFormat.TryParseVolume(volumeText, out var volumeCap))
            return FormOutcome.Fail("Could not parse volume capacity (try e.g. '1000 L').");

        var reach = await FormPrompts.NumberAsync(ui, t, "Trade reach (max route distance):", 1000);
        if (reach is null) return FormOutcome.Cancelled;

        var result = RepresentativeMerchant.Create(ctx.World.Id, new SettlementId(seatId.Value),
            new Money(capital.Value), weightCap, volumeCap, reach.Value);
        if (result.IsError) return FormOutcome.Fail(result.Errors[0].Description);

        ctx.Db.Merchants.Add(result.Value);
        await ctx.Db.SaveChangesAsync();
        return FormOutcome.Ok("Created merchant.");
    }
}

/// <summary>Create a <see cref="RepresentativeConsumer"/> seated at a settlement.</summary>
public sealed class ConsumerForm : IEntityForm
{
    public string Label => "Consumer";
    public string? ResourceName => "consumers";

    public async Task<FormOutcome> RunAsync(TuiContext ctx, IUserInteraction ui)
    {
        const string t = "New Consumer";

        var settlements = await FormRefs.SettlementsAsync(ctx);
        if (settlements.Count == 0)
            return FormOutcome.Fail("Create a settlement first.");

        var seatId = await FormPrompts.RefAsync(ui, t, "Seat (home settlement):", settlements);
        if (seatId is null) return FormOutcome.Cancelled;

        var size = await FormPrompts.NumberAsync(ui, t, "Size (people represented):", 1000);
        if (size is null) return FormOutcome.Cancelled;

        var budget = await FormPrompts.NumberAsync(ui, t, "Starting budget (in copper):", 40000);
        if (budget is null) return FormOutcome.Cancelled;

        var result = RepresentativeConsumer.Create(ctx.World.Id, new SettlementId(seatId.Value),
            size.Value, new Money(budget.Value));
        if (result.IsError) return FormOutcome.Fail(result.Errors[0].Description);

        ctx.Db.Consumers.Add(result.Value);
        await ctx.Db.SaveChangesAsync();
        return FormOutcome.Ok("Created consumer.");
    }
}

/// <summary>Create a <see cref="ResourceEndowment"/> (a farm/mine that extracts a raw good).</summary>
public sealed class EndowmentForm : IEntityForm
{
    public string Label => "Resource endowment (farm/mine)";
    public string? ResourceName => "cities";

    public async Task<FormOutcome> RunAsync(TuiContext ctx, IUserInteraction ui)
    {
        const string t = "New Endowment";

        var settlements = await FormRefs.SettlementsAsync(ctx);
        if (settlements.Count == 0)
            return FormOutcome.Fail("Create a settlement first.");
        var goods = await FormRefs.GoodsAsync(ctx);
        if (goods.Count == 0)
            return FormOutcome.Fail("Create a good first.");

        var settlementId = await FormPrompts.RefAsync(ui, t, "Settlement:", settlements);
        if (settlementId is null) return FormOutcome.Cancelled;

        var goodId = await FormPrompts.RefAsync(ui, t, "Good extracted:", goods);
        if (goodId is null) return FormOutcome.Cancelled;

        var abundance = await FormPrompts.NumberAsync(ui, t, "Abundance (units extracted per day):");
        if (abundance is null) return FormOutcome.Cancelled;

        var result = ResourceEndowment.Create(ctx.World.Id, new SettlementId(settlementId.Value),
            new GoodId(goodId.Value), abundance.Value);
        if (result.IsError) return FormOutcome.Fail(result.Errors[0].Description);

        ctx.Db.ResourceEndowments.Add(result.Value);
        await ctx.Db.SaveChangesAsync();
        return FormOutcome.Ok("Created resource endowment.");
    }
}
