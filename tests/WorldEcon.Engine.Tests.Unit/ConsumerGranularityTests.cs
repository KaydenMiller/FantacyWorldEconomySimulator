using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Economy;
using WorldEcon.Persistence;
using WorldEcon.SharedKernel;

namespace WorldEcon.Engine.Tests.Unit;

public class ConsumerGranularityTests
{
    // Builds one seeded world (a grain shop + a funded consumer) and returns its db file path with the
    // context flushed/closed. Both granularity runs operate on COPIES of this same file, so they share
    // identical entity ids + seed — isolating advance-chunking from cross-world reproducibility (which is
    // deliberately snapshot-based, since the price-belief draw is keyed on the entities' random ids).
    private static async Task<string> BuildWorldAsync()
    {
        var s = await LogTestWorld.CreateAsync();
        var grain = Good.Create(s.World.Id, "Grain", GoodCategory.Food, new Money(10), "sack",
            SizeClass.Medium, 0, true, 100, NeedTier.Essential).Value;
        s.Db.Goods.Add(grain);
        var shop = Shop.Create(s.World.Id, s.Settlement.Id, "Granary", 2000, Money.Zero).Value;
        s.Db.Shops.Add(shop);
        s.Db.Stockpiles.Add(Stockpile.CreateForShop(s.World.Id, shop.Id, grain.Id, 1_000_000, new Money(10)).Value);
        s.Db.Consumers.Add(RepresentativeConsumer.Create(s.World.Id, s.Settlement.Id, 100, new Money(1_000_000)).Value);
        await s.Db.SaveChangesAsync();
        await s.Db.DisposeAsync();
        // Close pooled SQLite handles so the file is fully flushed before we copy it.
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        return s.Path;
    }

    private static async Task<(long stock, long till, long budget)> RunAsync(string path, int chunks, long perChunkTicks)
    {
        await using var db = new WorldDbContext(
            new DbContextOptionsBuilder<WorldDbContext>().UseSqlite($"Data Source={path}").Options);
        var world = await db.Worlds.FirstAsync();
        var sim = await SimulationContext.LoadAsync(db, world.Id);
        var engine = new TickEngine(StandardPhases.All());
        for (int i = 0; i < chunks; i++)
            await engine.AdvanceAsync(sim, perChunkTicks);

        var stock = (await db.Stockpiles.FirstAsync()).Quantity;
        var till = (await db.Shops.FirstAsync()).Till.Units;
        var budget = (await db.Consumers.FirstAsync()).Budget.Units;
        return (stock, till, budget);
    }

    [Test]
    public async Task DemandIsGranularityIndependent()
    {
        var basePath = await BuildWorldAsync();
        var single = basePath + ".single";
        var chunked = basePath + ".chunked";
        System.IO.File.Copy(basePath, single, overwrite: true);
        System.IO.File.Copy(basePath, chunked, overwrite: true);
        try
        {
            var oneShot = await RunAsync(single, 1, 6 * Tick.DefaultMinutesPerDay);
            var inSteps = await RunAsync(chunked, 6, Tick.DefaultMinutesPerDay);
            inSteps.Should().Be(oneShot); // same stock, till, budget after 6 days either way
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var p in new[] { basePath, single, chunked })
                if (System.IO.File.Exists(p)) System.IO.File.Delete(p);
        }
    }
}
