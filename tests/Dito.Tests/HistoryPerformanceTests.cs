using System.Diagnostics;
using Dito.Application;
using Dito.Storage;
using Dito.UI;
using Xunit.Abstractions;

namespace Dito.Tests;

public sealed class HistoryPerformanceTests
{
    private readonly ITestOutputHelper _output;

    public HistoryPerformanceTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    [Trait("Category", "Performance")]
    public async Task MeasureOneHundredThousandEntryWorkflow()
    {
        await using var database = new TestDatabase();
        await database.Store.InitializeAsync();
        var seed = Stopwatch.StartNew();
        SeedHistory(database);
        _output.WriteLine($"Seed 100,000 unique entries: {seed.Elapsed.TotalSeconds:F2} s");
        ReportDataShape(database);

        await MeasureAsync("Insert into 100k history", 30, async index =>
            await database.Store.RecordCopyAsync($"new measured capture {index}", DateTimeOffset.UtcNow));
        await MeasureAsync("Recopy in 100k history", 30, async _ =>
            await database.Store.RecordCopyAsync("new measured capture 0", DateTimeOffset.UtcNow));
        await database.Store.DisposeAsync();

        await using var reopened = new HistoryStore(database.Path);
        var reopen = Stopwatch.StartNew();
        await reopened.InitializeAsync();
        _output.WriteLine($"Reopen: {reopen.Elapsed.TotalMilliseconds:F2} ms");
        foreach (var query in new[] { "", "server con", "project12", "cafe", "c", "missingterm" })
        {
            await MeasureAsync($"First page [{query}]", 15, async _ =>
                await reopened.SearchAsync(query));
        }

        await MeasureUiAsync(reopened);
        _output.WriteLine($"Database size after checkpoint: {new FileInfo(database.Path).Length / 1048576d:F1} MiB");
    }

    private static void SeedHistory(TestDatabase database)
    {
        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO clips (text, first_copied_utc_ms, last_copied_utc_ms, copy_order)
            VALUES ($text, $time, $time, $order);
            """;
        var text = command.Parameters.Add("$text", Microsoft.Data.Sqlite.SqliteType.Text);
        command.Parameters.AddWithValue("$time", 1_790_000_000_000L);
        var order = command.Parameters.Add("$order", Microsoft.Data.Sqlite.SqliteType.Integer);
        command.Prepare();

        for (var index = 0; index < 100_000; index++)
        {
            text.Value = CreateText(index);
            order.Value = index + 1;
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    private static string CreateText(int index)
    {
        var header = $"project{index % 100} item {index}: ";
        return header + ((index % 5) switch
        {
            0 => $"server connection configuration\r\nPort = {1000 + index % 5000};\r\n" +
                 string.Concat(Enumerable.Repeat($"setting{index % 31}=enabled; ", index % 40 + 1)),
            1 => $"SELECT customer_id, total FROM invoices WHERE id = {index};\n" +
                 string.Concat(Enumerable.Repeat("ORDER BY created_at DESC;\n", index % 25 + 1)),
            2 => $"# Notes for task {index}\n- Review implementation\n- Check cache invalidation\n" +
                 string.Concat(Enumerable.Repeat("A representative paragraph with words and whitespace. ", index % 30 + 1)),
            3 => $"caf\u00e9 Za\u017c\u00f3\u0142\u0107 \ud83d\udccb\nException: operation {index} failed\n" +
                 string.Concat(Enumerable.Repeat("at Example.Worker.Process(input)\r\n", index % 20 + 1)),
            _ => $"{{ \"id\": {index}, \"state\": \"pending\", \"path\": \"C:\\\\work\\\\project\" }}\n" +
                 string.Concat(Enumerable.Repeat("alpha beta gamma delta ", index % 50 + 1))
        });
    }

    private void ReportDataShape(TestDatabase database)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*), MIN(length(text)), AVG(length(text)), MAX(length(text)) FROM clips;";
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(100_000, reader.GetInt64(0));
        _output.WriteLine(
            $"Text characters min/average/max: {reader.GetInt64(1)}/{reader.GetDouble(2):F0}/{reader.GetInt64(3)}");
    }

    private async Task MeasureAsync(string label, int samples, Func<int, Task> action)
    {
        var measurements = new List<double>();
        for (var index = 0; index < samples; index++)
        {
            var elapsed = Stopwatch.StartNew();
            await action(index);
            measurements.Add(elapsed.Elapsed.TotalMilliseconds);
        }

        measurements.Sort();
        _output.WriteLine(
            $"{label}: median {measurements[samples / 2]:F2} ms; p95 {measurements[(int)Math.Ceiling(samples * .95) - 1]:F2} ms");
    }

    private async Task MeasureUiAsync(HistoryStore history)
    {
        await WinFormsTest.RunAsync(async () =>
        {
            using var search = new HistorySearch(history);
            using var form = new SearchForm(search);
            _ = form.Handle;
            using var image = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(image, new Rectangle(Point.Empty, form.Size));
            using var timer = new System.Windows.Forms.Timer { Interval = 15 };
            var elapsed = Stopwatch.StartNew();
            var lastTick = elapsed.Elapsed.TotalMilliseconds;
            var maxGap = 0d;
            var ticks = 0;
            timer.Tick += (_, _) =>
            {
                var now = elapsed.Elapsed.TotalMilliseconds;
                maxGap = Math.Max(maxGap, now - lastTick);
                lastTick = now;
                ticks++;
            };
            timer.Start();

            var iterations = 0;
            while (iterations < 40 || elapsed.Elapsed < TimeSpan.FromSeconds(1))
            {
                var completion = new TaskCompletionSource<SearchResults>(TaskCreationOptions.RunContinuationsAsynchronously);
                var preview = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                void Complete(SearchResults results) => completion.TrySetResult(results);
                void CompletePreview(string text)
                {
                    if (text.Length > 0)
                    {
                        preview.TrySetResult();
                    }
                }

                search.ResultsChanged += Complete;
                search.PreviewChanged += CompletePreview;
                try
                {
                    search.Search(iterations % 2 == 0 ? "c" : "server con", debounce: false);
                    var page = await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    Assert.InRange(page.Items.Count, 1, 100);
                    await preview.Task.WaitAsync(TimeSpan.FromSeconds(5));
                }
                finally
                {
                    search.ResultsChanged -= Complete;
                    search.PreviewChanged -= CompletePreview;
                }

                iterations++;
            }

            var ticksDuringSearch = ticks;
            await Task.Delay(30);
            timer.Stop();
            Assert.True(ticksDuringSearch > 0, "The UI message loop did not process timer messages during searches.");
            _output.WriteLine(
                $"Hidden WinForms search/preview: {iterations} searches, {ticks} UI timer ticks, max timer gap {maxGap:F2} ms");
            Assert.False(form.Visible);
        });
    }
}
