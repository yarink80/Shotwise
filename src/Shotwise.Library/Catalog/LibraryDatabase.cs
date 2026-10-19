using Microsoft.Data.Sqlite;

namespace Shotwise.Library.Catalog;

/// <summary>
/// Single SQLite connection (WAL) guarded by an async gate. All access goes through <see cref="RunAsync{T}"/>, which
/// executes the callback on the thread pool while holding the gate (Microsoft.Data.Sqlite connections are not thread-safe).
/// </summary>
internal sealed class LibraryDatabase : IDisposable
{
    /// <summary>Highest schema version this build understands (stored in PRAGMA user_version).</summary>
    public const int CurrentSchemaVersion = 1;

    private readonly SqliteConnection _conn;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    private LibraryDatabase(SqliteConnection conn) => _conn = conn;

    /// <summary>
    /// Opens (creating if absent) and migrates the catalog. Throws <see cref="SqliteException"/> /
    /// <see cref="InvalidDataException"/> when the file is not a usable catalog (caller rebuilds).
    /// </summary>
    public static LibraryDatabase Open(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var cs = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString();
        var conn = new SqliteConnection(cs);
        try
        {
            conn.Open();
            Exec(conn, "PRAGMA journal_mode=WAL;");
            Exec(conn, "PRAGMA synchronous=NORMAL;");
            Exec(conn, "PRAGMA foreign_keys=ON;");
            Exec(conn, "PRAGMA busy_timeout=5000;");
            using (var q = conn.CreateCommand())
            {
                q.CommandText = "PRAGMA quick_check;";
                var r = q.ExecuteScalar() as string;
                if (!string.Equals(r, "ok", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Catalog integrity check failed: " + r);
            }
            Migrate(conn);
            return new LibraryDatabase(conn);
        }
        catch
        {
            conn.Dispose();
            throw;
        }
    }

    private static void Migrate(SqliteConnection conn)
    {
        var version = Convert.ToInt32(Scalar(conn, "PRAGMA user_version;"), System.Globalization.CultureInfo.InvariantCulture);
        if (version > CurrentSchemaVersion)
            throw new InvalidDataException($"Catalog schema {version} is newer than supported {CurrentSchemaVersion}.");
        // Ordered, additive migrations. Each runs in its own transaction and bumps user_version.
        for (var v = version + 1; v <= CurrentSchemaVersion; v++)
        {
            using var tx = conn.BeginTransaction();
            foreach (var stmt in Migrations[v - 1]) Exec(conn, stmt, tx);
            Exec(conn, $"PRAGMA user_version={v};", tx);
            tx.Commit();
        }
    }

    private static readonly string[][] Migrations =
    {
        // v1
        new[]
        {
            @"CREATE TABLE items(
                rid INTEGER PRIMARY KEY AUTOINCREMENT,
                id TEXT NOT NULL UNIQUE,
                rel TEXT NOT NULL UNIQUE COLLATE NOCASE,
                kind INTEGER NOT NULL,
                name TEXT NOT NULL,
                created INTEGER NOT NULL,
                modified INTEGER NOT NULL,
                size INTEGER NOT NULL,
                mtime_ticks INTEGER NOT NULL,
                width INTEGER NOT NULL DEFAULT 0,
                height INTEGER NOT NULL DEFAULT 0,
                duration_ms INTEGER,
                app TEXT, window TEXT, url TEXT, website TEXT, ocr TEXT, callouts TEXT,
                is_fav INTEGER NOT NULL DEFAULT 0,
                is_step INTEGER NOT NULL DEFAULT 0,
                has_thumb INTEGER NOT NULL DEFAULT 0)",
            "CREATE INDEX ix_items_created ON items(created)",
            "CREATE INDEX ix_items_app ON items(app)",
            "CREATE INDEX ix_items_website ON items(website)",
            "CREATE INDEX ix_items_kind ON items(kind)",
            @"CREATE TABLE tags(
                item_id TEXT NOT NULL,
                tag TEXT NOT NULL COLLATE NOCASE,
                PRIMARY KEY(item_id, tag)) WITHOUT ROWID",
            "CREATE INDEX ix_tags_tag ON tags(tag)",
            @"CREATE TABLE share_history(
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                item_id TEXT,
                at INTEGER NOT NULL,
                dest_id TEXT NOT NULL,
                dest_name TEXT NOT NULL,
                link TEXT, title TEXT)",
            "CREATE INDEX ix_share_item ON share_history(item_id)",
            "CREATE VIRTUAL TABLE items_fts USING fts5(name, app, window, url, ocr, callouts, tags, tokenize='unicode61 remove_diacritics 2')",
        },
    };

    public async Task<T> RunAsync<T>(Func<SqliteConnection, T> work, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return await Task.Run(() => work(_conn), ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public Task RunAsync(Action<SqliteConnection> work, CancellationToken ct = default)
        => RunAsync<object?>(c => { work(c); return null; }, ct);

    public void Dispose()
    {
        _gate.Wait();
        try
        {
            if (_disposed) return;
            _disposed = true;
            try { Exec(_conn, "PRAGMA wal_checkpoint(TRUNCATE);"); } catch (SqliteException) { }
            _conn.Dispose();
        }
        finally { _gate.Release(); }
    }

    // ---- small helpers used throughout the library ----

    public static void Exec(SqliteConnection c, string sql, SqliteTransaction? tx = null, params (string, object?)[] args)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public static object? Scalar(SqliteConnection c, string sql, SqliteTransaction? tx = null, params (string, object?)[] args)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        var r = cmd.ExecuteScalar();
        return r is DBNull ? null : r;
    }
}
