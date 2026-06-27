using WorldEcon.Domain.Geography;

namespace WorldEcon.Tui.Forms;

/// <summary>Create a <see cref="Continent"/> (top of the geography hierarchy; no parent).</summary>
public sealed class ContinentForm : IEntityForm
{
    public string Label => "Continent";
    public string? ResourceName => "continents";

    public async Task<FormOutcome> RunAsync(TuiContext ctx, IUserInteraction ui)
    {
        const string t = "New Continent";
        var name = await FormPrompts.RequiredTextAsync(ui, t, "Name:");
        if (name is null) return FormOutcome.Cancelled;

        var result = Continent.Create(ctx.World.Id, name);
        if (result.IsError) return FormOutcome.Fail(result.Errors[0].Description);

        ctx.Db.Continents.Add(result.Value);
        await ctx.Db.SaveChangesAsync();
        return FormOutcome.Ok($"Created continent '{name}'.");
    }
}

/// <summary>Create a <see cref="Country"/> within a continent.</summary>
public sealed class CountryForm : IEntityForm
{
    public string Label => "Country";
    public string? ResourceName => "countries";

    public async Task<FormOutcome> RunAsync(TuiContext ctx, IUserInteraction ui)
    {
        const string t = "New Country";

        var continents = await FormRefs.ContinentsAsync(ctx);
        if (continents.Count == 0)
            return FormOutcome.Fail("Create a continent first.");

        var name = await FormPrompts.RequiredTextAsync(ui, t, "Name:");
        if (name is null) return FormOutcome.Cancelled;

        var continentId = await FormPrompts.RefAsync(ui, t, "Continent:", continents);
        if (continentId is null) return FormOutcome.Cancelled;

        var result = Country.Create(ctx.World.Id, new ContinentId(continentId.Value), name);
        if (result.IsError) return FormOutcome.Fail(result.Errors[0].Description);

        ctx.Db.Countries.Add(result.Value);
        await ctx.Db.SaveChangesAsync();
        return FormOutcome.Ok($"Created country '{name}'.");
    }
}

/// <summary>Create a <see cref="Region"/> (kind + optional primary country).</summary>
public sealed class RegionForm : IEntityForm
{
    public string Label => "Region";
    public string? ResourceName => "regions";

    public async Task<FormOutcome> RunAsync(TuiContext ctx, IUserInteraction ui)
    {
        const string t = "New Region";

        var name = await FormPrompts.RequiredTextAsync(ui, t, "Name:");
        if (name is null) return FormOutcome.Cancelled;

        var kind = await FormPrompts.EnumAsync<RegionKind>(ui, t, "Kind:");
        if (kind is null) return FormOutcome.Cancelled;

        // Country is optional (oceans/wilderness have none): offer "(none)" plus existing countries.
        CountryId? countryId = null;
        var countries = await FormRefs.CountriesAsync(ctx);
        if (countries.Count > 0)
        {
            var options = new List<(string Name, Guid Id)> { ("(none)", Guid.Empty) };
            options.AddRange(countries);
            var chosen = await FormPrompts.RefAsync(ui, t, "Primary country:", options);
            if (chosen is null) return FormOutcome.Cancelled;
            if (chosen.Value != Guid.Empty) countryId = new CountryId(chosen.Value);
        }

        var result = Region.Create(ctx.World.Id, name, kind.Value, countryId);
        if (result.IsError) return FormOutcome.Fail(result.Errors[0].Description);

        ctx.Db.Regions.Add(result.Value);
        await ctx.Db.SaveChangesAsync();
        return FormOutcome.Ok($"Created region '{name}'.");
    }
}

/// <summary>Create a <see cref="Settlement"/> within a region.</summary>
public sealed class SettlementForm : IEntityForm
{
    public string Label => "Settlement";
    public string? ResourceName => "cities";

    public async Task<FormOutcome> RunAsync(TuiContext ctx, IUserInteraction ui)
    {
        const string t = "New Settlement";

        var regions = await FormRefs.RegionsAsync(ctx);
        if (regions.Count == 0)
            return FormOutcome.Fail("Create a region first.");

        var name = await FormPrompts.RequiredTextAsync(ui, t, "Name:");
        if (name is null) return FormOutcome.Cancelled;

        var type = await FormPrompts.EnumAsync<SettlementType>(ui, t, "Type:");
        if (type is null) return FormOutcome.Cancelled;

        var regionId = await FormPrompts.RefAsync(ui, t, "Region:", regions);
        if (regionId is null) return FormOutcome.Cancelled;

        var population = await FormPrompts.NumberAsync(ui, t, "Population:", 0);
        if (population is null) return FormOutcome.Cancelled;

        var x = await FormPrompts.NumberAsync(ui, t, "Map X coordinate:", 0);
        if (x is null) return FormOutcome.Cancelled;

        var y = await FormPrompts.NumberAsync(ui, t, "Map Y coordinate:", 0);
        if (y is null) return FormOutcome.Cancelled;

        var result = Settlement.Create(ctx.World.Id, new RegionId(regionId.Value), name, type.Value,
            (int)x.Value, (int)y.Value, population.Value);
        if (result.IsError) return FormOutcome.Fail(result.Errors[0].Description);

        ctx.Db.Settlements.Add(result.Value);
        await ctx.Db.SaveChangesAsync();
        return FormOutcome.Ok($"Created settlement '{name}'.");
    }
}

/// <summary>Create a directed <see cref="Route"/> between two settlements.</summary>
public sealed class RouteForm : IEntityForm
{
    public string Label => "Route";
    public string? ResourceName => "cities";

    public async Task<FormOutcome> RunAsync(TuiContext ctx, IUserInteraction ui)
    {
        const string t = "New Route";

        var settlements = await FormRefs.SettlementsAsync(ctx);
        if (settlements.Count < 2)
            return FormOutcome.Fail("Create at least two settlements first.");

        var fromId = await FormPrompts.RefAsync(ui, t, "From settlement:", settlements);
        if (fromId is null) return FormOutcome.Cancelled;

        var toId = await FormPrompts.RefAsync(ui, t, "To settlement:", settlements);
        if (toId is null) return FormOutcome.Cancelled;
        if (toId.Value == fromId.Value)
            return FormOutcome.Fail("A route's endpoints must be different settlements.");

        var distance = await FormPrompts.NumberAsync(ui, t, "Distance:");
        if (distance is null) return FormOutcome.Cancelled;

        var terrain = await FormPrompts.EnumAsync<Terrain>(ui, t, "Terrain:");
        if (terrain is null) return FormOutcome.Cancelled;

        var danger = await FormPrompts.NumberAsync(ui, t, "Danger (0-10):", 0);
        if (danger is null) return FormOutcome.Cancelled;

        var category = await FormPrompts.EnumAsync<RouteCategory>(ui, t, "Category:");
        if (category is null) return FormOutcome.Cancelled;

        var result = Route.Create(ctx.World.Id, new SettlementId(fromId.Value), new SettlementId(toId.Value),
            distance.Value, terrain.Value, (int)danger.Value, category.Value);
        if (result.IsError) return FormOutcome.Fail(result.Errors[0].Description);

        ctx.Db.Routes.Add(result.Value);
        await ctx.Db.SaveChangesAsync();
        var names = settlements.ToDictionary(s => s.Id, s => s.Name);
        return FormOutcome.Ok($"Created route {names[fromId.Value]} → {names[toId.Value]}.");
    }
}
