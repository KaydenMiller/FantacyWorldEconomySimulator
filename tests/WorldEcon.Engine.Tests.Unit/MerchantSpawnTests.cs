using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Economy;
using WorldEcon.Domain.Geography;
using WorldEcon.Engine;
using WorldEcon.Engine.Phases;
using WorldEcon.Persistence;
using WorldEcon.SharedKernel;
using WorldEcon.SharedKernel.Calendar;

namespace WorldEcon.Engine.Tests.Unit;

public class MerchantSpawnTests
{
    private static WorldDbContext NewContextOnFile(string path)
        => new(new DbContextOptionsBuilder<WorldDbContext>().UseSqlite($"Data Source={path}").Options);

    private sealed record Seed(string Path, WorldId WorldId, SettlementId Big, SettlementId Small);

    /// <summary>
    /// Seeds a world + geography + two settlements: a big one (pop 50000 => target 5) and a
    /// small one (pop 800 => target max(1,0)=1). No pre-existing merchants.
    /// </summary>
    private static async Task<Seed> SeedAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), $"we_spawn_{Guid.NewGuid():N}.db");
        var world = World.Create("Aerth", 1UL, CalendarDefinition.Default, "1.0.0").Value;
        var continent = Continent.Create(world.Id, "Cont").Value;
        var country = Country.Create(world.Id, continent.Id, "Country").Value;
        var region = Region.Create(world.Id, country.Id, "Region").Value;
        var big = Settlement.Create(world.Id, region.Id, "Big", SettlementType.City, 1, 1, 50_000).Value;
        var small = Settlement.Create(world.Id, region.Id, "Small", SettlementType.Village, 2, 2, 800).Value;

        await using var ctx = NewContextOnFile(path);
        await ctx.Database.MigrateAsync();
        ctx.Worlds.Add(world);
        ctx.Continents.Add(continent);
        ctx.Countries.Add(country);
        ctx.Regions.Add(region);
        ctx.Settlements.Add(big);
        ctx.Settlements.Add(small);
        await ctx.SaveChangesAsync();

        return new Seed(path, world.Id, big.Id, small.Id);
    }

    private static async Task AdvanceAsync(string path, WorldId worldId, long ticks)
    {
        await using var ctx = NewContextOnFile(path);
        var sim = await SimulationContext.LoadAsync(ctx, worldId);
        var engine = new TickEngine(new ISimulationPhase[] { new MerchantSpawnPhase() });
        await engine.AdvanceAsync(sim, ticks);
    }

    [Test]
    public async Task Spawn_FillsToTargetPerSettlement_AndIsIdempotent()
    {
        var seed = await SeedAsync();
        try
        {
            // One week => the weekly phase fires once.
            await AdvanceAsync(seed.Path, seed.WorldId, Tick.DefaultMinutesPerWeek);

            await using (var ctx = NewContextOnFile(seed.Path))
            {
                var merchants = await ctx.Merchants.Where(m => m.WorldId == seed.WorldId).ToListAsync();
                merchants.Count(m => m.Seat == seed.Big).Should().Be(5);
                merchants.Count(m => m.Seat == seed.Small).Should().Be(1);
            }

            // Another week => already at target, no duplicate spawning.
            await AdvanceAsync(seed.Path, seed.WorldId, Tick.DefaultMinutesPerWeek);

            await using (var ctx = NewContextOnFile(seed.Path))
            {
                var merchants = await ctx.Merchants.Where(m => m.WorldId == seed.WorldId).ToListAsync();
                merchants.Count(m => m.Seat == seed.Big).Should().Be(5);
                merchants.Count(m => m.Seat == seed.Small).Should().Be(1);
            }
        }
        finally { File.Delete(seed.Path); }
    }
}
