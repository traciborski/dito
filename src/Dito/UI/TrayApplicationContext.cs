using Dito.Application;
using Dito.Storage;
using Dito.Windows;
using FormsApplication = System.Windows.Forms.Application;

namespace Dito.UI;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly HistoryStore _history;
    private readonly MessageWindow _messages;
    private readonly CaptureController _capture;
    private readonly HistorySearch _search;
    private readonly RecallController _recall;
    private readonly PasteService _paste;
    private readonly SearchForm _form;
    private readonly Control _dispatcher = new();
    private readonly ContextMenuStrip _menu = new();
    private readonly Icon _icon;
    private readonly NotifyIcon _tray;
    private readonly ToolStripMenuItem _show;
    private readonly ToolStripMenuItem _pause;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _recallCancellation;
    private PasteTarget? _target;
    private Task _recallTask = Task.CompletedTask;
    private Task? _shutdownTask;
    private bool _ready;
    private bool _recalling;
    private bool _shuttingDown;
    private bool _disposed;

    public TrayApplicationContext(
        HistoryStore history,
        MessageWindow messages,
        CaptureController capture,
        HistorySearch search,
        RecallController recall,
        PasteService paste)
    {
        _history = history;
        _messages = messages;
        _capture = capture;
        _search = search;
        _recall = recall;
        _paste = paste;
        using var iconStream = typeof(TrayApplicationContext).Assembly.GetManifestResourceStream("Dito.ico")!;
        _icon = new Icon(iconStream);
        _form = new SearchForm(search) { Icon = _icon };
        _show = new ToolStripMenuItem("Show History (Ctrl+`)", null, OnShowRequested) { Enabled = false };
        _pause = new ToolStripMenuItem("Pause Capture", null, OnPauseRequested) { Enabled = false };
        _menu.Items.AddRange([
            _show, _pause, new ToolStripSeparator(),
            new ToolStripMenuItem("Exit", null, OnExitRequested)
        ]);
        _tray = new NotifyIcon
        {
            Icon = _icon,
            Text = "Dito - starting",
            ContextMenuStrip = _menu,
            Visible = true
        };
        _ = _dispatcher.Handle;
        ConnectEvents();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            FormsApplication.Idle -= OnStartup;
            _capture.Failed -= OnCaptureFailed;
            _capture.PauseChanged -= OnPauseChanged;
            _messages.ClipboardChanged -= OnClipboardChanged;
            _messages.HotkeyPressed -= OnShowRequested;
            _lifetime.Cancel();
            _messages.StopListening();
            _search.Dispose();
            _form.Dispose();
            _tray.Dispose();
            _icon.Dispose();
            _menu.Dispose();
            _dispatcher.Dispose();
            _lifetime.Dispose();
        }

        base.Dispose(disposing);
    }

    private void ConnectEvents()
    {
        FormsApplication.Idle += OnStartup;
        _messages.ClipboardChanged += OnClipboardChanged;
        _messages.HotkeyPressed += OnShowRequested;
        _tray.DoubleClick += OnShowRequested;
        _form.RecallRequested += OnRecallRequested;
        _form.Dismissed += OnDismissed;
        _capture.Failed += OnCaptureFailed;
        _capture.PauseChanged += OnPauseChanged;
    }

    private async void OnStartup(object? sender, EventArgs e)
    {
        FormsApplication.Idle -= OnStartup;
        try
        {
            await _history.InitializeAsync(_lifetime.Token);
            if (_shuttingDown)
            {
                return;
            }

            _capture.Start();
            _messages.StartListening();
            var hotkeyRegistered = _messages.TryRegisterHotkey();
            _ready = true;
            _show.Enabled = true;
            _pause.Enabled = true;
            UpdatePauseState();

            if (!hotkeyRegistered)
            {
                MessageBox.Show(
                    "Ctrl+` could not be registered; another application may be using it. " +
                    "Dito is still capturing. Open history from its tray icon.",
                    "Dito - shortcut unavailable", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        catch (OperationCanceledException) when (_shuttingDown)
        {
        }
        catch (Exception)
        {
            MessageBox.Show(
                "Dito could not initialize clipboard history. Check database permissions, disk space, " +
                "or whether the database requires a newer version. Existing history was not removed.",
                "Dito", MessageBoxButtons.OK, MessageBoxIcon.Error);
            await RequestShutdownAsync();
        }
    }

    private void OnClipboardChanged(object? sender, EventArgs e)
    {
        if (_ready && !_shuttingDown)
        {
            _capture.ObserveClipboard();
        }
    }

    private void OnShowRequested(object? sender, EventArgs e)
    {
        if (!_ready || _shuttingDown || _recalling)
        {
            return;
        }

        if (_form.Visible)
        {
            _form.FocusSearch();
            return;
        }

        _target = _paste.CaptureTarget();
        _form.ShowPopup();
    }

    private void OnPauseRequested(object? sender, EventArgs e)
    {
        _capture.SetPaused(!_capture.IsPaused);
    }

    private void OnPauseChanged(bool paused)
    {
        Dispatch(UpdatePauseState);
    }

    private void UpdatePauseState()
    {
        _pause.Text = _capture.IsPaused ? "Resume Capture" : "Pause Capture";
        _tray.Text = _capture.IsPaused ? "Dito - capture paused" : "Dito (Ctrl+`)";
    }

    private void OnCaptureFailed(string message)
    {
        Dispatch(() => Notify(message));
    }

    private void OnRecallRequested(long entryId, RecallMode mode)
    {
        if (!_recalling && !_shuttingDown)
        {
            _recallTask = RecallAsync(entryId, mode);
        }
    }

    private async Task RecallAsync(long entryId, RecallMode mode)
    {
        if (_recalling || _shuttingDown)
        {
            return;
        }

        _recalling = true;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _recallCancellation = cancellation;
        _form.SetRecalling(true);
        try
        {
            var result = await _recall.RecallAsync(
                entryId, _target, mode, _form.HideForRecall, cancellation.Token);
            if (!cancellation.IsCancellationRequested)
            {
                ShowRecallResult(result);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            if (!_shuttingDown)
            {
                Notify("Recall failed. Check the destination before retrying.");
            }
        }
        finally
        {
            _recallCancellation = null;
            _recalling = false;
            if (!_form.IsDisposed)
            {
                _form.SetRecalling(false);
            }
        }
    }

    private void ShowRecallResult(RecallResult result)
    {
        switch (result)
        {
            case RecallResult.ClipboardBusy:
                _form.ShowStatus("The clipboard is busy. Try again; nothing was pasted.");
                break;
            case RecallResult.EntryMissing:
                _form.ShowStatus("That entry is no longer available. Reopen history to refresh.");
                break;
            case RecallResult.ManualPaste:
                Notify("Text is on the clipboard. Automatic paste was unavailable; use Ctrl+V.");
                break;
            case RecallResult.ClipboardChanged:
                Notify("The clipboard changed before paste. Nothing was pasted; select the entry again.");
                break;
        }
    }

    private void OnDismissed()
    {
        _recallCancellation?.Cancel();
    }

    private async void OnExitRequested(object? sender, EventArgs e)
    {
        await RequestShutdownAsync();
    }

    private Task RequestShutdownAsync()
    {
        return _shutdownTask ??= ShutdownAsync();
    }

    private async Task ShutdownAsync()
    {
        _shuttingDown = true;
        _show.Enabled = false;
        _pause.Enabled = false;
        _tray.Text = "Dito - finishing pending saves";
        _messages.StopListening();
        _lifetime.Cancel();
        _search.Cancel();

        try
        {
            await _recallTask;
            await _capture.StopAsync();
            await _history.DisposeAsync();
        }
        catch (Exception)
        {
            MessageBox.Show("Dito could not finish saving history. Check disk space and database access.",
                "Dito", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            _form.CloseForExit();
            _tray.Visible = false;
            ExitThread();
        }
    }

    private void Notify(string message)
    {
        if (_form.Visible)
        {
            _form.ShowStatus(message);
        }

        _tray.ShowBalloonTip(5000, "Dito", message, ToolTipIcon.Warning);
    }

    private void Dispatch(Action action)
    {
        if (_disposed || _shuttingDown)
        {
            return;
        }

        if (_dispatcher.InvokeRequired)
        {
            _dispatcher.BeginInvoke(() => Dispatch(action));
        }
        else
        {
            action();
        }
    }
}
