namespace Dito.Application;

internal enum ClipboardReadStatus
{
    Text,
    Ignored,
    Busy,
    Superseded
}

internal sealed record ClipboardReadResult(ClipboardReadStatus Status, string? Text = null);

internal readonly record struct ClipboardWrite(uint Sequence);

internal interface IClipboardAccess
{
    uint SequenceNumber { get; }
    ClipboardReadResult ReadText(uint expectedSequence);
    ClipboardWrite? TryWriteText(string text);
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

    public async Task<string?> ReadAsync(uint sequence, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_clipboard.SequenceNumber != sequence)
            {
                return null;
            }

            var result = _clipboard.ReadText(sequence);
            if (result.Status != ClipboardReadStatus.Busy)
            {
                return result.Status == ClipboardReadStatus.Text ? result.Text : null;
            }

            await Task.Delay(RetryDelay, cancellationToken);
        }

        return null;
    }

    public async Task<ClipboardWrite?> WriteAsync(string text, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var write = _clipboard.TryWriteText(text);
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
