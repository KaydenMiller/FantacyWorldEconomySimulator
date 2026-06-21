using Microsoft.Data.Sqlite;

namespace WorldEcon.Persistence.Snapshots;

/// <summary>Consistent file snapshot via SQLite `VACUUM INTO`, which runs in an implicit
/// transaction and writes a single, transactionally-consistent, committed-only, compacted copy.</summary>
public sealed class SqliteSnapshotService : ISnapshotService
{
    public async Task CaptureAsync(string sourceDbPath, string destDbPath)
    {
        // Ensure the destination directory exists.
        var destDir = Path.GetDirectoryName(destDbPath);
        if (!string.IsNullOrEmpty(destDir))
            Directory.CreateDirectory(destDir);

        // VACUUM INTO requires the target not to pre-exist. Clear pooled handles first so a
        // previously-opened dest file (held by a pooled connection) can be deleted on any OS.
        if (File.Exists(destDbPath))
        {
            SqliteConnection.ClearAllPools();
            File.Delete(destDbPath);
        }

        // Pooling=False: this is a short-lived file-level operation; pooling buys nothing and
        // risks leaving a handle that blocks later file deletes.
        await using var connection = new SqliteConnection($"Data Source={sourceDbPath};Pooling=False");
        await connection.OpenAsync();

        // VACUUM INTO alone provides the consistent copy (committed state under a read transaction).
        // No WAL checkpoint is needed: the DB uses the default DELETE journal mode, and even under
        // WAL, VACUUM INTO produces a consistent snapshot.
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "VACUUM main INTO $dest;";
        cmd.Parameters.AddWithValue("$dest", destDbPath);
        await cmd.ExecuteNonQueryAsync();
    }
}
