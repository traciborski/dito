using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Dito.X11;

internal sealed class DesktopInstance : IDisposable
{
    private readonly FileStream _lock;
    private readonly Socket _listener;
    private readonly string _socketPath;
    private readonly CancellationTokenSource _stop = new();
    private Task _accept = Task.CompletedTask;

    private DesktopInstance(FileStream file, Socket listener, string socketPath)
    {
        _lock = file;
        _listener = listener;
        _socketPath = socketPath;
    }

    public static async Task<DesktopInstance?> OpenAsync(string runtime, string display)
    {
        var screen = display.LastIndexOf('.');
        if (screen > display.LastIndexOf(':'))
        {
            display = display[..screen];
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(display)).AsSpan(0, 8));
        var path = Path.Combine(runtime, $"dito-{hash}");
        var endpoint = new UnixDomainSocketEndPoint(path);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!timeout.IsCancellationRequested)
        {
            FileStream? file = null;
            try
            {
                file = File.Open(path + ".lock", FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
            }
            catch (IOException)
            {
            }

            if (file is not null)
            {
                return Listen(file, endpoint, path);
            }

            using var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                await client.ConnectAsync(endpoint, timeout.Token).ConfigureAwait(false);
                return null;
            }
            catch (SocketException)
            {
                await Task.Delay(50, timeout.Token).ConfigureAwait(false);
            }
        }

        throw new IOException("The running Dito instance did not accept activation.");
    }

    public void ReceiveActivations(Action show, Action failed)
    {
        _accept = Task.Run(async () =>
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    using var client = await _listener.AcceptAsync(_stop.Token).ConfigureAwait(false);
                    show();
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
            }
            catch (SocketException) when (!_stop.IsCancellationRequested)
            {
                failed();
            }
        });
    }

    public void Dispose()
    {
        _stop.Cancel();
        try
        {
            _accept.GetAwaiter().GetResult();
        }
        finally
        {
            _listener.Dispose();
            try
            {
                File.Delete(_socketPath);
            }
            finally
            {
                _lock.Dispose();
                _stop.Dispose();
            }
        }
    }

    private static DesktopInstance Listen(FileStream file, UnixDomainSocketEndPoint endpoint, string path)
    {
        var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            File.Delete(path);
            listener.Bind(endpoint);
            listener.Listen(8);
            return new DesktopInstance(file, listener, path);
        }
        catch
        {
            listener.Dispose();
            file.Dispose();
            throw;
        }
    }
}
