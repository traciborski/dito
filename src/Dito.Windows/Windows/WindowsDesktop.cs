using System.Runtime.InteropServices;
using Dito.Application;

namespace Dito.Windows;

internal sealed class WindowsDesktop : IDesktopInput
{
    private static readonly int[] ModifierKeys = [0x10, 0x11, 0x12, 0x5B, 0x5C];

    public bool ModifiersReleased =>
        ModifierKeys.All(key => (NativeMethods.GetAsyncKeyState(key) & 0x8000) == 0);

    public PasteTarget? CaptureTarget()
    {
        var window = NativeMethods.GetForegroundWindow();
        var threadId = NativeMethods.GetWindowThreadProcessId(window, out var processId);
        return window == 0 || processId == Environment.ProcessId || threadId == 0
            ? null
            : new PasteTarget(window, processId, threadId);
    }

    public bool IsTargetValid(PasteTarget target)
    {
        if (!NativeMethods.IsWindow(target.Window) || target.ProcessId == Environment.ProcessId)
        {
            return false;
        }

        var threadId = NativeMethods.GetWindowThreadProcessId(target.Window, out var processId);
        return threadId == target.ThreadId && processId == target.ProcessId;
    }

    public bool TryActivate(PasteTarget target)
    {
        return IsTargetValid(target) && NativeMethods.SetForegroundWindow(target.Window);
    }

    public bool IsForeground(PasteTarget target)
    {
        return NativeMethods.GetForegroundWindow() == target.Window;
    }

    public bool TrySendPaste(PasteTarget target, Func<bool> clipboardUnchanged)
    {
        NativeMethods.Input[] inputs =
        [
            KeyInput(0x11, keyUp: false),
            KeyInput(0x56, keyUp: false),
            KeyInput(0x56, keyUp: true),
            KeyInput(0x11, keyUp: true)
        ];

        if (!IsTargetValid(target) || !IsForeground(target) ||
            !ModifiersReleased || !clipboardUnchanged())
        {
            return false;
        }

        var inserted = NativeMethods.SendInput(
            (uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.Input>());
        if (inserted > 0 && inserted < inputs.Length)
        {
            ReleasePartiallyInjectedKeys(inserted);
        }

        return inserted == inputs.Length;
    }

    private static NativeMethods.Input KeyInput(ushort key, bool keyUp)
    {
        return new NativeMethods.Input
        {
            Type = 1,
            Data = new NativeMethods.InputData
            {
                Keyboard = new NativeMethods.KeyboardInput
                {
                    Key = key,
                    Flags = keyUp ? 0x0002u : 0
                }
            }
        };
    }

    private static void ReleasePartiallyInjectedKeys(uint inserted)
    {
        // Release only key-downs injected by this operation, never user-held modifiers.
        NativeMethods.Input[] releases = inserted == 2
            ? [KeyInput(0x56, keyUp: true), KeyInput(0x11, keyUp: true)]
            : [KeyInput(0x11, keyUp: true)];
        NativeMethods.SendInput(
            (uint)releases.Length, releases, Marshal.SizeOf<NativeMethods.Input>());
    }
}
