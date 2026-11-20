using Dito.Application;

namespace Dito.Tests;

internal sealed class FakeClipboard : IClipboardAccess
{
    public uint SequenceNumber { get; set; } = 1;
    public int ReadAttempts { get; private set; }
    public int WriteAttempts { get; private set; }
    public int BusyReads { get; set; }
    public int BusyWrites { get; set; }
    public ClipboardContent? WrittenContent { get; private set; }
    public string? WrittenText => WrittenContent?.Text;
    public bool OwnWriteIsCurrent { get; set; } = true;
    public Action? OnRead { get; set; }
    public ClipboardReadResult ReadResult { get; set; } = new(ClipboardReadStatus.Text, new("copied"));

    public ClipboardReadResult Read(uint expectedSequence)
    {
        ReadAttempts++;
        OnRead?.Invoke();
        return ReadAttempts <= BusyReads
            ? new ClipboardReadResult(ClipboardReadStatus.Busy)
            : ReadResult;
    }

    public ClipboardWrite? TryWrite(ClipboardContent content)
    {
        WriteAttempts++;
        if (WriteAttempts <= BusyWrites)
        {
            return null;
        }

        WrittenContent = content;
        return new ClipboardWrite(++SequenceNumber);
    }

    public bool IsCurrentWrite(ClipboardWrite write)
    {
        return OwnWriteIsCurrent && write.Sequence == SequenceNumber;
    }
}
