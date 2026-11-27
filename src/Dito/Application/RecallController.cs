using Dito.Storage;

namespace Dito.Application;

internal enum RecallMode
{
    CopyText,
    PasteText,
    PasteFormatted
}

internal enum RecallResult
{
    Pasted,
    Copied,
    ManualPaste,
    ClipboardBusy,
    ClipboardChanged,
    EntryMissing
}

internal sealed class RecallController
{
    private readonly HistoryStore _history;
    private readonly ClipboardTransfer _clipboard;
    private readonly PasteService _paste;

    public RecallController(HistoryStore history, ClipboardTransfer clipboard, PasteService paste)
    {
        _history = history;
        _clipboard = clipboard;
        _paste = paste;
    }

    public async Task<RecallResult> RecallAsync(
        long entryId,
        PasteTarget? target,
        RecallMode mode,
        Action dismissPopup,
        CancellationToken cancellationToken)
    {
        var content = await LoadContentAsync(entryId, mode, cancellationToken);
        if (content is null)
        {
            return RecallResult.EntryMissing;
        }

        var write = await _clipboard.WriteAsync(content, cancellationToken);
        if (!write.HasValue)
        {
            return RecallResult.ClipboardBusy;
        }

        cancellationToken.ThrowIfCancellationRequested();
        dismissPopup();
        var result = RecallResult.Copied;
        if (mode != RecallMode.CopyText)
        {
            var pasted = await _paste.TryPasteAsync(
                target, () => _clipboard.IsCurrentWrite(write.Value), cancellationToken);
            if (!pasted && !_clipboard.IsCurrentWrite(write.Value))
            {
                return RecallResult.ClipboardChanged;
            }

            result = pasted ? RecallResult.Pasted : RecallResult.ManualPaste;
        }

        await _history.PromoteEntryAsync(entryId);
        return result;
    }

    private async Task<ClipboardContent?> LoadContentAsync(
        long entryId, RecallMode mode, CancellationToken cancellationToken)
    {
        if (mode == RecallMode.PasteFormatted)
        {
            return await _history.ReadFormattedAsync(entryId, cancellationToken);
        }

        var text = await _history.ReadTextAsync(entryId, cancellationToken);
        return text is null ? null : new ClipboardContent(text);
    }
}
