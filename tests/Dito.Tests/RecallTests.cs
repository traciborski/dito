using Dito.Application;

namespace Dito.Tests;

public sealed class RecallTests
{
    [Fact]
    public async Task CopyOnlyPreservesOriginalTextAndDoesNotReorderOrInjectInput()
    {
        await using var database = new TestDatabase();
        await database.Store.InitializeAsync();
        const string text = "  original\r\nsecond line\t ";
        var id = await database.Store.RecordCopyAsync(
            new ClipboardContent(text, "<b>original</b>"u8.ToArray()), DateTimeOffset.UtcNow);
        var newestId = await database.Store.RecordCopyAsync("newer", DateTimeOffset.UtcNow);
        var access = new FakeClipboard();
        var desktop = new FakeDesktop();
        var recall = CreateRecall(database, access, desktop);
        var dismissed = false;

        var result = await recall.RecallAsync(
            id, desktop.Target, RecallMode.CopyText, () => dismissed = true, CancellationToken.None);

        Assert.Equal(RecallResult.Copied, result);
        Assert.True(dismissed);
        Assert.Equal(text, access.WrittenText);
        Assert.Null(access.WrittenContent!.Html);
        Assert.Equal(0, desktop.PasteAttempts);
        Assert.Equal(newestId, (await database.Store.SearchAsync("")).Items[0].Id);
    }

    [Fact]
    public async Task BusyClipboardDoesNotDismissOrPasteOldContents()
    {
        await using var database = new TestDatabase();
        await database.Store.InitializeAsync();
        var id = await database.Store.RecordCopyAsync("selected", DateTimeOffset.UtcNow);
        var access = new FakeClipboard { BusyWrites = int.MaxValue };
        var desktop = new FakeDesktop();
        var dismissed = false;

        var result = await CreateRecall(database, access, desktop).RecallAsync(
            id, desktop.Target, RecallMode.PasteText, () => dismissed = true, CancellationToken.None);

        Assert.Equal(RecallResult.ClipboardBusy, result);
        Assert.False(dismissed);
        Assert.Null(access.WrittenText);
        Assert.Equal(0, desktop.PasteAttempts);
    }

    [Fact]
    public async Task MissingTargetLeavesSelectedTextReadyForManualPaste()
    {
        await using var database = new TestDatabase();
        await database.Store.InitializeAsync();
        var id = await database.Store.RecordCopyAsync("selected", DateTimeOffset.UtcNow);
        var access = new FakeClipboard();
        var desktop = new FakeDesktop();

        var result = await CreateRecall(database, access, desktop).RecallAsync(
            id, null, RecallMode.PasteFormatted, () => { }, CancellationToken.None);

        Assert.Equal(RecallResult.ManualPaste, result);
        Assert.Equal("selected", access.WrittenText);
        Assert.Null(access.WrittenContent!.Html);
        Assert.Null(access.WrittenContent.Rtf);
        Assert.Equal(0, desktop.PasteAttempts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SuccessfulPastePublishesOnlyRequestedFormatsBeforeDismissing(bool formatted)
    {
        await using var database = new TestDatabase();
        await database.Store.InitializeAsync();
        var content = new ClipboardContent("selected", "<b>selected</b>"u8.ToArray(), @"{\rtf1\b selected}"u8.ToArray());
        var id = await database.Store.RecordCopyAsync(content, DateTimeOffset.UtcNow);
        var access = new FakeClipboard();
        var desktop = new FakeDesktop();
        var dismissed = false;
        desktop.OnPaste = () =>
        {
            Assert.True(dismissed);
            var written = Assert.IsType<ClipboardContent>(access.WrittenContent);
            Assert.Equal(content.Text, written.Text);
            Assert.Equal(formatted ? content.Html : null, written.Html);
            Assert.Equal(formatted ? content.Rtf : null, written.Rtf);
        };

        var result = await CreateRecall(database, access, desktop).RecallAsync(
            id, desktop.Target, formatted ? RecallMode.PasteFormatted : RecallMode.PasteText,
            () => dismissed = true, CancellationToken.None);

        Assert.Equal(RecallResult.Pasted, result);
        Assert.Equal(1, desktop.PasteAttempts);
    }

    [Fact]
    public async Task ClipboardChangedDuringDismissalCancelsPasteWithoutOverwritingIt()
    {
        await using var database = new TestDatabase();
        await database.Store.InitializeAsync();
        var id = await database.Store.RecordCopyAsync("selected", DateTimeOffset.UtcNow);
        var access = new FakeClipboard();
        var desktop = new FakeDesktop();

        var result = await CreateRecall(database, access, desktop).RecallAsync(
            id, desktop.Target, RecallMode.PasteText, () => access.SequenceNumber++, CancellationToken.None);

        Assert.Equal(RecallResult.ClipboardChanged, result);
        Assert.Equal(1, access.WriteAttempts);
        Assert.Equal(0, desktop.PasteAttempts);
    }

    [Fact]
    public async Task LostFocusWhileWaitingForModifiersDoesNotInjectInput()
    {
        var desktop = new FakeDesktop { ModifiersReleased = false };
        var checks = 0;
        desktop.CheckForeground = () => ++checks == 1;

        var pasted = await new PasteService(desktop).TryPasteAsync(
            desktop.Target, () => true, CancellationToken.None);

        Assert.False(pasted);
        Assert.Equal(0, desktop.PasteAttempts);
    }

    [Fact]
    public async Task HeldModifiersAndUnrestorableFocusHaveBoundedFallback()
    {
        var heldModifiers = new FakeDesktop { ModifiersReleased = false };
        var noFocus = new FakeDesktop { CheckForeground = () => false };

        Assert.False(await new PasteService(heldModifiers).TryPasteAsync(
            heldModifiers.Target, () => true, CancellationToken.None));
        Assert.False(await new PasteService(noFocus).TryPasteAsync(
            noFocus.Target, () => true, CancellationToken.None));
        Assert.Equal(0, heldModifiers.PasteAttempts);
        Assert.Equal(0, noFocus.PasteAttempts);
    }

    [Fact]
    public async Task CancelledRecallCannotSendInput()
    {
        var desktop = new FakeDesktop { ModifiersReleased = false };
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new PasteService(desktop).TryPasteAsync(
                desktop.Target, () => true, cancellation.Token));
        Assert.Equal(0, desktop.PasteAttempts);
    }

    private static RecallController CreateRecall(
        TestDatabase database, FakeClipboard clipboard, FakeDesktop desktop)
    {
        return new RecallController(
            database.Store, new ClipboardTransfer(clipboard), new PasteService(desktop));
    }

    private sealed class FakeDesktop : IDesktopInput
    {
        public PasteTarget Target { get; } = new(new nint(123), 456, 789);
        public bool ModifiersReleased { get; set; } = true;
        public Func<bool> CheckForeground { get; set; } = () => true;
        public Action? OnPaste { get; set; }
        public int PasteAttempts { get; private set; }

        public PasteTarget? CaptureTarget() => Target;
        public bool IsTargetValid(PasteTarget target) => target == Target;
        public bool TryActivate(PasteTarget target) => false;
        public bool IsForeground(PasteTarget target) => CheckForeground();

        public bool TrySendPaste(PasteTarget target, Func<bool> clipboardUnchanged)
        {
            PasteAttempts++;
            OnPaste?.Invoke();
            return clipboardUnchanged();
        }
    }
}
