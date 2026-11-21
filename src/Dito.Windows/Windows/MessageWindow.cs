using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Dito.Windows;

internal sealed class MessageWindow : NativeWindow, IDisposable
{
    private const int ClipboardUpdateMessage = 0x031D;
    private const int HotkeyMessage = 0x0312;
    private const int HotkeyId = 9000;
    private const uint HotkeyModifiers = 0x0002 | 0x4000; // Control, no repeat.
    private const uint HotkeyKey = 0xC0; // US backtick/tilde OEM key.

    private bool _listening;
    private bool _hotkeyRegistered;
    private bool _disposed;

    public MessageWindow()
    {
        CreateHandle(new CreateParams
        {
            Caption = "Dito.Messages",
            Parent = new nint(-3)
        });
    }

    public event EventHandler? ClipboardChanged;
    public event EventHandler? HotkeyPressed;

    public void StartListening()
    {
        if (!NativeMethods.AddClipboardFormatListener(Handle))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        _listening = true;
    }

    public bool TryRegisterHotkey()
    {
        _hotkeyRegistered = NativeMethods.RegisterHotKey(
            Handle, HotkeyId, HotkeyModifiers, HotkeyKey);
        return _hotkeyRegistered;
    }

    public void StopListening()
    {
        if (_listening)
        {
            NativeMethods.RemoveClipboardFormatListener(Handle);
            _listening = false;
        }

        if (_hotkeyRegistered)
        {
            NativeMethods.UnregisterHotKey(Handle, HotkeyId);
            _hotkeyRegistered = false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopListening();
        DestroyHandle();
        GC.SuppressFinalize(this);
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == ClipboardUpdateMessage)
        {
            ClipboardChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (message.Msg == HotkeyMessage && message.WParam == HotkeyId)
        {
            HotkeyPressed?.Invoke(this, EventArgs.Empty);
            return;
        }

        base.WndProc(ref message);
    }
}
