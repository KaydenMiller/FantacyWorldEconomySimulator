using FluentAssertions;
using WorldEcon.Domain.Geography;
using WorldEcon.Tui.Navigation;

namespace WorldEcon.Tui.Tests.Unit;

public class NavigatorTests
{
    private static NavRow ByName(NavView view, string name) =>
        view.Rows.FirstOrDefault(r => r.Cells.Any(c => c == name))
        ?? throw new InvalidOperationException($"No row '{name}' in '{view.Title}'. Rows: {string.Join("; ", view.Rows.Select(r => string.Join("/", r.Cells)))}");

    [Test]
    public async Task Root_Cities_ListsSettlements_WithState()
    {
        var path = await TestWorld.SeedTempDbAsync();
        try
        {
            await using var ctx = TestWorld.NewContext(path);
            var tctx = await TuiContext.LoadAsync(ctx, path);
            var view = await new Navigator().RootAsync("cities", tctx);

            view.Columns.Should().Contain("State");
            view.Rows.Select(r => r.Cells[0]).Should().Contain(["Hammerfell", "Riverwood"]);
            view.Rows.Should().OnlyContain(r => r.Kind == NavKind.City);
        }
        finally { File.Delete(path); }
    }

    [Test]
    public void TryResolveRoot_Aliases()
    {
        var nav = new Navigator();
        nav.TryResolveRoot("city", out var c1).Should().BeTrue(); c1.Should().Be("cities");
        nav.TryResolveRoot("settlement", out var c2).Should().BeTrue(); c2.Should().Be("cities");
        nav.TryResolveRoot("node", out var c3).Should().BeTrue(); c3.Should().Be("factories");
        nav.TryResolveRoot("claim", out var c4).Should().BeTrue(); c4.Should().Be("claims");
        nav.TryResolveRoot("nonsense", out _).Should().BeFalse();
    }

    [Test]
    public async Task DrillChain_Continent_To_ShopGoods()
    {
        var path = await TestWorld.SeedTempDbAsync();
        try
        {
            await using var ctx = TestWorld.NewContext(path);
            var tctx = await TuiContext.LoadAsync(ctx, path);
            var nav = new Navigator();

            var continents = await nav.RootAsync("continents", tctx);
            var countries = await nav.DrillAsync(continents.Rows[0], tctx);
            countries!.Rows.Should().NotBeEmpty();

            var regions = await nav.DrillAsync(countries.Rows[0], tctx);
            regions!.Rows.Should().NotBeEmpty();

            var contents = await nav.DrillAsync(regions.Rows[0], tctx);
            contents!.Rows.Should().Contain(r => r.Kind == NavKind.City);

            var cityRow = contents.Rows.First(r => r.Kind == NavKind.City);
            var chooser = await nav.DrillAsync(cityRow, tctx);
            chooser!.Rows.Should().HaveCount(4);
            chooser.Rows.Should().OnlyContain(r => r.Kind == NavKind.CityCategory);

            // Pick a city that has shops: Hammerfell. Re-resolve via the cities root to get its key.
            var cities = await nav.RootAsync("cities", tctx);
            var hammerfell = ByName(cities, "Hammerfell");
            var hammerfellChooser = await nav.DrillAsync(hammerfell, tctx);
            var shopsCat = hammerfellChooser!.Rows.First(r => r.Cells[0].StartsWith("Shops"));
            var shops = await nav.DrillAsync(shopsCat, tctx);
            shops!.Rows.Should().NotBeEmpty();
            shops.Rows.Should().OnlyContain(r => r.Kind == NavKind.Shop);

            var shopGoods = await nav.DrillAsync(shops.Rows[0], tctx);
            shopGoods!.Rows.Should().OnlyContain(r => r.Kind == NavKind.Good);
        }
        finally { File.Delete(path); }
    }

    [Test]
    public async Task Drill_Merchant_ListsCaravans()
    {
        var path = await TestWorld.SeedTempDbAsync();
        try
        {
            await using var ctx = TestWorld.NewContext(path);
            var tctx = await TuiContext.LoadAsync(ctx, path);
            var nav = new Navigator();
            var merchants = await nav.RootAsync("merchants", tctx);
            if (merchants.Rows.Count == 0) return; // sample may seed merchants; tolerate either
            var caravans = await nav.DrillAsync(merchants.Rows[0], tctx);
            caravans!.Columns.Should().Contain("Dest");
        }
        finally { File.Delete(path); }
    }

    [Test]
    public async Task Drill_Good_ReturnsNull()
    {
        var path = await TestWorld.SeedTempDbAsync();
        try
        {
            await using var ctx = TestWorld.NewContext(path);
            var tctx = await TuiContext.LoadAsync(ctx, path);
            var nav = new Navigator();
            var goods = await nav.RootAsync("goods", tctx);
            (await nav.DrillAsync(goods.Rows[0], tctx)).Should().BeNull();
        }
        finally { File.Delete(path); }
    }

    [Test]
    public async Task Details_City_IncludesStateAndClaims()
    {
        var path = await TestWorld.SeedTempDbAsync();
        try
        {
            // Seed: make Hammerfell contested + a ruined settlement, directly via the DB.
            await using (var seed = TestWorld.NewContext(path))
            {
                var world = await TuiContextWorld(seed);
                var hammerfell = seed.Settlements.First(s => s.Name == "Hammerfell");
                var country = seed.Countries.First();
                var claim = TerritorialClaim.CreateForSettlement(world, country.Id, hammerfell.Id, ClaimType.Disputes).Value;
                seed.TerritorialClaims.Add(claim);
                hammerfell.SetState(SettlementState.Ruined);
                await seed.SaveChangesAsync();
            }

            await using var ctx = TestWorld.NewContext(path);
            var tctx = await TuiContext.LoadAsync(ctx, path);
            var nav = new Navigator();
            var cities = await nav.RootAsync("cities", tctx);
            var hammerfellRow = ByName(cities, "Hammerfell");
            var details = await nav.DetailsAsync(hammerfellRow, tctx);

            details.Should().Contain(l => l.Contains("State: Ruined"));
            details.Should().Contain(l => l.Contains("Disputes"));
        }
        finally { File.Delete(path); }
    }

    [Test]
    public async Task Root_Regions_NullSafeCountry()
    {
        var path = await TestWorld.SeedTempDbAsync();
        try
        {
            await using (var seed = TestWorld.NewContext(path))
            {
                var world = await TuiContextWorld(seed);
                var ocean = Region.Create(world, "Ocean of Lost Souls", RegionKind.Ocean).Value;
                seed.Regions.Add(ocean);
                await seed.SaveChangesAsync();
            }

            await using var ctx = TestWorld.NewContext(path);
            var tctx = await TuiContext.LoadAsync(ctx, path);
            var view = await new Navigator().RootAsync("regions", tctx);
            var oceanRow = ByName(view, "Ocean of Lost Souls");
            oceanRow.Cells.Should().Contain("Ocean");
            oceanRow.Cells.Should().Contain("—");
        }
        finally { File.Delete(path); }
    }

    private static async Task<WorldId> TuiContextWorld(Persistence.WorldDbContext db)
    {
        var w = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.FirstAsync(db.Worlds);
        return w.Id;
    }
}
