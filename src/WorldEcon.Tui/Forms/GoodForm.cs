using WorldEcon.Domain.Economy;
using WorldEcon.SharedKernel;
using WorldEcon.SharedKernel.Measure;

namespace WorldEcon.Tui.Forms;

/// <summary>Create a tradeable <see cref="Good"/>.</summary>
public sealed class GoodForm : IEntityForm
{
    public string Label => "Good";
    public string? ResourceName => "goods";

    public async Task<FormOutcome> RunAsync(TuiContext ctx, IUserInteraction ui)
    {
        const string t = "New Good";

        var name = await FormPrompts.RequiredTextAsync(ui, t, "Name:");
        if (name is null) return FormOutcome.Cancelled;

        var category = await FormPrompts.EnumAsync<GoodCategory>(ui, t, "Category:");
        if (category is null) return FormOutcome.Cancelled;

        var baseValue = await FormPrompts.NumberAsync(ui, t, "Base value (in copper):");
        if (baseValue is null) return FormOutcome.Cancelled;

        var unit = await FormPrompts.RequiredTextAsync(ui, t, "Base unit (e.g. loaf, ingot):");
        if (unit is null) return FormOutcome.Cancelled;

        var size = await FormPrompts.EnumAsync<SizeClass>(ui, t, "Size class:");
        if (size is null) return FormOutcome.Cancelled;

        var shelfLife = await FormPrompts.NumberAsync(ui, t, "Shelf life in ticks (0 = never spoils):", 0);
        if (shelfLife is null) return FormOutcome.Cancelled;

        var divisible = await FormPrompts.BoolAsync(ui, t, "Divisible?");
        if (divisible is null) return FormOutcome.Cancelled;

        var consumption = await FormPrompts.NumberAsync(ui, t, "Consumption per-capita (basis points; 0 = not consumed):", 0);
        if (consumption is null) return FormOutcome.Cancelled;

        var needTier = await FormPrompts.EnumAsync<NeedTier>(ui, t, "Need tier:");
        if (needTier is null) return FormOutcome.Cancelled;

        var massText = await FormPrompts.OptionalTextAsync(ui, t, "Mass per unit (e.g. 5 kg; blank = size default):");
        if (massText is null) return FormOutcome.Cancelled;
        Mass? mass = null;
        if (!string.IsNullOrWhiteSpace(massText))
        {
            if (!MeasurementFormat.TryParseMass(massText, out var parsedMass))
                return FormOutcome.Fail("Could not parse mass (try e.g. '5 kg').");
            mass = parsedMass;
        }

        var volumeText = await FormPrompts.OptionalTextAsync(ui, t, "Volume per unit (e.g. 4 L; blank = size default):");
        if (volumeText is null) return FormOutcome.Cancelled;
        Volume? volume = null;
        if (!string.IsNullOrWhiteSpace(volumeText))
        {
            if (!MeasurementFormat.TryParseVolume(volumeText, out var parsedVolume))
                return FormOutcome.Fail("Could not parse volume (try e.g. '4 L').");
            volume = parsedVolume;
        }

        var result = Good.Create(ctx.World.Id, name, category.Value, new Money(baseValue.Value), unit,
            size.Value, shelfLife.Value, divisible.Value, consumption.Value, needTier.Value,
            peakWillingnessMultipleBasisPoints: null, massPerUnit: mass, volumePerUnit: volume);
        if (result.IsError)
            return FormOutcome.Fail(result.Errors[0].Description);

        ctx.Db.Goods.Add(result.Value);
        await ctx.Db.SaveChangesAsync();
        return FormOutcome.Ok($"Created good '{name}'.");
    }
}
