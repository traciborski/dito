using Dito.Application;
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
    public async Task LabelsAreSearchableAndSurviveRecopyAndReopenWithoutChangingText()
    {
        await using var database = new TestDatabase();
        var now = DateTimeOffset.UtcNow;
        await database.Store.InitializeAsync();
        var address = await database.Store.RecordCopyAsync("alice@example.com", now);
        var other = await database.Store.RecordCopyAsync("work email note", now);
        Assert.True(await database.Store.SetLabelAsync(address, " Work Email "));
        Assert.True(await database.Store.SetLabelAsync(other, "Work Email"));

        var matches = (await database.Store.SearchAsync("work ema")).Items;
        Assert.Equal(new[] { other, address }, matches.Select(item => item.Id));
        Assert.Empty((await database.Store.SearchAsync("example work")).Items);
        Assert.Equal(address, await database.Store.RecordCopyAsync("alice@example.com", now));
        await database.Store.DisposeAsync();

        await using var reopened = new HistoryStore(database.Path);
        await reopened.InitializeAsync();
        var labelled = (await reopened.SearchAsync("WORK EM")).Items[0];
        Assert.Equal(address, labelled.Id);
        Assert.Equal("Work Email", labelled.Label);
        Assert.Equal("alice@example.com", await reopened.ReadTextAsync(address));

        Assert.True(await reopened.SetLabelAsync(address, "Personal"));
        Assert.Equal(other, Assert.Single((await reopened.SearchAsync("work email")).Items).Id);
        Assert.Equal(address, Assert.Single((await reopened.SearchAsync("personal")).Items).Id);
        Assert.True(await reopened.SetLabelAsync(address, " \t "));
        Assert.Empty((await reopened.SearchAsync("personal")).Items);
        Assert.Null(Assert.Single((await reopened.SearchAsync("alice")).Items).Label);
    }

    [Fact]
    public async Task DeletingRemovesBothSearchMatchesAndRecopyCreatesAnUnlabelledEntry()
    {
        await using var database = new TestDatabase();
        await database.Store.InitializeAsync();
        var now = DateTimeOffset.UtcNow;
        var id = await database.Store.RecordCopyAsync(new ClipboardContent("original", "html"u8.ToArray()), now);
        await database.Store.SetLabelAsync(id, "shortcut");
        var order = Assert.Single((await database.Store.SearchAsync("")).Items).CopyOrder;

        Assert.True(await database.Store.DeleteEntryAsync(id));
        Assert.False(await database.Store.DeleteEntryAsync(id));
        Assert.Null(await database.Store.ReadFormattedAsync(id));
        Assert.Empty((await database.Store.SearchAsync("original")).Items);
        Assert.Empty((await database.Store.SearchAsync("shortcut")).Items);
        Assert.Empty((await database.Store.SearchAsync("")).Items);

        await database.Store.DisposeAsync();
        await using var reopened = new HistoryStore(database.Path);
        await reopened.InitializeAsync();
        var replacement = await reopened.RecordCopyAsync("original", now);
        var entry = Assert.Single((await reopened.SearchAsync("original")).Items);
        Assert.NotEqual(id, replacement);
        Assert.Equal(replacement, entry.Id);
        Assert.True(entry.CopyOrder > order);
        Assert.Null(entry.Label);
        Assert.False(await reopened.SetLabelAsync(id, "stale"));
        Assert.Empty((await reopened.SearchAsync("shortcut")).Items);
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

    [Theory]
    [InlineData("")]
    [InlineData("item")]
    public async Task DeletingTheOnlyEntryOnTheOlderPageRemovesItsCursor(string query)
    {
        await using var database = new TestDatabase();
        await database.Store.InitializeAsync();
        var now = DateTimeOffset.UtcNow;
        var oldest = await database.Store.RecordCopyAsync("item oldest", now);
        for (var index = 0; index < 100; index++)
        {
            await database.Store.RecordCopyAsync($"item {index}", now);
        }

        Assert.NotNull((await database.Store.SearchAsync(query)).NextCursor);
        Assert.True(await database.Store.DeleteEntryAsync(oldest));
        var page = await database.Store.SearchAsync(query);
        Assert.Equal(100, page.Items.Count);
        Assert.Null(page.NextCursor);
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
    public async Task FormattedRecopiesReplacePreviousFormatsAndPreserveIdentityAfterReopening()
    {
        await using var database = new TestDatabase();
        await database.Store.InitializeAsync();
        var now = DateTimeOffset.UtcNow;
        var id = await database.Store.RecordCopyAsync(new ClipboardContent("original", "<b>original</b>"u8.ToArray()), now);
        var rtf = @"{\rtf1\i original}"u8.ToArray();
        Assert.Equal(id, await database.Store.RecordCopyAsync(new ClipboardContent("original", Rtf: rtf), now));
        await database.Store.DisposeAsync();

        await using var reopened = new HistoryStore(database.Path);
        await reopened.InitializeAsync();
        var saved = Assert.IsType<ClipboardContent>(await reopened.ReadFormattedAsync(id));
        Assert.Equal("original", saved.Text);
        Assert.Null(saved.Html);
        Assert.Equal(rtf, saved.Rtf);
        Assert.Equal(id, Assert.Single((await reopened.SearchAsync("original")).Items).Id);

        Assert.Equal(id, await reopened.RecordCopyAsync("original", now));
        Assert.Equal(new ClipboardContent("original"), await reopened.ReadFormattedAsync(id));
    }

    [Fact]
    public async Task MigratingVersionOneKeepsExistingEntriesSearchableAndAvailableAsPlainText()
    {
        await using var database = new TestDatabase();
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(database.Path)!);
        using (var connection = database.OpenConnection())
        {
            using var command = connection.CreateCommand();
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
                    text, content = 'clips', content_rowid = 'id', tokenize = 'unicode61', prefix = '2 3 4'
                );
                CREATE TRIGGER clips_after_insert AFTER INSERT ON clips BEGIN
                    INSERT INTO clips_fts(rowid, text) VALUES (new.id, new.text);
                END;
                INSERT INTO clips VALUES (42, 'saved history', 1000, 1000, 1);
                PRAGMA user_version = 1;
                """;
            command.ExecuteNonQuery();
        }

        await database.Store.InitializeAsync();

        Assert.Equal(42, Assert.Single((await database.Store.SearchAsync("saved hist")).Items).Id);
        Assert.Equal(new ClipboardContent("saved history"), await database.Store.ReadFormattedAsync(42));
        var html = "<b>new entry</b>"u8.ToArray();
        var id = await database.Store.RecordCopyAsync(new ClipboardContent("new entry", html), DateTimeOffset.UtcNow);
        Assert.Equal(id, Assert.Single((await database.Store.SearchAsync("new entry")).Items).Id);
        Assert.Equal(html, (await database.Store.ReadFormattedAsync(id))!.Html);
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
                PRAGMA user_version = 4;
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
