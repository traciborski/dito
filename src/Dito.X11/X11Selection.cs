using System.Runtime.InteropServices;

namespace Dito.X11;

internal sealed class X11Selection : IDisposable
{
    private const string X11 = "libX11.so.6";
    private const string XFixes = "libXfixes.so.3";
    private readonly nint _display;
    private readonly int _eventType;
    private readonly nint[] _event = new nint[24];
    private long _revision;

    public X11Selection()
    {
        _display = XOpenDisplay(0);
        if (_display == 0)
        {
            throw new InvalidOperationException("Could not connect to the X11 display.");
        }

        try
        {
            if (XFixesQueryExtension(_display, out _eventType, out _) == 0)
            {
                throw new InvalidOperationException("The X11 server does not provide XFixes.");
            }

            var root = XDefaultRootWindow(_display);
            var clipboard = XInternAtom(_display, "CLIPBOARD", 0);
            const nuint selectionNotifications = 1 | 2 | 4; // Owner change, owner window destroyed, client closed.
            XFixesSelectSelectionInput(_display, root, clipboard, selectionNotifications);
            XSync(_display, 0);
        }
        catch
        {
            XCloseDisplay(_display);
            throw;
        }
    }

    public long ReadRevision()
    {
        while (XCheckTypedEvent(_display, _eventType, _event) != 0)
        {
            _revision++;
        }

        return _revision;
    }

    public void Dispose() => XCloseDisplay(_display);

    [DllImport(X11)]
    private static extern nint XOpenDisplay(nint name);

    [DllImport(X11)]
    private static extern int XCloseDisplay(nint display);

    [DllImport(X11)]
    private static extern nuint XDefaultRootWindow(nint display);

    [DllImport(X11)]
    private static extern nuint XInternAtom(nint display, string atom, int onlyIfExists);

    [DllImport(X11)]
    private static extern int XSync(nint display, int discard);

    [DllImport(X11)]
    private static extern int XCheckTypedEvent(nint display, int type, [Out] nint[] buffer);

    [DllImport(XFixes)]
    private static extern int XFixesQueryExtension(nint display, out int eventBase, out int errorBase);

    [DllImport(XFixes)]
    private static extern void XFixesSelectSelectionInput(nint display, nuint window, nuint selection, nuint mask);
}
