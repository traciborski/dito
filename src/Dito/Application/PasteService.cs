using System.Diagnostics;

namespace Dito.Application;

internal sealed record PasteTarget(nint Window, uint ProcessId, uint ThreadId);

internal interface IDesktopInput
{
    PasteTarget? CaptureTarget();
    bool IsTargetValid(PasteTarget target);
    bool TryActivate(PasteTarget target);
    bool IsForeground(PasteTarget target);
    bool ModifiersReleased { get; }
    bool TrySendPaste(PasteTarget target, Func<bool> clipboardUnchanged);
}

internal sealed class PasteService
{
    private readonly IDesktopInput _desktop;

    public PasteService(IDesktopInput desktop)
    {
        _desktop = desktop;
    }

    public PasteTarget? CaptureTarget()
    {
        return _desktop.CaptureTarget();
    }

    public async Task<bool> TryPasteAsync(
        PasteTarget? target,
        Func<bool> clipboardUnchanged,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (target is null || !_desktop.IsTargetValid(target) || !clipboardUnchanged())
        {
            return false;
        }

        _desktop.TryActivate(target);
        if (!await WaitForTargetAsync(target, clipboardUnchanged, cancellationToken))
        {
            return false;
        }

        cancellationToken.ThrowIfCancellationRequested();
        return _desktop.TrySendPaste(target, clipboardUnchanged);
    }

    private async Task<bool> WaitForTargetAsync(
        PasteTarget target,
        Func<bool> clipboardUnchanged,
        CancellationToken cancellationToken)
    {
        var timeout = Stopwatch.StartNew();
        var hadForeground = false;

        while (timeout.Elapsed < TimeSpan.FromMilliseconds(600))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_desktop.IsTargetValid(target) || !clipboardUnchanged())
            {
                return false;
            }

            var foreground = _desktop.IsForeground(target);
            if (hadForeground && !foreground)
            {
                return false;
            }

            hadForeground |= foreground;
            if (foreground && _desktop.ModifiersReleased)
            {
                return true;
            }

            await Task.Delay(20, cancellationToken);
        }

        return false;
    }
}
