using Dito.Application;
using Microsoft.Data.Sqlite;

namespace Dito.Storage;

internal sealed class HistoryStore : IAsyncDisposable
{
    private const int PageSize = 100;
    private const int SchemaVersion = 3;

    private readonly string _databasePath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SqliteConnection? _connection;
    private bool _disposed;

    public HistoryStore(string databasePath)
    {
        _databasePath = Path.GetFullPath(databasePath);
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        return RunAsync(Initialize, cancellationToken);
    }

    public Task<long> RecordCopyAsync(
        string text,
        DateTimeOffset copiedAt,
        CancellationToken cancellationToken = default)
    {
        return RecordCopyAsync(new ClipboardContent(text), copiedAt, cancellationToken);
    }

    public Task<long> RecordCopyAsync(
        ClipboardContent content,
        DateTimeOffset copiedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(content.Text);
        return RunAsync(() => RecordCopy(content, copiedAt), cancellationToken);
    }

    public Task<HistoryPage> SearchAsync(
        string query,
        long? beforeCopyOrder = null,
        CancellationToken cancellationToken = default)
    {
        var match = FtsQuery.Build(query);
        return RunAsync(() => ReadPage(match, beforeCopyOrder), cancellationToken);
    }

    public Task<string?> ReadTextAsync(long id, CancellationToken cancellationToken = default)
    {
        return RunAsync(() => ReadText(id), cancellationToken);
    }

    public Task<bool> DeleteEntryAsync(long id) =>
        RunAsync(() => DeleteEntry(id), CancellationToken.None);

    public Task<bool> SetLabelAsync(long id, string? label) =>
        RunAsync(() => SetLabel(id, label), CancellationToken.None);

    public Task<ClipboardContent?> ReadFormattedAsync(long id, CancellationToken cancellationToken = default)
    {
        return RunAsync(() => ReadFormatted(id), cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            await Task.Run(() => _connection?.Dispose()).ConfigureAwait(false);
            _connection = null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<T> RunAsync<T>(Func<T> operation, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return await Task.Run(operation, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private bool Initialize()
    {
        if (_connection is not null)
        {
            return true;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            DefaultTimeout = 2,
            Pooling = false
        }.ToString());

        try
        {
            connection.Open();
            ConfigureConnection(connection);
            Migrate(connection);
            _connection = connection;
            return true;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static void ConfigureConnection(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = FULL;
            PRAGMA busy_timeout = 2000;
            """;
        command.ExecuteNonQuery();
    }

    private static void Migrate(SqliteConnection connection)
    {
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA user_version;";
        var version = (long)command.ExecuteScalar()!;

        if (version > SchemaVersion)
        {
            throw new InvalidDataException("This database requires a newer version of Dito.");
        }

        if (version == 0)
        {
            command.CommandText = """
                CREATE TABLE clips (
                    id INTEGER PRIMARY KEY,
                    text TEXT NOT NULL COLLATE BINARY UNIQUE,
                    first_copied_utc_ms INTEGER NOT NULL,
                    last_copied_utc_ms INTEGER NOT NULL,
                    copy_order INTEGER NOT NULL
                );

                CREATE UNIQUE INDEX clips_recent ON clips(copy_order);

                CREATE VIRTUAL TABLE clips_fts USING fts5(
                    text,
                    content = 'clips',
                    content_rowid = 'id',
                    tokenize = 'unicode61',
                    prefix = '2 3 4'
                );

                CREATE TRIGGER clips_after_insert AFTER INSERT ON clips BEGIN
                    INSERT INTO clips_fts(rowid, text) VALUES (new.id, new.text);
                END;

                PRAGMA user_version = 1;
                """;
            command.ExecuteNonQuery();
        }

        if (version < 2)
        {
            command.CommandText = """
                ALTER TABLE clips ADD COLUMN html BLOB;
                ALTER TABLE clips ADD COLUMN rtf BLOB;
                PRAGMA user_version = 2;
                """;
            command.ExecuteNonQuery();
        }

        if (version < 3)
        {
            command.CommandText = """
                ALTER TABLE clips ADD COLUMN label TEXT;

                CREATE TABLE history_sequence (value INTEGER NOT NULL);
                INSERT INTO history_sequence
                    SELECT MAX(COALESCE(MAX(id), 0), COALESCE(MAX(copy_order), 0)) FROM clips;

                CREATE VIRTUAL TABLE labels_fts USING fts5(
                    label, tokenize = 'unicode61', prefix = '2 3 4'
                );

                CREATE TRIGGER clips_sequence_after_insert AFTER INSERT ON clips BEGIN
                    UPDATE history_sequence SET value = new.copy_order;
                END;
                CREATE TRIGGER clips_sequence_after_recopy AFTER UPDATE OF copy_order ON clips BEGIN
                    UPDATE history_sequence SET value = new.copy_order;
                END;
                CREATE TRIGGER clips_after_label_change AFTER UPDATE OF label ON clips BEGIN
                    DELETE FROM labels_fts WHERE rowid = old.id;
                    INSERT INTO labels_fts(rowid, label) SELECT new.id, new.label WHERE new.label IS NOT NULL;
                END;
                CREATE TRIGGER clips_after_delete AFTER DELETE ON clips BEGIN
                    INSERT INTO clips_fts(clips_fts, rowid, text) VALUES ('delete', old.id, old.text);
                    DELETE FROM labels_fts WHERE rowid = old.id;
                END;

                PRAGMA user_version = 3;
                """;
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    private long RecordCopy(ClipboardContent content, DateTimeOffset copiedAt)
    {
        using var command = Connection.CreateCommand();
        command.CommandText = """
            INSERT INTO clips (
                id, text, first_copied_utc_ms, last_copied_utc_ms, copy_order, html, rtf
            )
            SELECT value + 1, $text, $now, $now, value + 1, $html, $rtf
            FROM history_sequence WHERE true
            ON CONFLICT(text) DO UPDATE SET
                last_copied_utc_ms = excluded.last_copied_utc_ms,
                copy_order = excluded.copy_order,
                html = excluded.html,
                rtf = excluded.rtf
            RETURNING id;
            """;
        command.Parameters.AddWithValue("$text", content.Text);
        command.Parameters.AddWithValue("$now", copiedAt.ToUnixTimeMilliseconds());
        command.Parameters.Add("$html", SqliteType.Blob).Value = (object?)content.Html ?? DBNull.Value;
        command.Parameters.Add("$rtf", SqliteType.Blob).Value = (object?)content.Rtf ?? DBNull.Value;
        return (long)command.ExecuteScalar()!;
    }

    private bool DeleteEntry(long id)
    {
        using var command = Connection.CreateCommand();
        command.CommandText = "DELETE FROM clips WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteNonQuery() != 0;
    }

    private bool SetLabel(long id, string? label)
    {
        label = label?.Trim();
        using var command = Connection.CreateCommand();
        command.CommandText = "UPDATE clips SET label = $label WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.Add("$label", SqliteType.Text).Value =
            string.IsNullOrEmpty(label) ? DBNull.Value : label;
        return command.ExecuteNonQuery() != 0;
    }

    private HistoryPage ReadPage(string match, long? beforeCopyOrder)
    {
        using var command = CreatePageCommand(match, beforeCopyOrder);
        using var reader = command.ExecuteReader();
        var items = new List<HistoryListItem>();

        while (reader.Read())
        {
            if (items.Count == PageSize)
            {
                return new HistoryPage(items, items[^1].CopyOrder);
            }

            items.Add(new HistoryListItem(
                reader.GetInt64(0),
                reader.GetInt64(1),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2)),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4)));
        }

        return new HistoryPage(items, null);
    }

    private SqliteCommand CreatePageCommand(string match, long? beforeCopyOrder)
    {
        var command = Connection.CreateCommand();
        var source = "clips";
        var conditions = new List<string>();

        if (match.Length > 0)
        {
            // Avoid fetching every matching text before sorting a broad prefix search.
            source = "clips INDEXED BY clips_recent";
            conditions.Add("""
                clips.id IN (
                    SELECT rowid FROM clips_fts WHERE clips_fts MATCH $match
                    UNION ALL
                    SELECT rowid FROM labels_fts WHERE labels_fts MATCH $match
                )
                """);
            command.Parameters.AddWithValue("$match", match);
        }

        if (beforeCopyOrder.HasValue)
        {
            conditions.Add("clips.copy_order < $before");
            command.Parameters.AddWithValue("$before", beforeCopyOrder.Value);
        }

        var filter = conditions.Count == 0 ? "" : "WHERE " + string.Join(" AND ", conditions);
        command.CommandText = $"""
            SELECT clips.id,
                   clips.copy_order,
                   clips.last_copied_utc_ms,
                   substr(clips.text, 1, 240),
                   clips.label
            FROM {source}
            {filter}
            ORDER BY clips.copy_order DESC
            LIMIT $pageSize;
            """;
        command.Parameters.AddWithValue("$pageSize", PageSize + 1);
        return command;
    }

    private string? ReadText(long id)
    {
        using var command = Connection.CreateCommand();
        command.CommandText = "SELECT text FROM clips WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteScalar() as string;
    }

    private ClipboardContent? ReadFormatted(long id)
    {
        using var command = Connection.CreateCommand();
        command.CommandText = "SELECT text, html, rtf FROM clips WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new ClipboardContent(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetFieldValue<byte[]>(1),
                reader.IsDBNull(2) ? null : reader.GetFieldValue<byte[]>(2))
            : null;
    }

    private SqliteConnection Connection =>
        _connection ?? throw new InvalidOperationException("History has not been initialized.");
}
