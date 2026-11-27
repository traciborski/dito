using System.Text;
using Dito.Application;
using Dito.Tests;
using Microsoft.Data.Sqlite;

namespace Dito.Windows.Tests;

public sealed class DittoImportTests
{
    [Fact]
    public async Task ImportsUnicodeTextInDittoOrderAndLeavesExistingClipsUnchanged()
    {
        await using var target = new TestDatabase();
        await target.Store.InitializeAsync();
        var existingTime = DateTimeOffset.FromUnixTimeSeconds(100);
        var existingId = await target.Store.RecordCopyAsync(new ClipboardContent("existing", "HTML"u8.ToArray()), existingTime);
        await target.Store.SetLabelAsync(existingId, "keep label");
        var newerId = await target.Store.RecordCopyAsync("already newer", existingTime);
        var before = (await target.Store.SearchAsync("")).Items;

        var source = Path.Combine(Path.GetDirectoryName(target.Path)!, "Ditto.db");
        CreateDittoBackup(source);
        var sourceBytes = File.ReadAllBytes(source);
        var preview = await DittoImport.RunAsync(source, target.Path, dryRun: true);
        Assert.Equal((2, 2, 3), (preview.Added, preview.Duplicates, preview.Skipped));
        Assert.Equal(before, (await target.Store.SearchAsync("")).Items);

        var imported = await DittoImport.RunAsync(source, target.Path, dryRun: false);
        Assert.Equal(preview, imported);
        var entries = (await target.Store.SearchAsync("")).Items;
        Assert.Equal(newerId, entries[2].Id);
        Assert.Equal(before[1], entries[3]);
        Assert.Equal("HTML"u8.ToArray(), (await target.Store.ReadFormattedAsync(existingId))!.Html);
        Assert.Equal("newest \U0001f4cb\r\nsecond line", await target.Store.ReadTextAsync(entries[0].Id));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1), entries[0].LastCopiedAt);
        Assert.Equal(entries[0].Id, Assert.Single((await target.Store.SearchAsync("newest sec")).Items).Id);
        Assert.Equal("oldest", await target.Store.ReadTextAsync(entries[1].Id));
        var repeated = await DittoImport.RunAsync(source, target.Path, dryRun: false);
        Assert.Equal((0, 4, 3), (repeated.Added, repeated.Duplicates, repeated.Skipped));
        Assert.Equal(entries, (await target.Store.SearchAsync("")).Items);
        Assert.Equal(sourceBytes, File.ReadAllBytes(source));
    }

    [Fact]
    public async Task PreviewAndInvalidSourceDoNotCreateHistory()
    {
        await using var target = new TestDatabase();
        var directory = Path.GetDirectoryName(target.Path)!;
        Directory.CreateDirectory(directory);
        var source = Path.Combine(directory, "Ditto.db");
        CreateDittoBackup(source);

        var preview = await DittoImport.RunAsync(source, target.Path, dryRun: true);
        Assert.Equal((3, 1, 3), (preview.Added, preview.Duplicates, preview.Skipped));
        Assert.False(File.Exists(target.Path));
        await Assert.ThrowsAsync<InvalidDataException>(() => DittoImport.RunAsync(source, source, dryRun: false));
        await Assert.ThrowsAsync<SqliteException>(() =>
            DittoImport.RunAsync(Path.Combine(directory, "missing.db"), target.Path, dryRun: false));
        Assert.False(File.Exists(target.Path));
    }

    private static void CreateDittoBackup(string path)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Pooling = false
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE Main (lID INTEGER PRIMARY KEY, lDate INTEGER, mText TEXT,
                bIsGroup INTEGER, lParentID INTEGER, clipOrder REAL);
            CREATE TABLE Data (lID INTEGER PRIMARY KEY, lParentID INTEGER, strClipBoardFormat TEXT, ooData BLOB);
            CREATE INDEX Data_ParentId_Format ON Data(lParentID, strClipBoardFormat COLLATE NOCASE);
            INSERT INTO Main VALUES
                (1, 3, 'preview only', 1, -1, 0),
                (2, 1, 'preview only', 0, 1, 50.5),
                (3, 2, 'preview only', 0, -1, 30),
                (4, 3, 'preview only', 0, -1, 10),
                (5, 3, 'preview only', 0, -1, 40),
                (6, 3, 'preview only', 0, -1, 60),
                (7, 3, 'preview only', 0, -1, 70),
                (8, 3, 'preview only', 0, -1, 80);
            INSERT INTO Data VALUES
                (1, 1, 'CF_UNICODETEXT', $group),
                (2, 2, 'CF_UNICODETEXT', $newest),
                (3, 3, 'CF_UNICODETEXT', $duplicate),
                (4, 4, 'CF_UNICODETEXT', $oldest),
                (5, 5, 'CF_UNICODETEXT', $existing),
                (6, 6, 'CF_DIB', X'0000'),
                (7, 7, 'CF_UNICODETEXT', $blank),
                (8, 8, 'CF_UNICODETEXT', X'00D80000');
            """;
        command.Parameters.AddWithValue("$group", Encoding.Unicode.GetBytes("not a clip\0"));
        command.Parameters.AddWithValue("$newest", Encoding.Unicode.GetBytes("  newest \U0001f4cb\r\nsecond line \0padding"));
        command.Parameters.AddWithValue("$duplicate", Encoding.Unicode.GetBytes("oldest\0"));
        command.Parameters.AddWithValue("$oldest", Encoding.Unicode.GetBytes(" oldest \0"));
        command.Parameters.AddWithValue("$existing", Encoding.Unicode.GetBytes("existing\0"));
        command.Parameters.AddWithValue("$blank", Encoding.Unicode.GetBytes(" \r\n\0"));
        command.ExecuteNonQuery();
    }
}
