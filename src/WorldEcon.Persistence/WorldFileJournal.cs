using Microsoft.Data.Sqlite;

namespace WorldEcon.Persistence;

/// <summary>
/// The journal mode of a world database file. The desktop app keeps world files in SQLite WAL (write-ahead logging)
/// mode: a simulation run holds one write transaction open for its whole length, and in WAL mode other connections can
/// still read the last committed state while it runs (in the default DELETE mode they would be blocked). WAL mode is
/// stored in the file, so it is switched once, when the file is created or first opened in the app.
/// </summary>
public static class WorldFileJournal
{
    // Busy timeout of 1 s: a file another program is writing to is reported as "not switched" quickly instead of
    // waiting the provider's default 30 s. Pooling off so no connection lingers on the file afterwards.
    private static string ConnectionString(string dbPath, SqliteOpenMode mode) => new SqliteConnectionStringBuilder
    {
        DataSource = dbPath,
        Mode = mode,
        Pooling = false,
        DefaultTimeout = 1,
    }.ToString();

    /// <summary>
    /// Switches <paramref name="dbPath"/> to WAL mode (a no-op for a file already in it). Returns true when the file is
    /// in WAL mode afterwards; false, without throwing, when the file does not exist or another connection holds a lock
    /// that prevents the switch.
    /// </summary>
    public static async Task<bool> TryEnableWalAsync(string dbPath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(dbPath);
        try
        {
            await using var conn = new SqliteConnection(ConnectionString(dbPath, SqliteOpenMode.ReadWrite));
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA journal_mode=WAL;";
            var mode = (string?)await cmd.ExecuteScalarAsync(ct);
            return string.Equals(mode, "wal", StringComparison.OrdinalIgnoreCase);
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    /// <summary>True when <paramref name="dbPath"/> exists and is in WAL mode.</summary>
    public static async Task<bool> IsWalAsync(string dbPath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(dbPath);
        try
        {
            await using var conn = new SqliteConnection(ConnectionString(dbPath, SqliteOpenMode.ReadOnly));
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA journal_mode;";
            var mode = (string?)await cmd.ExecuteScalarAsync(ct);
            return string.Equals(mode, "wal", StringComparison.OrdinalIgnoreCase);
        }
        catch (SqliteException)
        {
            return false;
        }
    }
}
