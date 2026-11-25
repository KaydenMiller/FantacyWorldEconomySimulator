using FluentAssertions;
using Microsoft.Data.Sqlite;
using WorldEcon.Application.Globe;
using WorldEcon.Persistence;
using WorldEcon.Seeding;
using WorldEcon.WorldGen.Pipeline;

namespace WorldEcon.Application.Tests.Unit;

/// <summary><see cref="GlobeWorldLoader.LoadAllAsync"/> switches the file to WAL mode and reports whether time controls can run.</summary>
public class GlobeLoadWalTests
{
    private static readonly WorldGenSettings Small = new() { Grid = new() { Frequency = 16, RegionMultiplier = 3, TileMultiplier = 4 } };

    private static async Task SetJournalAsync(string path, string mode)
    {
        SqliteConnection.ClearAllPools(); // the seeder's pooled connection would block leaving WAL mode
        await using var conn = new SqliteConnection($"Data Source={path};Pooling=False");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA journal_mode={mode};";
        await cmd.ExecuteScalarAsync();
    }

    private static void Delete(string path)
    {
        SqliteConnection.ClearAllPools();
        foreach (var f in new[] { path, path + "-wal", path + "-shm", path + "-journal" })
            File.Delete(f);
    }

    [Test]
    public async Task LoadAll_DeleteModeFile_SwitchesToWal_AndSetsTimeControlsAvailable()
    {
        var path = Path.Combine(Path.GetTempPath(), $"we_loadwal_{Guid.NewGuid():N}.db");
        try
        {
            await DemoSeeder.CreateAsync(path, Small);
            await SetJournalAsync(path, "DELETE");

            var loaded = await GlobeWorldLoader.LoadAllAsync(path);

            loaded.TimeControlsAvailable.Should().BeTrue();
            (await WorldFileJournal.IsWalAsync(path)).Should().BeTrue();
        }
        finally
        {
            Delete(path);
        }
    }

    [Test]
    public async Task LoadAll_FileLockedByOtherWriter_LoadsWithoutTimeControls()
    {
        var path = Path.Combine(Path.GetTempPath(), $"we_loadwal_{Guid.NewGuid():N}.db");
        try
        {
            await DemoSeeder.CreateAsync(path, Small);
            await SetJournalAsync(path, "DELETE");
            await using var other = new SqliteConnection($"Data Source={path};Pooling=False");
            await other.OpenAsync();
            await using (var cmd = other.CreateCommand())
            {
                // A reserved lock: readers continue, but the switch to WAL needs exclusive access.
                cmd.CommandText = "BEGIN IMMEDIATE";
                await cmd.ExecuteNonQueryAsync();
            }

            var loaded = await GlobeWorldLoader.LoadAllAsync(path);

            loaded.TimeControlsAvailable.Should().BeFalse();
            loaded.Settlements.Should().NotBeEmpty();
        }
        finally
        {
            Delete(path);
        }
    }
}
