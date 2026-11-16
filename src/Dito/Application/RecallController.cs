using Dito.Storage;

namespace Dito.Application;

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
        bool paste,
        Action dismissPopup,
        CancellationToken cancellationToken)
    {
        var text = await _history.ReadTextAsync(entryId, cancellationToken);
        if (text is null)
        {
            return RecallResult.EntryMissing;
        }

        var write = await _clipboard.WriteAsync(text, cancellationToken);
        if (!write.HasValue)
        {
            return RecallResult.ClipboardBusy;
        }

        cancellationToken.ThrowIfCancellationRequested();
        dismissPopup();
        if (!paste)
        {
            return RecallResult.Copied;
        }

        var pasted = await _paste.TryPasteAsync(
            target, () => _clipboard.IsCurrentWrite(write.Value), cancellationToken);
        if (pasted)
        {
            return RecallResult.Pasted;
        }

        return _clipboard.IsCurrentWrite(write.Value)
            ? RecallResult.ManualPaste
            : RecallResult.ClipboardChanged;
    }
}
