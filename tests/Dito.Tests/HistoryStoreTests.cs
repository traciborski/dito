using Dito.Storage;

namespace Dito.Tests;

public sealed class HistoryStoreTests
{
    [Theory]
    [InlineData("  line one\r\nline two\n\t  ")]
    [InlineData("\r\n \t")]
    [InlineData("Za\u017c\u00f3\u0142\u0107 \ud83d\udccb e\u0301 \u00e9")]
    [InlineData("before\0after")]
    public async Task OriginalTextSurvivesPersistence(string text)
    {
        await using var database = new TestDatabase();
        await database.Store.InitializeAsync();
        var id = await database.Store.RecordCopyAsync(text, DateTimeOffset.UtcNow);
        await database.Store.DisposeAsync();

        await using var reopened = new HistoryStore(database.Path);
        await reopened.InitializeAsync();

        Assert.Equal(text, await reopened.ReadTextAsync(id));
        Assert.Single((await reopened.SearchAsync("")).Items);
    }

    [Fact]
    public async Task RecopiesPreserveIdentityAndMoveToTopDespiteClockChanges()
    {
        await using var database = new TestDatabase();
        await database.Store.InitializeAsync();
        var firstTime = DateTimeOffset.FromUnixTimeMilliseconds(2000);
        var id = await database.Store.RecordCopyAsync("original", firstTime);
        await database.Store.RecordCopyAsync("other", firstTime);

        var repeatedId = await database.Store.RecordCopyAsync(
            "original", firstTime.AddSeconds(-1));
        var page = await database.Store.SearchAsync("");

        Assert.Equal(id, repeatedId);
        Assert.Equal(2, page.Items.Count);
        Assert.Equal(id, page.Items[0].Id);
        Assert.Equal(firstTime.AddSeconds(-1), page.Items[0].LastCopiedAt);
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT first_copied_utc_ms FROM clips WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        Assert.Equal(2000L, command.ExecuteScalar());
        Assert.Single((await database.Store.SearchAsync("original")).Items);
    }

    [Fact]
    public async Task DeduplicationDoesNotNormalizeCaseWhitespaceOrUnicode()
    {
        await using var database = new TestDatabase();
        await database.Store.InitializeAsync();
        string[] texts = ["Text", "text", " text ", "\u00e9", "e\u0301"];

        foreach (var text in texts)
        {
            await database.Store.RecordCopyAsync(text, DateTimeOffset.UtcNow);
        }

        Assert.Equal(texts.Length, (await database.Store.SearchAsync("")).Items.Count);
    }

    [Theory]
    [InlineData("SERVER con", 2)]
    [InlineData("connection server", 1)]
    [InlineData("server connection", 1)]
    [InlineData("serve connection", 0)]
    [InlineData("net", 0)]
    [InlineData("cafe", 1)]
    [InlineData("", 4)]
    [InlineData("  \t ", 4)]
    public async Task SearchUsesConjunctionAndOnlyPrefixesTheLastTerm(string query, int expected)
    {
        await using var database = new TestDatabase();
        await database.Store.InitializeAsync();
        string[] texts =
        [
            "Server connection",
            "Configuration for the server",
            "internet connectivity",
            "caf\u00e9 menu"
        ];
        foreach (var text in texts)
        {
            await database.Store.RecordCopyAsync(text, DateTimeOffset.UtcNow);
        }

        Assert.Equal(expected, (await database.Store.SearchAsync(query)).Items.Count);
    }

    [Theory]
    [InlineData("AND", 1)]
    [InlineData("OR", 0)]
    [InlineData("NEAR(", 0)]
    [InlineData("\"", 0)]
    [InlineData("*", 0)]
    [InlineData("(", 0)]
    [InlineData("\" OR \"", 0)]
    [InlineData("\"quoted\"", 1)]
    [InlineData("server.connection", 1)]
    public async Task QuerySyntaxIsLiteralAndCannotInjectFtsOperators(string query, int expected)
    {
        await using var database = new TestDatabase();
        await database.Store.InitializeAsync();
        await database.Store.RecordCopyAsync("AND a quoted server connection", DateTimeOffset.UtcNow);

        Assert.Equal(expected, (await database.Store.SearchAsync(query)).Items.Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("item")]
    public async Task CursorPagesReturnEveryEntryOnceInRecencyOrder(string query)
    {
        await using var database = new TestDatabase();
        await database.Store.InitializeAsync();
        var ids = new List<long>();
        for (var index = 0; index < 205; index++)
        {
            ids.Add(await database.Store.RecordCopyAsync($"item {index}", DateTimeOffset.UtcNow));
        }

        var actualIds = new List<long>();
        long? cursor = null;
        do
        {
            var page = await database.Store.SearchAsync(query, cursor);
            Assert.InRange(page.Items.Count, 1, 100);
            actualIds.AddRange(page.Items.Select(item => item.Id));
            cursor = page.NextCursor;
        }
        while (cursor.HasValue);

        ids.Reverse();
        Assert.Equal(ids, actualIds);
    }

    [Fact]
    public async Task SearchIndexSurvivesReopening()
    {
        await using var database = new TestDatabase();
        await database.Store.InitializeAsync();
        await database.Store.RecordCopyAsync("persistent searchable text", DateTimeOffset.UtcNow);
        await database.Store.DisposeAsync();
        await using var reopened = new HistoryStore(database.Path);
        await reopened.InitializeAsync();

        Assert.Single((await reopened.SearchAsync("persistent sea")).Items);
    }

    [Fact]
    public async Task NewerSchemaIsRejectedWithoutReplacingExistingData()
    {
        await using var database = new TestDatabase();
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(database.Path)!);
        using (var connection = database.OpenConnection())
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE sentinel (value TEXT);
                INSERT INTO sentinel VALUES ('keep');
                PRAGMA user_version = 2;
                """;
            command.ExecuteNonQuery();
        }

        await Assert.ThrowsAsync<InvalidDataException>(() => database.Store.InitializeAsync());

        using var check = database.OpenConnection();
        using var read = check.CreateCommand();
        read.CommandText = "SELECT value FROM sentinel;";
        Assert.Equal("keep", read.ExecuteScalar());
    }
}
