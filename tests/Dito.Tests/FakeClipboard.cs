using Dito.Application;

namespace Dito.Tests;

internal sealed class FakeClipboard : IClipboardAccess
{
    public uint SequenceNumber { get; set; } = 1;
    public int ReadAttempts { get; private set; }
    public int WriteAttempts { get; private set; }
    public int BusyReads { get; set; }
    public int BusyWrites { get; set; }
    public string? WrittenText { get; private set; }
    public bool OwnWriteIsCurrent { get; set; } = true;
    public Action? OnRead { get; set; }
    public ClipboardReadResult ReadResult { get; set; } = new(ClipboardReadStatus.Text, "copied");

    public ClipboardReadResult ReadText(uint expectedSequence)
    {
        ReadAttempts++;
        OnRead?.Invoke();
        return ReadAttempts <= BusyReads
            ? new ClipboardReadResult(ClipboardReadStatus.Busy)
            : ReadResult;
    }

    public ClipboardWrite? TryWriteText(string text)
    {
        WriteAttempts++;
        if (WriteAttempts <= BusyWrites)
        {
            return null;
        }

        WrittenText = text;
        return new ClipboardWrite(++SequenceNumber);
    }

    public bool IsCurrentWrite(ClipboardWrite write)
    {
        return OwnWriteIsCurrent && write.Sequence == SequenceNumber;
    }
}
