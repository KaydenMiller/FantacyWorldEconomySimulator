using FluentAssertions;
using Microsoft.Data.Sqlite;
using WorldEcon.Persistence;
using WorldEcon.WorldGen.Pipeline;

namespace WorldEcon.Seeding.Tests.Unit;

/// <summary>World files created by <see cref="DemoSeeder.CreateAsync"/> are in WAL mode (the app's time controls need it).</summary>
public class DemoSeederWalTests
{
    private static readonly WorldGenSettings Small = new() { Grid = new() { Frequency = 16, RegionMultiplier = 3, TileMultiplier = 4 } };

    [Test]
    public async Task DemoSeeder_CreateAsync_WritesWalFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"demo_wal_{Guid.NewGuid():N}.db");
        try
        {
            await DemoSeeder.CreateAsync(path, Small);
            (await WorldFileJournal.IsWalAsync(path)).Should().BeTrue();
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var f in new[] { path, path + "-wal", path + "-shm" })
                File.Delete(f);
        }
    }
}
