using System.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace WorldEcon.Persistence.Globe;

/// <summary>
/// A <see cref="SqliteTransaction"/> for raw bulk commands on a <see cref="WorldDbContext"/>'s connection. When the
/// context has an active EF Core (Entity Framework Core) transaction (<c>db.Database.CurrentTransaction</c>) the commands join it, and the
/// caller of that transaction decides commit or rollback; otherwise a new transaction is begun and owned here.
/// Microsoft.Data.Sqlite rejects nested transactions, so beginning a second one while EF Core's is active throws.
/// </summary>
public sealed class SqliteAmbientTransaction : IAsyncDisposable
{
    private readonly bool _owned;
    private readonly bool _openedConnection;

    private SqliteAmbientTransaction(SqliteConnection connection, SqliteTransaction transaction, bool owned, bool openedConnection)
    {
        Connection = connection;
        Transaction = transaction;
        _owned = owned;
        _openedConnection = openedConnection;
    }

    public SqliteConnection Connection { get; }

    public SqliteTransaction Transaction { get; }

    /// <summary>Joins the context's active transaction, or opens the connection if needed and begins an owned one.</summary>
    public static async Task<SqliteAmbientTransaction> JoinOrBeginAsync(WorldDbContext db, CancellationToken ct = default)
    {
        var conn = (SqliteConnection)db.Database.GetDbConnection();
        if (db.Database.CurrentTransaction?.GetDbTransaction() is SqliteTransaction ambient)
            return new SqliteAmbientTransaction(conn, ambient, owned: false, openedConnection: false);

        var opened = conn.State != ConnectionState.Open;
        if (opened)
            await conn.OpenAsync(ct);
        var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);
        return new SqliteAmbientTransaction(conn, tx, owned: true, openedConnection: opened);
    }

    /// <summary>Commits an owned transaction; does nothing when joined to the context's transaction.</summary>
    public async Task CommitAsync(CancellationToken ct = default)
    {
        if (_owned)
            await Transaction.CommitAsync(ct);
    }

    /// <summary>Disposes an owned transaction (rolling it back if not committed) and closes a connection opened here.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_owned)
            await Transaction.DisposeAsync();
        if (_openedConnection)
            await Connection.CloseAsync();
    }
}
