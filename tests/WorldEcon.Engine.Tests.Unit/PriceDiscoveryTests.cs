using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Economy;
using WorldEcon.Domain.Geography;
using WorldEcon.Engine.Demand;
using WorldEcon.Persistence;
using WorldEcon.SharedKernel;
using WorldEcon.SharedKernel.Calendar;

namespace WorldEcon.Engine.Tests.Unit;

public class PriceDiscoveryTests
{
    private static WorldDbContext NewContextOnFile(string path)
        => new(new DbContextOptionsBuilder<WorldDbContext>().UseSqlite($"Data Source={path}").Options);

    private sealed record Seed(string Path, WorldId WorldId, SettlementId SettlementId);

    private static async Task<Seed> SeedAsync(Action<WorldDbContext, WorldId, SettlementId> populate)
    {
        var path = Path.Combine(Path.GetTempPath(), $"we_pd_{Guid.NewGuid():N}.db");
        var world = World.Create("Aerth", 1UL, CalendarDefinition.Default, "1.0.0").Value;
        var continent = Continent.Create(world.Id, "Cont").Value;
        var country = Country.Create(world.Id, continent.Id, "Country").Value;
        var region = Region.Create(world.Id, country.Id, "Region").Value;
        var settlement = Settlement.Create(world.Id, region.Id, "Town", SettlementType.Village, 1, 1, 1000).Value;

        await using var ctx = NewContextOnFile(path);
        await ctx.Database.MigrateAsync();
        ctx.Worlds.Add(world);
        ctx.Continents.Add(continent);
        ctx.Countries.Add(country);
        ctx.Regions.Add(region);
        ctx.Settlements.Add(settlement);
        populate(ctx, world.Id, settlement.Id);
        await ctx.SaveChangesAsync();
        return new Seed(path, world.Id, settlement.Id);
    }

    private static async Task AdvanceAsync(string path, WorldId worldId, long ticks)
    {
        await using var ctx = NewContextOnFile(path);
        var sim = await SimulationContext.LoadAsync(ctx, worldId);
        var engine = new TickEngine(StandardPhases.All());
        await engine.AdvanceAsync(sim, ticks);
    }

    private static async Task<long> StockAsync(string path, GoodId goodId)
    {
        await using var ctx = NewContextOnFile(path);
        return (await ctx.Stockpiles.Where(s => s.OwnerKind == StockpileOwnerKind.Shop && s.GoodId == goodId)
            .FirstAsync()).Quantity;
    }

    // ---- DemandCurve (pure) --------------------------------------------------

    [Test]
    public void DemandCurve_PeaksAtFirstUnit_DecaysToBaseValueAtLast()
    {
        var baseValue = new Money(100);
        long peak = 40_000; // 4x at the first unit
        long quantity = 5;

        DemandCurve.UnitReservationPrice(baseValue, peak, quantity, 1).Should().Be(400);
        DemandCurve.UnitReservationPrice(baseValue, peak, quantity, 5).Should().Be(100);

        long prev = long.MaxValue;
        for (long unit = 1; unit <= quantity; unit++)
        {
            long p = DemandCurve.UnitReservationPrice(baseValue, peak, quantity, unit);
            p.Should().BeLessThanOrEqualTo(prev); // monotonically non-increasing
            prev = p;
        }
    }

    // ---- Elasticity ----------------------------------------------------------

    [Test]
    public async Task ElasticGood_GoesUnsold_WhenAskExceedsWillingness()
    {
        // One shop sells an essential and a comfort good, both base 100 but with a price-belief band of
        // [160,240] (bootstrapped on 200). Essential peak-willingness is 4x base (up to 400 ≥ 240 → always
        // buys); comfort peak-willingness is 1.3x base (up to 130 < 160 → never affordable to want). The
        // inelastic essential should draw down; the elastic comfort good should not move at all.
        GoodId essentialId = default, comfortId = default;
        var seed = await SeedAsync((ctx, worldId, settlementId) =>
        {
            var essential = Good.Create(worldId, "Bread", GoodCategory.Food, new Money(100), "loaf",
                SizeClass.Small, 0, true, 1000, NeedTier.Essential).Value; // peak 4x by tier default
            var comfort = Good.Create(worldId, "Wine", GoodCategory.Luxury, new Money(100), "bottle",
                SizeClass.Small, 0, true, 1000, NeedTier.Comfort).Value;   // peak 1.3x by tier default
            essentialId = essential.Id;
            comfortId = comfort.Id;
            ctx.Goods.AddRange(essential, comfort);

            var shop = Shop.Create(worldId, settlementId, "Market", 0, Money.Zero).Value;
            ctx.Shops.Add(shop);
            ctx.Stockpiles.Add(Stockpile.CreateForShop(worldId, shop.Id, essential.Id, 10_000, new Money(100)).Value);
            ctx.Stockpiles.Add(Stockpile.CreateForShop(worldId, shop.Id, comfort.Id, 10_000, new Money(100)).Value);
            // Pre-seed belief bands at [160,240] for both goods so the ask is well above comfort willingness.
            ctx.ShopPriceBeliefs.Add(ShopPriceBelief.Bootstrap(worldId, shop.Id, essential.Id, new Money(200)));
            ctx.ShopPriceBeliefs.Add(ShopPriceBelief.Bootstrap(worldId, shop.Id, comfort.Id, new Money(200)));

            ctx.Consumers.Add(RepresentativeConsumer.Create(worldId, settlementId, 100, new Money(10_000_000)).Value);
        });

        try
        {
            await AdvanceAsync(seed.Path, seed.WorldId, Tick.DefaultMinutesPerDay);
            (await StockAsync(seed.Path, essentialId)).Should().BeLessThan(10_000); // inelastic: bought
            (await StockAsync(seed.Path, comfortId)).Should().Be(10_000);           // elastic: priced out
        }
        finally { File.Delete(seed.Path); }
    }

    // ---- Belief learning loop ------------------------------------------------

    [Test]
    public async Task BeliefBand_Narrows_WhenShopKeepsSelling()
    {
        // A shop that sells day after day gains confidence: its belief band narrows from the bootstrap
        // width (0.4 × base). Essential demand with deep stock + funded consumers guarantees daily sales.
        var seed = await SeedAsync((ctx, worldId, settlementId) =>
        {
            var good = Good.Create(worldId, "Bread", GoodCategory.Food, new Money(100), "loaf",
                SizeClass.Small, 0, true, 1000, NeedTier.Essential).Value;
            ctx.Goods.Add(good);
            var shop = Shop.Create(worldId, settlementId, "Market", 0, Money.Zero).Value;
            ctx.Shops.Add(shop);
            ctx.Stockpiles.Add(Stockpile.CreateForShop(worldId, shop.Id, good.Id, 10_000_000, new Money(100)).Value);
            ctx.Consumers.Add(RepresentativeConsumer.Create(worldId, settlementId, 100, new Money(100_000_000)).Value);
        });

        try
        {
            await AdvanceAsync(seed.Path, seed.WorldId, 60 * Tick.DefaultMinutesPerDay);
            await using var ctx = NewContextOnFile(seed.Path);
            var belief = await ctx.ShopPriceBeliefs.FirstAsync();
            (belief.High.Units - belief.Low.Units).Should().BeLessThan(40); // narrower than the bootstrap width
        }
        finally { File.Delete(seed.Path); }
    }
}
