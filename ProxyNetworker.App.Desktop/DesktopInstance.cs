using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace ProxyNetworker.App.Desktop;

internal sealed class DesktopInstance : IDisposable
{
    private readonly FileStream _lease;
    private readonly string _pipeName;
    private readonly Action _activate;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _receiver;
    private NamedPipeServerStream? _pipe;
    private int _disposed;

    private DesktopInstance(FileStream lease, string directory, Action activate)
    {
        _lease = lease; _pipeName = PipeName(directory); _activate = activate;
        _receiver = Task.Run(ReceiveAsync);
    }

    internal static DesktopInstance? TryAcquire(string directory, Action activate)
    {
        Directory.CreateDirectory(directory);
        var options = new FileStreamOptions { Mode = FileMode.OpenOrCreate, Access = FileAccess.Read, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        try { return new(new FileStream(Path.Combine(directory, "ui.lock"), options), directory, activate); }
        catch (IOException) { return null; }
    }

    internal static async Task<bool> ActivateExistingAsync(string directory)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        using var pipe = new NamedPipeClientStream(".", PipeName(directory), PipeDirection.Out,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await pipe.ConnectAsync(deadline.Token).ConfigureAwait(false);
            await pipe.WriteAsync(new byte[] { 1 }, deadline.Token).ConfigureAwait(false);
            await pipe.FlushAsync(deadline.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException) { return false; }
    }

    private async Task ReceiveAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                using var pipe = new NamedPipeServerStream(_pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                _pipe = pipe;
                await pipe.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
                using var request = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                request.CancelAfter(TimeSpan.FromSeconds(1));
                try
                {
                    var message = new byte[1];
                    if (await pipe.ReadAsync(message, request.Token).ConfigureAwait(false) == 1 && message[0] == 1) _activate();
                }
                catch (Exception exception) when (exception is IOException or OperationCanceledException) { }
            }
        }
        catch (Exception exception) when (_stop.IsCancellationRequested &&
            exception is IOException or OperationCanceledException or ObjectDisposedException) { }
    }
    private static string PipeName(string directory)
    {
        var identity = Path.GetFullPath(directory);
        if (OperatingSystem.IsWindows()) identity = identity.ToUpperInvariant();
        return "proxynetworker-ui-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel(); _pipe?.Dispose();
        _receiver.GetAwaiter().GetResult();
        _stop.Dispose(); _lease.Dispose();
    }
}
