using Microsoft.Data.Sqlite;

namespace WorldEcon.Persistence.Snapshots;

/// <summary>Consistent file snapshot via SQLite `VACUUM INTO` (transactional + compacted).</summary>
public sealed class SqliteSnapshotService : ISnapshotService
{
    public async Task CaptureAsync(string sourceDbPath, string destDbPath)
    {
        if (File.Exists(destDbPath))
            File.Delete(destDbPath); // VACUUM INTO requires the target not to pre-exist

        await using var connection = new SqliteConnection($"Data Source={sourceDbPath}");
        await connection.OpenAsync();

        await using (var checkpoint = connection.CreateCommand())
        {
            checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            await checkpoint.ExecuteNonQueryAsync();
        }

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "VACUUM main INTO $dest;";
        cmd.Parameters.AddWithValue("$dest", destDbPath);
        await cmd.ExecuteNonQueryAsync();
    }
}
