using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Geography;
using WorldEcon.Persistence;
using WorldEcon.SharedKernel.Calendar;

namespace WorldEcon.Engine.Tests.Unit;

/// <summary>Spins up a temp SQLite world with one continent → country → region → settlement, for
/// exercising the logging pipeline end-to-end.</summary>
public static class LogTestWorld
{
    public sealed record Seeded(string Path, WorldDbContext Db, World World,
        Continent Continent, Country Country, PoliticalRegion Region, Settlement Settlement);

    public static async Task<Seeded> CreateAsync()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"logeng-{Guid.NewGuid():N}.db");
        var db = new WorldDbContext(
            new DbContextOptionsBuilder<WorldDbContext>().UseSqlite($"Data Source={path}").Options);
        await db.Database.MigrateAsync();

        var world = World.Create("Test", 1UL, CalendarDefinition.Default, "1.0.0").Value;
        db.Worlds.Add(world);

        var continent = Continent.Create(world.Id, "Praxus").Value;
        var country = Country.Create(world.Id, continent.Id, "Thaloria").Value;
        var region = PoliticalRegion.Create(world.Id, "The Reach", country.Id).Value;
        var settlement = Settlement.Create(world.Id, region.Id, "Hammerfell", SettlementType.City, 0, 0, 50_000).Value;
        db.Continents.Add(continent);
        db.Countries.Add(country);
        db.PoliticalRegions.Add(region);
        db.Settlements.Add(settlement);
        db.PoliticalRegionContinents.Add(PoliticalRegionContinent.Create(world.Id, region.Id, continent.Id).Value);
        await db.SaveChangesAsync();

        return new Seeded(path, db, world, continent, country, region, settlement);
    }

    public static async Task DisposeAsync(Seeded s)
    {
        await s.Db.DisposeAsync();
        System.IO.File.Delete(s.Path);
    }
}
