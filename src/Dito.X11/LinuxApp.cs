using System.Runtime.Versioning;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Dito.Application;
using Dito.Storage;

namespace Dito.X11;

[SupportedOSPlatform("linux")]
internal sealed class LinuxApp(DesktopInstance instance) : Avalonia.Application
{
    private readonly CancellationTokenSource _stopping = new();
    private readonly string _historyPath = GetHistoryPath();
    private IClassicDesktopStyleApplicationLifetime _desktop = null!;
    private HistoryStore _history = null!;
    private HistorySearch _search = null!;
    private HistoryWindow _window = null!;
    private NativeMenuItem _pause = null!;
    private LinuxClipboard? _clipboard;
    private X11Selection? _selection;
    private ClipboardCapture? _capture;
    private Task _initialize = Task.CompletedTask;
    private Task _monitor = Task.CompletedTask;
    private int _activationPending;

    public override void Initialize() => Styles.Add(new FluentTheme());

    public override void OnFrameworkInitializationCompleted()
    {
        _desktop = (IClassicDesktopStyleApplicationLifetime)ApplicationLifetime!;
        _history = new HistoryStore(_historyPath);
        _search = new HistorySearch(_history);
        using var iconStream = typeof(LinuxApp).Assembly.GetManifestResourceStream("Dito.ico")!;
        var icon = new WindowIcon(iconStream);
        _window = new HistoryWindow(_search, CopyEntryAsync, icon);
        _window.ToggleCapture += ToggleCapture;
        _window.Quit += () => _ = QuitAsync();
        CreateTray(icon);
        _window.OpenHistory();
        instance.ReceiveActivations(QueueActivation, () => Dispatcher.UIThread.Post(
            () => _window.ShowError("Dito can no longer receive --show requests. Restart Dito to try again.")));
        Dispatcher.UIThread.Post(() => _initialize = InitializeHistoryAsync());
        base.OnFrameworkInitializationCompleted();
    }

    private async Task InitializeHistoryAsync()
    {
        if (_stopping.IsCancellationRequested)
        {
            return;
        }

        try
        {
            await Task.Run(() => Directory.CreateDirectory(Path.GetDirectoryName(_historyPath)!,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute), _stopping.Token);
            await _history.InitializeAsync(_stopping.Token);
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
            return;
        }
        catch (Exception)
        {
            _window.ShowError("History could not be opened. Check database access and disk space.");
            return;
        }

        if (_stopping.IsCancellationRequested)
        {
            return;
        }

        _window.EnableHistory();
        try
        {
            _clipboard = new LinuxClipboard(_window.Clipboard ??
                throw new InvalidOperationException("X11 clipboard is unavailable."));
            _selection = new X11Selection();
            _capture = new ClipboardCapture(_history, _selection.ReadRevision, _clipboard.ReadExternalTextAsync);
            _capture.Failed += ShowCaptureFailure;
            ToggleCapture();
            _monitor = _capture.RunAsync(_stopping.Token);
        }
        catch (Exception)
        {
            _window.ShowCaptureStatus(true, "Clipboard capture is unavailable. Check X11 and libXfixes.", canToggle: false);
        }
    }

    private static string GetHistoryPath()
    {
        var data = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (string.IsNullOrEmpty(data) || !Path.IsPathFullyQualified(data))
        {
            data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        }

        return Path.Combine(data, "dito", "history.db");
    }

    private void CreateTray(WindowIcon icon)
    {
        _pause = new NativeMenuItem("Pause Capture") { IsEnabled = false };
        _pause.Click += (_, _) => ToggleCapture();
        var show = new NativeMenuItem("Show History");
        show.Click += (_, _) => _window.OpenHistory();
        var exit = new NativeMenuItem("Exit");
        exit.Click += async (_, _) => await QuitAsync();
        var tray = new TrayIcon
        {
            Icon = icon,
            ToolTipText = "Dito",
            Menu = new NativeMenu { Items = { show, _pause, exit } }
        };
        tray.Clicked += (_, _) => _window.OpenHistory();
        TrayIcon.SetIcons(this, new TrayIcons { tray });
    }

    private void QueueActivation()
    {
        if (Interlocked.Exchange(ref _activationPending, 1) != 0)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            Interlocked.Exchange(ref _activationPending, 0);
            if (!_stopping.IsCancellationRequested)
            {
                _window.OpenHistory();
            }
        });
    }

    private async Task<bool> CopyEntryAsync(long id, CancellationToken cancellationToken)
    {
        var text = await _history.ReadTextAsync(id, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return text is not null && _clipboard is { } clipboard && await clipboard.CopyAsync(text);
    }

    private void ToggleCapture()
    {
        if (_stopping.IsCancellationRequested || _capture is not { CanResume: true } capture)
        {
            return;
        }

        capture.SetPaused(!capture.IsPaused);
        _window.ShowCaptureStatus(capture.IsPaused);
        _pause.IsEnabled = true;
        _pause.Header = capture.IsPaused ? "Resume Capture" : "Pause Capture";
    }

    private void ShowCaptureFailure(string message)
    {
        var canResume = _capture is { CanResume: true };
        _window.ShowCaptureStatus(true, message, canResume);
        _pause.IsEnabled = canResume;
        _pause.Header = "Resume Capture";
    }

    private async Task QuitAsync()
    {
        if (_stopping.IsCancellationRequested)
        {
            return;
        }

        _stopping.Cancel();
        try
        {
            _window.Exit();
            await _initialize;
            await _monitor;
            await _history.DisposeAsync();
        }
        catch (Exception)
        {
            Console.Error.WriteLine("Dito could not shut down cleanly. Check history database access.");
        }
        finally
        {
            _selection?.Dispose();
            _search.Dispose();
            _stopping.Dispose();
            _desktop.Shutdown();
        }
    }
}
