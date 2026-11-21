using System.Diagnostics;
using Dito.Application;
using Dito.Storage;
using Dito.UI;
using Xunit.Abstractions;

namespace Dito.Tests;

public sealed class SearchFormPerformanceTests
{
    private readonly ITestOutputHelper _output;

    public SearchFormPerformanceTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    [Trait("Category", "Performance")]
    public async Task MeasureHiddenFormSearchWithOneHundredThousandEntries()
    {
        await using var database = new TestDatabase();
        await database.Store.InitializeAsync();
        HistorySeeder.Seed(database);
        await MeasureUiAsync(database.Store);
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
