using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Geography;
using WorldEcon.Persistence;
using WorldEcon.WorldGen.Pipeline;

namespace WorldEcon.Seeding.Tests.Unit;

/// <summary>
/// The Aerthos sample and <see cref="DemoSeeder"/> (kept in sync) both carry the Greystone Quarry site on the
/// Hammerfell–Greenfield road, so sites can be checked live. Small grids (Frequency 16, ×3, ×4) keep the tests fast.
/// </summary>
public class SampleSiteTests
{
    private static readonly WorldGenSettings Small = new() { Grid = new() { Frequency = 16, RegionMultiplier = 3, TileMultiplier = 4 } };

    private static WorldDbContext Ctx(string path)
        => new(new DbContextOptionsBuilder<WorldDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options);

    private static string SampleSeedPath()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "samples", "aerthos.seed.json")))
            dir = Path.GetDirectoryName(dir);
        return Path.Combine(dir ?? throw new FileNotFoundException("samples/aerthos.seed.json"), "samples", "aerthos.seed.json");
    }

    /// <summary>
    /// The site exists and the Hammerfell–Greenfield road is built. At this small grid the site's coordinates are not
    /// joined to the road by land, so the road is built without it (see RoadGenerator's waypoint fallback); at the
    /// earth-5km preset the road is split at the site (checked in a live run).
    /// </summary>
    private static async Task AssertSiteAndRoadAsync(string path)
    {
        await using var ctx = Ctx(path);
        var site = await ctx.RouteNodes.SingleAsync(n => n.Kind == RouteNodeKind.Site);
        site.Name.Should().Be("Greystone Quarry");
        (await ctx.Roads.ToListAsync()).Should().Contain(r => r.Name == "Hammerfell–Greenfield" && r.Routes.Count > 0);
    }

    [Test]
    [Skip("Generator version 6: on the frequency-16 test world the Hammerfell–Greenfield road with the Greystone waypoint is refused ('waypoints make the path revisit a tile'); needs a decision")]
    public async Task Sample_HasGreystoneQuarrySite_AndHammerfellGreenfieldRoad()
    {
        var path = Path.Combine(Path.GetTempPath(), $"site_sample_{Guid.NewGuid():N}.db");
        try
        {
            var seed = await new JsonSeedSource(SampleSeedPath()).LoadAsync();
            seed = seed with { Generation = Small };
            await using (var ctx = Ctx(path))
            {
                await ctx.Database.MigrateAsync();
                await new SeedImporter(ctx).ImportAsync(seed);
            }

            await AssertSiteAndRoadAsync(path);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }

    [Test]
    [Skip("Generator version 6: on the frequency-16 test world the Hammerfell–Greenfield road with the Greystone waypoint is refused ('waypoints make the path revisit a tile'); needs a decision")]
    public async Task DemoSeeder_HasGreystoneQuarrySite_AndHammerfellGreenfieldRoad()
    {
        var path = Path.Combine(Path.GetTempPath(), $"site_demo_{Guid.NewGuid():N}.db");
        try
        {
            await DemoSeeder.CreateAsync(path, Small);
            SqliteConnection.ClearAllPools();
            await AssertSiteAndRoadAsync(path);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }
}
