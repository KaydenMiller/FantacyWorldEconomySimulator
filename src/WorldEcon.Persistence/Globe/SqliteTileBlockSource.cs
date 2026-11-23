using Microsoft.Data.Sqlite;
using WorldEcon.WorldGen.Tiles;

namespace WorldEcon.Persistence.Globe;

/// <summary>
/// <see cref="ITileBlockSource"/> over a database file, for on-demand tile block loads from several threads at once (the
/// app's map renderer loads visible cells on worker threads). Each <see cref="LoadAsync"/> opens its own read-only
/// connection from the Microsoft.Data.Sqlite pool, runs one parameterised SELECT on <c>tile_blocks</c> and decodes the row
/// on the calling thread; no state is shared between calls, so calls need no lock. SQLite allows concurrent readers on
/// one file. Microsoft.Data.Sqlite completes its "async" methods synchronously, so callers that must not block (the user
/// interface thread) call this from a worker thread (<c>Task.Run</c>).
/// <para>
/// The pooled connections keep the file open. Before the file is replaced or deleted, call <see cref="ClearPool"/>.
/// </para>
/// </summary>
public sealed class SqliteTileBlockSource : ITileBlockSource
{
    private readonly string _connectionString;

    public SqliteTileBlockSource(string dbPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(dbPath);
        if (!File.Exists(dbPath))
            throw new InvalidOperationException($"Database '{dbPath}' does not exist.");
        DbPath = dbPath;
        _connectionString = ConnectionString(dbPath);
    }

    public string DbPath { get; }

    /// <summary>The decoded block of <paramref name="globeCell"/>, or null when no row is stored for it.</summary>
    /// <exception cref="InvalidDataException">The row is corrupt or holds a different cell or tile count than recorded.</exception>
    public async Task<TileBlock?> LoadAsync(int globeCell, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT TileCount, Data FROM tile_blocks WHERE CellId = $cell";
        cmd.Parameters.AddWithValue("$cell", globeCell);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        var count = reader.GetInt32(0);
        if (reader.IsDBNull(1)) throw new InvalidDataException($"tile_blocks row {globeCell} has no data (NULL).");
        var data = (byte[])reader.GetValue(1);
        var block = TileBlockCodec.Decode(data, count);
        if (block.GlobeCell != globeCell || block.Count != count)
            throw new InvalidDataException(
                $"tile_blocks row {globeCell} holds cell {block.GlobeCell} with {block.Count} tiles; the row records {count}.");
        return block;
    }

    /// <summary>Closes the pooled read-only connections of <paramref name="dbPath"/> opened by this type.</summary>
    public static void ClearPool(string dbPath) => SqliteConnection.ClearPool(new SqliteConnection(ConnectionString(dbPath)));

    private static string ConnectionString(string dbPath) =>
        new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadOnly, Pooling = true }.ToString();
}
