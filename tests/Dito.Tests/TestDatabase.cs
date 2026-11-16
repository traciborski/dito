using Dito.Storage;
using Microsoft.Data.Sqlite;

namespace Dito.Tests;

internal sealed class TestDatabase : IAsyncDisposable
{
    private readonly string _directory = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), "Dito.Tests", Guid.NewGuid().ToString("N"));

    public TestDatabase()
    {
        Path = System.IO.Path.Combine(_directory, "history.db");
        Store = new HistoryStore(Path);
    }

    public string Path { get; }
    public HistoryStore Store { get; }

    public SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path,
            Pooling = false
        }.ToString());
        connection.Open();
        return connection;
    }

    public async ValueTask DisposeAsync()
    {
        await Store.DisposeAsync();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
