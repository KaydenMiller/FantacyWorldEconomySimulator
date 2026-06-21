using Microsoft.EntityFrameworkCore;
using WorldEcon.Persistence;
using WorldEcon.Seeding;

namespace WorldEcon.Tui.Tests.Unit;

/// <summary>Seeds a throwaway on-disk SQLite world from the shipped Aerthos sample seed.</summary>
internal static class TestWorld
{
    /// <summary>Path to the repo's sample seed, resolved relative to the running test assembly.</summary>
    private static string SampleSeedPath()
    {
        // Walk up from the test bin dir to the repo root (where 'samples' lives).
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "samples", "aerthos.seed.json")))
            dir = Path.GetDirectoryName(dir);

        if (dir is null)
            throw new FileNotFoundException("Could not locate samples/aerthos.seed.json from the test assembly.");

        return Path.Combine(dir, "samples", "aerthos.seed.json");
    }

    public static WorldDbContext NewContext(string path)
        => new(new DbContextOptionsBuilder<WorldDbContext>().UseSqlite($"Data Source={path}").Options);

    /// <summary>Creates a fresh temp db file and imports the sample seed. Returns the db file path.</summary>
    public static async Task<string> SeedTempDbAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tui_test_{Guid.NewGuid():N}.db");
        var seed = await new JsonSeedSource(SampleSeedPath()).LoadAsync();

        await using var ctx = NewContext(path);
        await ctx.Database.MigrateAsync();
        await new SeedImporter(ctx).ImportAsync(seed);

        return path;
    }
}
