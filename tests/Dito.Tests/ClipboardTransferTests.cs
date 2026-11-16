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
            ReadResult = new ClipboardReadResult(ClipboardReadStatus.Text, " \r\noriginal\t ")
        };
        var clipboard = new ClipboardTransfer(access);

        var text = await clipboard.ReadAsync(1, CancellationToken.None);
        var write = await clipboard.WriteAsync(text!, CancellationToken.None);

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
            ReadResult = new ClipboardReadResult((ClipboardReadStatus)status, "must not capture")
        });

        Assert.Null(await clipboard.ReadAsync(1, CancellationToken.None));
    }

    [Fact]
    public async Task PermanentlyBusyClipboardHasBoundedRetries()
    {
        var access = new FakeClipboard { BusyReads = int.MaxValue, BusyWrites = int.MaxValue };
        var clipboard = new ClipboardTransfer(access);

        Assert.Null(await clipboard.ReadAsync(1, CancellationToken.None));
        Assert.Null(await clipboard.WriteAsync("text", CancellationToken.None));
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
    public async Task ShutdownDrainsCapturedTextToDisk()
    {
        await using var database = new TestDatabase();
        await database.Store.InitializeAsync();
        var access = new FakeClipboard();
        var captures = new CaptureController(database.Store, new ClipboardTransfer(access));
        captures.Start();
        captures.ObserveClipboard();
        await captures.StopAsync();

        Assert.Single((await database.Store.SearchAsync("copied")).Items);
    }
}
