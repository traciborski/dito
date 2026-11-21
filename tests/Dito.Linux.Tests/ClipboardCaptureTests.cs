using Dito.Tests;

namespace Dito.Linux.Tests;

public sealed class ClipboardCaptureTests
{
    [Fact]
    public async Task OnlyAChangedSelectionWithExternalTextIsRecorded()
    {
        await using var database = new TestDatabase();
        await database.Store.InitializeAsync();
        const string text = "  caf\u00e9\r\nclipboard\t ";
        await database.Store.RecordCopyAsync(text, DateTimeOffset.UtcNow);
        await database.Store.RecordCopyAsync("newer", DateTimeOffset.UtcNow);

        string? clipboardText = null;
        var revision = 0L;
        var capture = new ClipboardCapture(database.Store, () => revision, () => Task.FromResult(clipboardText));
        capture.SetPaused(false);

        revision++;
        await capture.CaptureLatestAsync();
        Assert.Equal("newer", (await database.Store.SearchAsync("")).Items[0].Preview);

        clipboardText = text;
        await capture.CaptureLatestAsync();
        Assert.Equal("newer", (await database.Store.SearchAsync("")).Items[0].Preview);

        revision++;
        await capture.CaptureLatestAsync();
        var items = (await database.Store.SearchAsync("")).Items;
        Assert.Equal(2, items.Count);
        Assert.Equal(text, await database.Store.ReadTextAsync(items[0].Id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChangingSelectionOrPausingDuringReadDiscardsTheResult(bool pause)
    {
        await using var database = new TestDatabase();
        await database.Store.InitializeAsync();
        var read = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var revision = 0L;
        var capture = new ClipboardCapture(database.Store, () => revision, () => read.Task);
        capture.SetPaused(false);
        revision++;
        var pending = capture.CaptureLatestAsync();

        if (pause)
        {
            capture.SetPaused(true);
        }

        revision++;
        read.SetResult("stale");
        await pending;
        if (pause)
        {
            capture.SetPaused(false);
            await capture.CaptureLatestAsync();
        }

        Assert.Empty((await database.Store.SearchAsync("")).Items);
    }
}
