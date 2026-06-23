using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Economy;
using WorldEcon.Domain.Geography;
using WorldEcon.SharedKernel;
using WorldEcon.Tui.Navigation;

namespace WorldEcon.Tui.Forms;

/// <summary>
/// An edit-form that mutates the currently-selected row's entity. The shell's <c>E</c> (edit) key
/// looks one up by the selected row's <see cref="NavKind"/> and runs it. Edits are limited to the
/// mutations the domain actually exposes (most aggregates are create-only by design).
/// </summary>
public interface IRowEditForm
{
    /// <summary>The row kind this form edits (e.g. <see cref="NavKind.City"/>).</summary>
    NavKind Kind { get; }

    /// <summary>Short noun shown in hints/messages, e.g. "settlement state".</summary>
    string Label { get; }

    Task<FormOutcome> RunAsync(Guid entityId, TuiContext ctx, IUserInteraction ui);
}

/// <summary>Set a settlement's state (Active / Ruined / Abandoned).</summary>
public sealed class SettlementEditForm : IRowEditForm
{
    public NavKind Kind => NavKind.City;
    public string Label => "settlement state";

    public async Task<FormOutcome> RunAsync(Guid id, TuiContext ctx, IUserInteraction ui)
    {
        var s = await ctx.Db.Settlements.FirstOrDefaultAsync(x => x.Id == new SettlementId(id));
        if (s is null) return FormOutcome.Fail("Settlement not found.");

        var state = await FormPrompts.EnumAsync<SettlementState>(ui, $"Edit {s.Name}", $"State (now {s.State}):");
        if (state is null) return FormOutcome.Cancelled;

        s.SetState(state.Value);
        await ctx.Db.SaveChangesAsync();
        return FormOutcome.Ok($"{s.Name} is now {state.Value}.");
    }
}

/// <summary>Edit a region's kind and primary country.</summary>
public sealed class RegionEditForm : IRowEditForm
{
    public NavKind Kind => NavKind.Region;
    public string Label => "region";

    public async Task<FormOutcome> RunAsync(Guid id, TuiContext ctx, IUserInteraction ui)
    {
        var r = await ctx.Db.Regions.FirstOrDefaultAsync(x => x.Id == new RegionId(id));
        if (r is null) return FormOutcome.Fail("Region not found.");
        var t = $"Edit {r.Name}";

        var kind = await FormPrompts.EnumAsync<RegionKind>(ui, t, $"Kind (now {r.Kind}):");
        if (kind is null) return FormOutcome.Cancelled;

        var countries = await FormRefs.CountriesAsync(ctx);
        var options = new List<(string Name, Guid Id)> { ("(none)", Guid.Empty) };
        options.AddRange(countries);
        var chosen = await FormPrompts.RefAsync(ui, t, "Primary country:", options);
        if (chosen is null) return FormOutcome.Cancelled;

        r.SetKind(kind.Value);
        r.SetPrimaryCountry(chosen.Value == Guid.Empty ? null : new CountryId(chosen.Value));
        await ctx.Db.SaveChangesAsync();
        return FormOutcome.Ok($"Updated region '{r.Name}'.");
    }
}

/// <summary>Adjust a merchant's working capital (DM tool): positive adds, negative removes.</summary>
public sealed class MerchantEditForm : IRowEditForm
{
    public NavKind Kind => NavKind.Merchant;
    public string Label => "merchant capital";

    public async Task<FormOutcome> RunAsync(Guid id, TuiContext ctx, IUserInteraction ui)
    {
        var m = await ctx.Db.Merchants.FirstOrDefaultAsync(x => x.Id == new MerchantId(id));
        if (m is null) return FormOutcome.Fail("Merchant not found.");

        var delta = await FormPrompts.NumberAsync(ui, "Edit merchant",
            $"Adjust capital in copper (now {ctx.FormatMoney(m.Capital)}); + to add, - to remove:");
        if (delta is null) return FormOutcome.Cancelled;

        if (delta.Value > 0)
            m.Earn(new Money(delta.Value));
        else if (delta.Value < 0)
            m.Spend(new Money(Math.Min(-delta.Value, m.Capital.Units))); // clamp; cannot go below zero

        await ctx.Db.SaveChangesAsync();
        return FormOutcome.Ok($"Capital is now {ctx.FormatMoney(m.Capital)}.");
    }
}

/// <summary>Add money to a shop's till (DM tool). The till can only be credited (no debit in the domain).</summary>
public sealed class ShopEditForm : IRowEditForm
{
    public NavKind Kind => NavKind.Shop;
    public string Label => "shop till";

    public async Task<FormOutcome> RunAsync(Guid id, TuiContext ctx, IUserInteraction ui)
    {
        var shop = await ctx.Db.Shops.FirstOrDefaultAsync(x => x.Id == new ShopId(id));
        if (shop is null) return FormOutcome.Fail("Shop not found.");

        var amount = await FormPrompts.NumberAsync(ui, $"Edit {shop.Name}",
            $"Add to till in copper (now {ctx.FormatMoney(shop.Till)}):", 0);
        if (amount is null) return FormOutcome.Cancelled;
        if (amount.Value < 0)
            return FormOutcome.Fail("The till can only be credited (enter a positive amount).");

        if (amount.Value > 0)
            shop.CreditTill(new Money(amount.Value));
        await ctx.Db.SaveChangesAsync();
        return FormOutcome.Ok($"{shop.Name} till is now {ctx.FormatMoney(shop.Till)}.");
    }
}

/// <summary>Edit forms keyed by the row kind they apply to.</summary>
public static class EditRegistry
{
    private static readonly IRowEditForm[] Forms =
    [
        new SettlementEditForm(),
        new RegionEditForm(),
        new MerchantEditForm(),
        new ShopEditForm(),
    ];

    public static IRowEditForm? ForKind(NavKind kind) => Array.Find(Forms, f => f.Kind == kind);
}
