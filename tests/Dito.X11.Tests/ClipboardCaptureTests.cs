using Dito.Tests;

namespace Dito.X11.Tests;

public sealed class ClipboardCaptureTests
{
    [Fact]
    public async Task OnlyAChangedSelectionWithExternalTextIsRecorded()
    {
        await using var database = new TestDatabase();
        await database.Store.InitializeAsync();
        const string text = "caf\u00e9\r\nclipboard";
        var id = await database.Store.RecordCopyAsync(text, DateTimeOffset.UtcNow);
        await database.Store.RecordCopyAsync("newer", DateTimeOffset.UtcNow);

        string? clipboardText = null;
        var revision = 0L;
        var capture = new ClipboardCapture(database.Store, () => revision, () => Task.FromResult(clipboardText));
        capture.SetPaused(false);

        revision++;
        await capture.CaptureLatestAsync();
        Assert.Equal("newer", (await database.Store.SearchAsync("")).Items[0].Preview);

        clipboardText = "\u00a0 \r\n\t";
        revision++;
        await capture.CaptureLatestAsync();
        Assert.Equal(2, (await database.Store.SearchAsync("")).Items.Count);

        clipboardText = $"  {text}\t ";
        await capture.CaptureLatestAsync();
        Assert.Equal("newer", (await database.Store.SearchAsync("")).Items[0].Preview);

        revision++;
        await capture.CaptureLatestAsync();
        var items = (await database.Store.SearchAsync("")).Items;
        Assert.Equal(2, items.Count);
        Assert.Equal(id, items[0].Id);
        Assert.Equal(text, await database.Store.ReadTextAsync(id));
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
