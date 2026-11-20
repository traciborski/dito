using System.Text;
using Dito.Application;
using Dito.Windows;

namespace Dito.Tests;

public sealed class ClipboardTransferTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void HtmlPayloadKeepsUtf8BytesButNotClipboardPadding(bool nullTerminated)
    {
        const string header = "Version:1.0\r\nEndHTML:";
        const string body = "<b>caf\u00e9</b>";
        var length = Encoding.UTF8.GetByteCount(header + "000000\r\n" + body);
        var padding = nullTerminated ? "\0unused" : "unused";
        var bytes = Encoding.UTF8.GetBytes($"{header}{length:D6}\r\n{body}{padding}");

        Assert.Equal(length, WindowsClipboard.GetHtmlLength(bytes));
        Assert.Equal(body, Encoding.UTF8.GetString(bytes.AsSpan(0, length))[^body.Length..]);
    }

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
}
