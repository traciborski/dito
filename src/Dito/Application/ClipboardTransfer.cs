namespace Dito.Application;

internal enum ClipboardReadStatus
{
    Text,
    Ignored,
    Busy,
    Superseded
}

internal sealed record ClipboardContent(string Text, byte[]? Html = null, byte[]? Rtf = null);

internal sealed record ClipboardReadResult(ClipboardReadStatus Status, ClipboardContent? Content = null);

internal readonly record struct ClipboardWrite(uint Sequence);

internal interface IClipboardAccess
{
    uint SequenceNumber { get; }
    ClipboardReadResult Read(uint expectedSequence);
    ClipboardWrite? TryWrite(ClipboardContent content);
    bool IsCurrentWrite(ClipboardWrite write);
}

internal sealed class ClipboardTransfer
{
    private const int Attempts = 8;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(25);
    private readonly IClipboardAccess _clipboard;

    public ClipboardTransfer(IClipboardAccess clipboard)
    {
        _clipboard = clipboard;
    }

    public uint SequenceNumber => _clipboard.SequenceNumber;

    public async Task<ClipboardContent?> ReadAsync(uint sequence, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_clipboard.SequenceNumber != sequence)
            {
                return null;
            }

            var result = _clipboard.Read(sequence);
            if (result.Status != ClipboardReadStatus.Busy)
            {
                return result.Status == ClipboardReadStatus.Text ? result.Content : null;
            }

            await Task.Delay(RetryDelay, cancellationToken);
        }

        return null;
    }

    public async Task<ClipboardWrite?> WriteAsync(ClipboardContent content, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var write = _clipboard.TryWrite(content);
            if (write.HasValue)
            {
                return write;
            }

            await Task.Delay(RetryDelay, cancellationToken);
        }

        return null;
    }

    public bool IsCurrentWrite(ClipboardWrite write)
    {
        return _clipboard.IsCurrentWrite(write);
    }
}
