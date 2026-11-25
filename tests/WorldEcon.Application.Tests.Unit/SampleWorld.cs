using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using WorldEcon.Persistence;
using WorldEcon.Seeding;
using WorldEcon.WorldGen.Pipeline;

namespace WorldEcon.Application.Tests.Unit;

/// <summary>A throwaway on-disk world from the shipped Aerthos sample seed at grid frequency 16 (as the TUI tests' TestWorld).</summary>
internal static class SampleWorld
{
    private static string SampleSeedPath()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "samples", "aerthos.seed.json")))
            dir = Path.GetDirectoryName(dir);
        if (dir is null)
            throw new FileNotFoundException("Could not locate samples/aerthos.seed.json from the test assembly.");
        return Path.Combine(dir, "samples", "aerthos.seed.json");
    }

    public static WorldDbContext NewContext(string path)
        => new(new DbContextOptionsBuilder<WorldDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options);

    /// <summary>Creates a fresh temp db file, imports the sample seed and returns its path (all connections closed).</summary>
    public static async Task<string> SeedTempDbAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sample_{Guid.NewGuid():N}.db");
        var seed = await new JsonSeedSource(SampleSeedPath()).LoadAsync();
        seed = seed with { Generation = new WorldGenSettings { Grid = new() { Frequency = 16 } } };
        await using (var ctx = NewContext(path))
        {
            await ctx.Database.MigrateAsync();
            await new SeedImporter(ctx).ImportAsync(seed);
        }

        SqliteConnection.ClearAllPools();
        return path;
    }

    /// <summary>A copy of a closed database file (with its WAL side files when present).</summary>
    public static string Copy(string path)
    {
        var copy = Path.Combine(Path.GetTempPath(), $"sample_{Guid.NewGuid():N}.db");
        File.Copy(path, copy);
        foreach (var suffix in new[] { "-wal", "-shm" })
            if (File.Exists(path + suffix)) File.Copy(path + suffix, copy + suffix);
        return copy;
    }

    public static void Delete(string path)
    {
        SqliteConnection.ClearAllPools();
        foreach (var f in new[] { path, path + "-wal", path + "-shm", path + "-journal" })
            File.Delete(f);
    }
}
