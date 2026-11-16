using System.ComponentModel;
using System.Runtime.InteropServices;
using Dito.Application;

namespace Dito.Windows;

internal sealed class WindowsClipboard : IClipboardAccess
{
    private readonly nint _owner;
    private readonly uint _excludeFormat;
    private ClipboardWrite? _lastWrite;

    public WindowsClipboard(nint owner)
    {
        _owner = owner;
        _excludeFormat = NativeMethods.RegisterClipboardFormat(
            "ExcludeClipboardContentFromMonitorProcessing");
        if (_excludeFormat == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    public uint SequenceNumber => NativeMethods.GetClipboardSequenceNumber();

    public ClipboardReadResult ReadText(uint expectedSequence)
    {
        if (SequenceNumber != expectedSequence)
        {
            return new ClipboardReadResult(ClipboardReadStatus.Superseded);
        }

        if (!NativeMethods.OpenClipboard(_owner))
        {
            return new ClipboardReadResult(ClipboardReadStatus.Busy);
        }

        try
        {
            return ReadOpenClipboard(expectedSequence);
        }
        finally
        {
            NativeMethods.CloseClipboard();
        }
    }

    public ClipboardWrite? TryWriteText(string text)
    {
        var memory = AllocateText(text);
        try
        {
            if (!NativeMethods.OpenClipboard(_owner))
            {
                return null;
            }

            try
            {
                if (!NativeMethods.EmptyClipboard() ||
                    NativeMethods.SetClipboardData(NativeMethods.UnicodeText, memory) == 0)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                memory = 0; // Windows owns the allocation after SetClipboardData succeeds.
            }
            finally
            {
                NativeMethods.CloseClipboard();
            }

            _lastWrite = new ClipboardWrite(SequenceNumber);
            return _lastWrite;
        }
        finally
        {
            if (memory != 0)
            {
                NativeMethods.GlobalFree(memory);
            }
        }
    }

    public bool IsCurrentWrite(ClipboardWrite write)
    {
        return SequenceNumber == write.Sequence && NativeMethods.GetClipboardOwner() == _owner;
    }

    private ClipboardReadResult ReadOpenClipboard(uint expectedSequence)
    {
        if (SequenceNumber != expectedSequence)
        {
            return new ClipboardReadResult(ClipboardReadStatus.Superseded);
        }

        if (NativeMethods.IsClipboardFormatAvailable(_excludeFormat) ||
            (_lastWrite.HasValue && IsCurrentWrite(_lastWrite.Value)) ||
            !NativeMethods.IsClipboardFormatAvailable(NativeMethods.UnicodeText))
        {
            return new ClipboardReadResult(ClipboardReadStatus.Ignored);
        }

        var text = ReadUnicodeText();
        return new ClipboardReadResult(ClipboardReadStatus.Text, text);
    }

    private static string ReadUnicodeText()
    {
        var memory = NativeMethods.GetClipboardData(NativeMethods.UnicodeText);
        if (memory == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        var length = checked((int)(NativeMethods.GlobalSize(memory) / sizeof(char)));
        if (length == 0)
        {
            return "";
        }

        var pointer = NativeMethods.GlobalLock(memory);
        if (pointer == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        try
        {
            var text = Marshal.PtrToStringUni(pointer, length)!;
            var terminator = text.IndexOf('\0');
            return terminator < 0 ? text : text[..terminator];
        }
        finally
        {
            NativeMethods.GlobalUnlock(memory);
        }
    }

    private static nint AllocateText(string text)
    {
        var bytes = checked((nuint)(text.Length + 1) * sizeof(char));
        var memory = NativeMethods.GlobalAlloc(0x0002, bytes);
        if (memory == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        try
        {
            var pointer = NativeMethods.GlobalLock(memory);
            if (pointer == 0)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            try
            {
                Marshal.Copy(text.ToCharArray(), 0, pointer, text.Length);
                Marshal.WriteInt16(pointer, checked(text.Length * sizeof(char)), 0);
            }
            finally
            {
                NativeMethods.GlobalUnlock(memory);
            }

            return memory;
        }
        catch
        {
            NativeMethods.GlobalFree(memory);
            throw;
        }
    }
}
