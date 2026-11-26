using Dito.Application;

namespace Dito.Tests;

public sealed class ClipboardTransferTests
{
    [Fact]
    public async Task BusyClipboardIsRetriedWithoutChangingText()
    {
        var access = new FakeClipboard
        {
            BusyReads = 2,
            BusyWrites = 2,
            ReadResult = new ClipboardReadResult(ClipboardReadStatus.Text, new(" \r\noriginal\t "))
        };
        var clipboard = new ClipboardTransfer(access);

        var content = await clipboard.ReadAsync(1, CancellationToken.None);
        var write = await clipboard.WriteAsync(content!, CancellationToken.None);

        Assert.Equal(" \r\noriginal\t ", access.WrittenText);
        Assert.Equal(3, access.ReadAttempts);
        Assert.Equal(3, access.WriteAttempts);
        Assert.True(write.HasValue);
    }

    [Fact]
    public async Task SupersededRetryDoesNotCaptureNewClipboardContent()
    {
        var access = new FakeClipboard { BusyReads = 1 };
        access.OnRead = () => access.SequenceNumber++;
        var clipboard = new ClipboardTransfer(access);

        Assert.Null(await clipboard.ReadAsync(1, CancellationToken.None));
        Assert.Equal(1, access.ReadAttempts);
    }

    [Theory]
    [InlineData((int)ClipboardReadStatus.Ignored)]
    [InlineData((int)ClipboardReadStatus.Superseded)]
    public async Task IgnoredContentIsNotReturned(int status)
    {
        var clipboard = new ClipboardTransfer(new FakeClipboard
        {
            ReadResult = new ClipboardReadResult((ClipboardReadStatus)status, new("must not capture"))
        });

        Assert.Null(await clipboard.ReadAsync(1, CancellationToken.None));
    }

    [Fact]
    public async Task PermanentlyBusyClipboardHasBoundedRetries()
    {
        var access = new FakeClipboard { BusyReads = int.MaxValue, BusyWrites = int.MaxValue };
        var clipboard = new ClipboardTransfer(access);

        Assert.Null(await clipboard.ReadAsync(1, CancellationToken.None));
        Assert.Null(await clipboard.WriteAsync(new("text"), CancellationToken.None));
        Assert.InRange(access.ReadAttempts, 1, 10);
        Assert.InRange(access.WriteAttempts, 1, 10);
    }

    [Fact]
    public async Task PausingDuringRetryPreventsPersistence()
    {
        await using var database = new TestDatabase();
        await database.Store.InitializeAsync();
        var access = new FakeClipboard { BusyReads = 1 };
        var captures = new CaptureController(database.Store, new ClipboardTransfer(access));
        captures.Start();
        captures.ObserveClipboard();
        captures.SetPaused(true);
        await captures.StopAsync();

        Assert.Empty((await database.Store.SearchAsync("")).Items);
    }

    [Fact]
    public async Task ShutdownDrainsCapturedTextAndFormattingToDisk()
    {
        await using var database = new TestDatabase();
        await database.Store.InitializeAsync();
        var content = new ClipboardContent("copied", "<b>copied</b>"u8.ToArray(), @"{\rtf1\b copied}"u8.ToArray());
        var access = new FakeClipboard { ReadResult = new(ClipboardReadStatus.Text, content) };
        var captures = new CaptureController(database.Store, new ClipboardTransfer(access));
        captures.Start();
        captures.ObserveClipboard();
        await captures.StopAsync();

        var entry = Assert.Single((await database.Store.SearchAsync("copied")).Items);
        var saved = Assert.IsType<ClipboardContent>(await database.Store.ReadFormattedAsync(entry.Id));
        Assert.Equal(content.Text, saved.Text);
        Assert.Equal(content.Html, saved.Html);
        Assert.Equal(content.Rtf, saved.Rtf);
    }

    [Theory]
    [InlineData("\u00a0 \r\nfirst line\r\n\tsecond line \t", "first line\r\n\tsecond line")]
    [InlineData("\u00a0 \r\n\t", null)]
    [InlineData("", null)]
    public async Task CaptureTrimsPlainTextAndDiscardsFormattingWhenTextChanges(string text, string? expected)
    {
        await using var database = new TestDatabase();
        await database.Store.InitializeAsync();
        var content = new ClipboardContent(text, "<p>text</p>"u8.ToArray(), @"{\rtf1 text}"u8.ToArray());
        var access = new FakeClipboard { ReadResult = new(ClipboardReadStatus.Text, content) };
        var captures = new CaptureController(database.Store, new ClipboardTransfer(access));
        captures.Start();
        captures.ObserveClipboard();
        await captures.StopAsync();

        var entries = (await database.Store.SearchAsync("")).Items;
        if (expected is null)
        {
            Assert.Empty(entries);
        }
        else
        {
            var entry = Assert.Single(entries);
            Assert.Equal(new ClipboardContent(expected), await database.Store.ReadFormattedAsync(entry.Id));
        }
    }
}
