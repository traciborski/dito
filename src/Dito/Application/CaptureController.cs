using System.Threading.Channels;
using Dito.Storage;

namespace Dito.Application;

internal sealed class CaptureController
{
    private readonly HistoryStore _history;
    private readonly ClipboardTransfer _clipboard;
    private readonly Channel<CapturedText> _pending = Channel.CreateBounded<CapturedText>(
        new BoundedChannelOptions(64)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait
        });

    private CancellationTokenSource? _readCancellation;
    private Task _writer = Task.CompletedTask;
    private volatile bool _paused = true;
    private volatile bool _stopping;

    public CaptureController(HistoryStore history, ClipboardTransfer clipboard)
    {
        _history = history;
        _clipboard = clipboard;
    }

    public event Action<string>? Failed;
    public event Action<bool>? PauseChanged;

    public bool IsPaused => _paused;

    public void Start()
    {
        _writer = Task.Run(SavePendingAsync);
        SetPaused(false);
    }

    public void SetPaused(bool paused)
    {
        if (_stopping)
        {
            return;
        }

        _paused = paused;
        CancelPendingRead();
        PauseChanged?.Invoke(paused);
    }

    public void ObserveClipboard()
    {
        if (_paused || _stopping)
        {
            return;
        }

        CancelPendingRead();
        _readCancellation = new CancellationTokenSource();
        _ = ReadLatestAsync(
            _clipboard.SequenceNumber, DateTimeOffset.UtcNow, _readCancellation.Token);
    }

    public Task StopAsync()
    {
        _stopping = true;
        _paused = true;
        CancelPendingRead();
        _pending.Writer.TryComplete();
        return _writer;
    }

    private async Task ReadLatestAsync(
        uint sequence,
        DateTimeOffset copiedAt,
        CancellationToken cancellationToken)
    {
        try
        {
            var text = await _clipboard.ReadAsync(sequence, cancellationToken);
            if (_paused || _stopping || cancellationToken.IsCancellationRequested ||
                string.IsNullOrEmpty(text))
            {
                return;
            }

            EnqueueCapture(text, copiedAt);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            PauseAfterFailure("Clipboard text could not be read. Capture is paused.");
        }
    }

    private async Task SavePendingAsync()
    {
        await foreach (var copy in _pending.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                await _history.RecordCopyAsync(copy.Text, copy.CopiedAt).ConfigureAwait(false);
            }
            catch (Exception)
            {
                _paused = true;
                while (_pending.Reader.TryRead(out _))
                {
                }

                PauseAfterFailure(
                    "Some entries could not be saved. Check disk space and database access; capture is paused.");
            }
        }
    }

    private void EnqueueCapture(string text, DateTimeOffset copiedAt)
    {
        if (!_pending.Writer.TryWrite(new CapturedText(text, copiedAt)))
        {
            PauseAfterFailure(
                "History storage could not keep up. Some entries were not saved; capture is paused.");
        }
    }

    private void PauseAfterFailure(string message)
    {
        _paused = true;
        PauseChanged?.Invoke(true);
        Failed?.Invoke(message);
    }

    private void CancelPendingRead()
    {
        _readCancellation?.Cancel();
        _readCancellation?.Dispose();
        _readCancellation = null;
    }

    private sealed record CapturedText(string Text, DateTimeOffset CopiedAt);
}
