using System.Text.Json;
using Dito.Storage;

namespace Dito.Gnome.History;

internal static class Program
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static async Task<int> Main()
    {
        if (!OperatingSystem.IsLinux())
        {
            return 1;
        }

        try
        {
            var data = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            if (string.IsNullOrEmpty(data) || !Path.IsPathFullyQualified(data))
            {
                data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
            }

            var directory = Path.Combine(data, "dito");
            Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            await using var history = new HistoryStore(Path.Combine(directory, "history.db"));
            await history.InitializeAsync();
            await ServeAsync(history);
            return 0;
        }
        catch (Exception)
        {
            return 1;
        }
    }

    private static async Task ServeAsync(HistoryStore history)
    {
        while (await Console.In.ReadLineAsync() is { } line)
        {
            object response;
            try
            {
                var request = JsonSerializer.Deserialize<Request>(line, Json) ?? throw new InvalidDataException();
                response = new { ok = true, result = await ExecuteAsync(history, request) };
            }
            catch (Exception)
            {
                response = new { ok = false };
            }

            await Console.Out.WriteLineAsync(JsonSerializer.Serialize(response, Json));
        }
    }

    private static async Task<object?> ExecuteAsync(HistoryStore history, Request request)
    {
        if (request.Operation == "capture" && request.Text is { Length: > 0 } text)
        {
            await history.RecordCopyAsync(text, DateTimeOffset.FromUnixTimeMilliseconds(request.CopiedAt));
            return null;
        }

        if (request.Operation == "search")
        {
            var page = await history.SearchAsync(request.Query ?? "", request.Cursor);
            return new
            {
                items = page.Items.Select(item => new { id = item.Id.ToString(), item.Preview, item.Label }),
                nextCursor = page.NextCursor?.ToString()
            };
        }

        if (request.Operation == "read" && request.Id.HasValue)
        {
            return await history.ReadTextAsync(request.Id.Value);
        }

        if (request.Operation == "delete" && request.Id is { } deleteId)
        {
            return await history.DeleteEntryAsync(deleteId);
        }

        if (request.Operation == "setLabel" && request.Id is { } labelId)
        {
            return await history.SetLabelAsync(labelId, request.Label);
        }

        throw new InvalidDataException();
    }

    private sealed record Request(
        string Operation, string? Text, long CopiedAt, string? Query, long? Cursor, long? Id, string? Label);
}
