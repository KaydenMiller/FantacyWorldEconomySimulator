using System.Diagnostics;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace WorldEcon.Persistence.Tests.Unit;

public class WorldFileJournalTests
{
    /// <summary>A migrated file switched to DELETE mode (EF Core creates new SQLite files in WAL mode).</summary>
    private static async Task<string> NewMigratedFileAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), $"we_journal_{Guid.NewGuid():N}.db");
        await using var ctx = new WorldDbContext(new DbContextOptionsBuilder<WorldDbContext>()
            .UseSqlite($"Data Source={path};Pooling=False").Options);
        await ctx.Database.MigrateAsync();
        await ctx.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=DELETE;");
        return path;
    }

    private static void Delete(string path)
    {
        SqliteConnection.ClearAllPools();
        foreach (var f in new[] { path, path + "-wal", path + "-shm", path + "-journal" })
            File.Delete(f);
    }

    [Test]
    public async Task TryEnableWal_SwitchesDeleteModeFile()
    {
        var path = await NewMigratedFileAsync();
        try
        {
            (await WorldFileJournal.IsWalAsync(path)).Should().BeFalse();
            (await WorldFileJournal.TryEnableWalAsync(path)).Should().BeTrue();
            (await WorldFileJournal.IsWalAsync(path)).Should().BeTrue();
        }
        finally
        {
            Delete(path);
        }
    }

    [Test]
    public async Task TryEnableWal_WalFile_StaysWal()
    {
        var path = await NewMigratedFileAsync();
        try
        {
            (await WorldFileJournal.TryEnableWalAsync(path)).Should().BeTrue();
            (await WorldFileJournal.TryEnableWalAsync(path)).Should().BeTrue();
            (await WorldFileJournal.IsWalAsync(path)).Should().BeTrue();
        }
        finally
        {
            Delete(path);
        }
    }

    [Test]
    public async Task TryEnableWal_FileLockedByOtherWriter_ReturnsFalse()
    {
        var path = await NewMigratedFileAsync();
        try
        {
            await using var other = new SqliteConnection($"Data Source={path};Pooling=False");
            await other.OpenAsync();
            await using (var cmd = other.CreateCommand())
            {
                cmd.CommandText = "BEGIN EXCLUSIVE";
                await cmd.ExecuteNonQueryAsync();
            }

            var sw = Stopwatch.StartNew();
            (await WorldFileJournal.TryEnableWalAsync(path)).Should().BeFalse();
            sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
        }
        finally
        {
            Delete(path);
        }
    }

    [Test]
    public async Task IsWal_MissingFile_ReturnsFalse_WithoutCreatingIt()
    {
        var path = Path.Combine(Path.GetTempPath(), $"we_missing_{Guid.NewGuid():N}.db");
        (await WorldFileJournal.IsWalAsync(path)).Should().BeFalse();
        (await WorldFileJournal.TryEnableWalAsync(path)).Should().BeFalse();
        File.Exists(path).Should().BeFalse();
    }
}
