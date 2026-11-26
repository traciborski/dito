using Dito.Application;
using Dito.Storage;

namespace Dito.X11;

internal sealed class ClipboardCapture(
    HistoryStore history, Func<long> revision, Func<Task<string?>> readText)
{
    private long _seen;
    private int _generation;

    public bool IsPaused { get; private set; } = true;
    public bool CanResume { get; private set; } = true;

    public event Action<string>? Failed;

    public void SetPaused(bool paused)
    {
        _seen = revision();
        _generation++;
        IsPaused = paused;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(50));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                await CaptureLatestAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            IsPaused = true;
            CanResume = false;
            Failed?.Invoke("Clipboard monitoring stopped. Restart Dito to try again.");
        }
    }

    internal async Task CaptureLatestAsync(CancellationToken cancellationToken = default)
    {
        if (IsPaused)
        {
            return;
        }

        var current = revision();
        if (current == _seen)
        {
            return;
        }

        _seen = current;
        var generation = _generation;
        var now = DateTimeOffset.UtcNow;
        try
        {
            var text = await readText();
            if (!cancellationToken.IsCancellationRequested && generation == _generation &&
                current == revision() && text is not null &&
                new ClipboardContent(text).TrimForCapture() is { } content)
            {
                await history.RecordCopyAsync(content, now);
            }
        }
        catch (Exception)
        {
            if (generation == _generation && !cancellationToken.IsCancellationRequested)
            {
                SetPaused(true);
                Failed?.Invoke("Clipboard capture failed. Check the clipboard and history database, then resume capture.");
            }
        }
    }
}
