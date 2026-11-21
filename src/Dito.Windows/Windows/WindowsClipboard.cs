using System.Buffers.Text;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Dito.Application;

namespace Dito.Windows;

internal sealed class WindowsClipboard : IClipboardAccess
{
    private readonly nint _owner;
    private readonly uint _excludeFormat;
    private readonly uint _htmlFormat;
    private readonly uint _rtfFormat;
    private ClipboardWrite? _lastWrite;

    public WindowsClipboard(nint owner)
    {
        _owner = owner;
        _excludeFormat = RegisterFormat("ExcludeClipboardContentFromMonitorProcessing");
        _htmlFormat = RegisterFormat("HTML Format");
        _rtfFormat = RegisterFormat("Rich Text Format");
    }

    public uint SequenceNumber => NativeMethods.GetClipboardSequenceNumber();

    public ClipboardReadResult Read(uint expectedSequence)
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

    public ClipboardWrite? TryWrite(ClipboardContent content)
    {
        nint text = 0;
        nint html = 0;
        nint rtf = 0;
        try
        {
            text = AllocateText(content.Text);
            html = AllocateFormatted(content.Html);
            rtf = AllocateFormatted(content.Rtf);
            if (!NativeMethods.OpenClipboard(_owner))
            {
                return null;
            }

            var emptied = false;
            try
            {
                emptied = NativeMethods.EmptyClipboard();
                if (!emptied)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                // Publish richer formats before the Unicode fallback for consumers that enumerate formats.
                PublishFormat(_htmlFormat, ref html);
                PublishFormat(_rtfFormat, ref rtf);
                PublishFormat(NativeMethods.UnicodeText, ref text);
            }
            finally
            {
                NativeMethods.CloseClipboard();
                if (emptied)
                {
                    _lastWrite = new ClipboardWrite(SequenceNumber);
                }
            }

            return _lastWrite;
        }
        finally
        {
            FreeAllocation(rtf);
            FreeAllocation(html);
            FreeAllocation(text);
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
        if (text.Length == 0)
        {
            return new ClipboardReadResult(ClipboardReadStatus.Ignored);
        }

        var content = new ClipboardContent(text, ReadFormatted(_htmlFormat, isHtml: true), ReadFormatted(_rtfFormat));
        return new ClipboardReadResult(ClipboardReadStatus.Text, content);
    }

    private static uint RegisterFormat(string name)
    {
        var format = NativeMethods.RegisterClipboardFormat(name);
        return format != 0 ? format : throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    private static byte[]? ReadFormatted(uint format, bool isHtml = false)
    {
        if (!NativeMethods.IsClipboardFormatAvailable(format))
        {
            return null;
        }

        var memory = NativeMethods.GetClipboardData(format);
        if (memory == 0)
        {
            return null;
        }

        var size = NativeMethods.GlobalSize(memory);
        if (size == 0 || size > (nuint)Array.MaxLength)
        {
            return null;
        }

        var pointer = NativeMethods.GlobalLock(memory);
        if (pointer == 0)
        {
            return null;
        }

        try
        {
            var bytes = new byte[(int)size];
            Marshal.Copy(pointer, bytes, 0, bytes.Length);
            var length = isHtml ? GetHtmlLength(bytes) : Array.IndexOf(bytes, (byte)0);
            if (length <= 0)
            {
                return null;
            }

            return length == bytes.Length ? bytes : bytes[..length];
        }
        finally
        {
            NativeMethods.GlobalUnlock(memory);
        }
    }

    internal static int GetHtmlLength(ReadOnlySpan<byte> bytes)
    {
        var terminator = bytes.IndexOf((byte)0);
        if (terminator >= 0)
        {
            return terminator;
        }

        // CF_HTML may be unterminated; its header offsets count UTF-8 bytes.
        var end = ReadHtmlEnd(bytes, "EndHTML:"u8);
        return end > 0 ? end : ReadHtmlEnd(bytes, "EndFragment:"u8);
    }

    private static int ReadHtmlEnd(ReadOnlySpan<byte> bytes, ReadOnlySpan<byte> field)
    {
        var remainder = bytes;
        while (!remainder.IsEmpty)
        {
            var lineEnd = remainder.IndexOfAny((byte)'\r', (byte)'\n');
            if (lineEnd < 0 || remainder[0] == (byte)'<')
            {
                break;
            }

            var line = remainder[..lineEnd];
            if (line.StartsWith(field))
            {
                return Utf8Parser.TryParse(line[field.Length..], out int end, out _) &&
                    end >= bytes.Length - remainder.Length + lineEnd && end <= bytes.Length
                    ? end
                    : 0;
            }

            remainder = remainder[(lineEnd + 1)..];
        }

        return 0;
    }

    private static void PublishFormat(uint format, ref nint memory)
    {
        if (memory == 0)
        {
            return;
        }

        if (NativeMethods.SetClipboardData(format, memory) == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        memory = 0; // Windows owns the allocation after SetClipboardData succeeds.
    }

    private static void FreeAllocation(nint memory)
    {
        if (memory != 0)
        {
            NativeMethods.GlobalFree(memory);
        }
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
        var size = checked((nuint)(text.Length + 1) * sizeof(char));
        return AllocateClipboardData(size, pointer =>
        {
            Marshal.Copy(text.ToCharArray(), 0, pointer, text.Length);
            Marshal.WriteInt16(pointer, checked(text.Length * sizeof(char)), 0);
        });
    }

    private static nint AllocateFormatted(byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0)
        {
            return 0;
        }

        return AllocateClipboardData((nuint)bytes.Length + 1, pointer =>
        {
            Marshal.Copy(bytes, 0, pointer, bytes.Length);
            Marshal.WriteByte(pointer, bytes.Length, 0);
        });
    }

    private static nint AllocateClipboardData(nuint size, Action<nint> fill)
    {
        var memory = NativeMethods.GlobalAlloc(0x0042, size); // Movable, zero-initialized memory.
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
                fill(pointer);
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
