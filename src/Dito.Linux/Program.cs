using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;

namespace Dito.Linux;

internal static class Program
{
    public static int Main(string[] args)
    {
        if (args is ["--help"] or ["-h"])
        {
            Console.WriteLine("Usage: Dito.Linux [--show] (native X11 session required)");
            return 0;
        }

        if (args is not [] and not ["--show"])
        {
            Console.Error.WriteLine("Usage: Dito.Linux [--show]");
            return 1;
        }

        var display = Environment.GetEnvironmentVariable("DISPLAY");
        var session = Environment.GetEnvironmentVariable("XDG_SESSION_TYPE");
        var wayland = Environment.GetEnvironmentVariable("WAYLAND_DISPLAY");
        if (!OperatingSystem.IsLinux() || string.IsNullOrEmpty(display) ||
            (!string.IsNullOrEmpty(session) && session != "x11") ||
            !string.IsNullOrEmpty(wayland) || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_SOCKET")))
        {
            Console.Error.WriteLine("Dito needs a native X11 session. Wayland and XWayland are not supported.");
            return 1;
        }

        try
        {
            var runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
            if (string.IsNullOrEmpty(runtime) || !Path.IsPathFullyQualified(runtime))
            {
                Console.Error.WriteLine("Dito needs an absolute XDG_RUNTIME_DIR for its per-user activation socket.");
                return 1;
            }

            using var instance = DesktopInstance.OpenAsync(runtime, display).GetAwaiter().GetResult();
            if (instance is null)
            {
                return 0;
            }

            return RunDesktop(instance);
        }
        catch (Exception)
        {
            Console.Error.WriteLine("Dito could not start. Check X11, the required libraries, and runtime directory access.");
            return 1;
        }
    }

    [SupportedOSPlatform("linux")]
    private static int RunDesktop(DesktopInstance instance)
    {
        return AppBuilder.Configure(() => new LinuxApp(instance))
            .UsePlatformDetect()
            .StartWithClassicDesktopLifetime([], ShutdownMode.OnExplicitShutdown);
    }
}
