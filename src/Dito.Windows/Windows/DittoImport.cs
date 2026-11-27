using System.Runtime.InteropServices;
using System.Text;
using Dito.Storage;
using Microsoft.Data.Sqlite;

namespace Dito.Windows;

internal static class DittoImport
{
    private static readonly UnicodeEncoding Utf16 = new(false, false, true);

    public static bool TryParse(string[] args, out string sourcePath, out bool dryRun)
    {
        sourcePath = "";
        dryRun = args.Length == 3 && args[2] == "--dry-run";
        if ((args.Length != 2 && !dryRun) || args[0] != "--import-from-ditto" ||
            string.IsNullOrWhiteSpace(args[1]) || args[1].StartsWith("--", StringComparison.Ordinal))
        {
            return false;
        }

        sourcePath = args[1];
        return true;
    }

    public static void AttachToConsole() => AttachConsole(uint.MaxValue);

    public static async Task<HistoryImportResult> RunAsync(string sourcePath, string destinationPath, bool dryRun)
    {
        sourcePath = Path.GetFullPath(sourcePath);
        if (string.Equals(sourcePath, Path.GetFullPath(destinationPath), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The source and destination must be different files.");
        }

        await using var history = new HistoryStore(destinationPath);
        return await history.ImportAsync(ReadEntries(sourcePath), dryRun).ConfigureAwait(false);
    }

    private static IEnumerable<(string Text, DateTimeOffset CopiedAt)?> ReadEntries(string path)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            DefaultTimeout = 2,
            Pooling = false
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT clip.lDate, data.ooData
            FROM Main AS clip
            LEFT JOIN Data AS data ON data.lID = (
                SELECT format.lID FROM Data AS format
                WHERE format.lParentID = clip.lID
                    AND format.strClipBoardFormat = 'CF_UNICODETEXT' COLLATE NOCASE
                ORDER BY format.lID DESC LIMIT 1
            )
            WHERE clip.bIsGroup = 0
            ORDER BY COALESCE(clip.clipOrder, clip.lDate), clip.lID;
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            yield return ReadEntry(reader);
        }
    }

    private static (string Text, DateTimeOffset CopiedAt)? ReadEntry(SqliteDataReader reader)
    {
        if (reader.GetValue(0) is not long seconds || reader.GetValue(1) is not byte[] data)
        {
            return null;
        }

        if (seconds < DateTimeOffset.MinValue.ToUnixTimeSeconds() ||
            seconds > DateTimeOffset.MaxValue.ToUnixTimeSeconds())
        {
            return null;
        }

        for (var end = 0; end + 1 < data.Length; end += 2)
        {
            if (data[end] != 0 || data[end + 1] != 0)
            {
                continue;
            }

            try
            {
                var text = Utf16.GetString(data, 0, end).Trim();
                return text.Length == 0 ? null : (text, DateTimeOffset.FromUnixTimeSeconds(seconds));
            }
            catch (DecoderFallbackException)
            {
                return null;
            }
        }

        return null;
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(uint processId);
}
