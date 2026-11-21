using System.Diagnostics;
using Dito.Storage;
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
        HistorySeeder.Seed(database);
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

        _output.WriteLine($"Database size after checkpoint: {new FileInfo(database.Path).Length / 1048576d:F1} MiB");
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
}
