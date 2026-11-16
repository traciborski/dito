using Dito.Application;

namespace Dito.Tests;

public sealed class HistorySearchTests
{
    [Fact]
    public async Task SupersededSearchDoesNotPublishOldResults()
    {
        await using var database = new TestDatabase();
        await database.Store.InitializeAsync();
        await database.Store.RecordCopyAsync("old result", DateTimeOffset.UtcNow);
        var newId = await database.Store.RecordCopyAsync("new result", DateTimeOffset.UtcNow);
        using var search = new HistorySearch(database.Store);
        var completed = new TaskCompletionSource<SearchResults>(TaskCreationOptions.RunContinuationsAsynchronously);
        var observed = new List<SearchResults>();
        search.ResultsChanged += results =>
        {
            observed.Add(results);
            completed.TrySetResult(results);
        };

        search.Search("old");
        search.Search("new", debounce: false);
        var results = await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(newId, Assert.Single(results.Items).Id);
        Assert.Single(observed);
    }

    [Fact]
    public async Task SupersededPreviewCannotReplaceCurrentSelection()
    {
        await using var database = new TestDatabase();
        await database.Store.InitializeAsync();
        var oldId = await database.Store.RecordCopyAsync("old original", DateTimeOffset.UtcNow);
        var newId = await database.Store.RecordCopyAsync("new original\r\n ", DateTimeOffset.UtcNow);
        using var search = new HistorySearch(database.Store);
        var completed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        search.PreviewChanged += text =>
        {
            if (text.Length > 0)
            {
                completed.TrySetResult(text);
            }
        };

        search.Select(oldId);
        search.Select(newId);

        Assert.Equal("new original\r\n ", await completed.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task PageNavigationKeepsOnlyTheRequestedPageAndCanReturnToNewest()
    {
        await using var database = new TestDatabase();
        await database.Store.InitializeAsync();
        for (var index = 0; index < 105; index++)
        {
            await database.Store.RecordCopyAsync($"entry {index}", DateTimeOffset.UtcNow);
        }

        using var search = new HistorySearch(database.Store);
        var firstPage = await WaitForPageAsync(search, () => search.Search("", debounce: false));
        var olderPage = await WaitForPageAsync(search, search.LoadOlder);
        var newestAgain = await WaitForPageAsync(search, search.LoadNewer);

        Assert.Equal(100, firstPage.Items.Count);
        Assert.False(firstPage.CanLoadNewer);
        Assert.True(firstPage.CanLoadOlder);
        Assert.Equal(5, olderPage.Items.Count);
        Assert.True(olderPage.CanLoadNewer);
        Assert.False(olderPage.CanLoadOlder);
        Assert.Equal(firstPage.Items.Select(item => item.Id), newestAgain.Items.Select(item => item.Id));
    }

    private static async Task<SearchResults> WaitForPageAsync(HistorySearch search, Action request)
    {
        var completion = new TaskCompletionSource<SearchResults>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Complete(SearchResults results) => completion.TrySetResult(results);
        search.ResultsChanged += Complete;
        try
        {
            request();
            return await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            search.ResultsChanged -= Complete;
        }
    }
}
