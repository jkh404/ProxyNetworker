using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.ComponentModel;

namespace ProxyNetworker.Client.Runtime;

/// <summary>Runs an independently elevated network worker while the desktop UI remains unprivileged.</summary>
public sealed class WorkerClientController(string workerPath, string privateDirectory) : IClientController
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private NamedPipeServerStream? _pipe;
    private CancellationTokenSource? _connection;
    private Task? _readLoop;
    private Process? _process;
    private ClientState _state = ClientState.Stopped;
    public ClientState State => Volatile.Read(ref _state);
    public event Action<ClientState>? StateChanged;
    public event Action<ClientLog>? LogReceived;

    public async Task ConnectAsync(ProxyNetworkerClientConfig configuration, CancellationToken cancellationToken)
    {
        ClientConfigurationStore.Validate(configuration);
        if (State.State == ClientConnectionState.Failed)
            await DisconnectAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_pipe is not null) throw new InvalidOperationException("Disconnect the existing worker first.");
            if (!File.Exists(workerPath)) throw new FileNotFoundException("The network worker is missing. Build or install the complete desktop package.", workerPath);
            Publish(ClientState.Stopped with { State = ClientConnectionState.Starting });
            Directory.CreateDirectory(privateDirectory);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(privateDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var bootstrap = new WorkerBootstrap("proxynetworker-" + Guid.NewGuid().ToString("N"),
                Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), DateTimeOffset.UtcNow.AddMinutes(2));
            var bootstrapPath = Path.Combine(privateDirectory, "worker-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
                if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                await using (var stream = new FileStream(bootstrapPath, options))
                    await WorkerProtocol.WriteBootstrapAsync(stream, bootstrap, cancellationToken).ConfigureAwait(false);
                var pipeOptions = PipeOptions.Asynchronous;
                if (OperatingSystem.IsWindows()) pipeOptions |= PipeOptions.CurrentUserOnly;
                _pipe = new(bootstrap.PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, pipeOptions);
                _connection = new CancellationTokenSource();
                _process = LaunchWorker(Path.GetFullPath(workerPath), bootstrapPath, configuration.VirtualNetwork is { Enabled: true });
                using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                startup.CancelAfter(TimeSpan.FromSeconds(90));
                var exited = _process.WaitForExitAsync(startup.Token);
                while (true)
                {
                    var connected = _pipe.WaitForConnectionAsync(startup.Token);
                    if (await Task.WhenAny(connected, exited).ConfigureAwait(false) == exited)
                    {
                        await exited.ConfigureAwait(false);
                        throw new IOException("The network worker exited before authentication.");
                    }
                    await connected.ConfigureAwait(false);
                    using var handshake = CancellationTokenSource.CreateLinkedTokenSource(startup.Token);
                    handshake.CancelAfter(TimeSpan.FromSeconds(5));
                    WorkerMessage? hello = null;
                    try { hello = await WorkerProtocol.ReadAsync(_pipe, handshake.Token).ConfigureAwait(false); }
                    catch (Exception exception) when (exception is IOException or OperationCanceledException or System.Text.Json.JsonException) { }
                    if (hello?.Kind == "hello" && WorkerProtocol.Authenticate(bootstrap.Secret, hello.Secret)) break;
                    _pipe.Disconnect();
                }
                await WorkerProtocol.WriteAsync(_pipe, new("start", Configuration: configuration), startup.Token).ConfigureAwait(false);
                _readLoop = ReadLoopAsync(_pipe, _connection.Token);
            }
            finally { File.Delete(bootstrapPath); }
        }
        catch (Exception exception)
        {
            Cleanup();
            Publish(ClientState.Stopped with { State = ClientConnectionState.Failed,
                Error = exception is Win32Exception { NativeErrorCode: 1223 } ? "Administrator authorization was cancelled." : exception.Message });
            throw;
        }
        finally { _gate.Release(); }
    }

    private async Task ReadLoopAsync(Stream pipe, CancellationToken token)
    {
        try
        {
            while (await WorkerProtocol.ReadAsync(pipe, token).ConfigureAwait(false) is { } message)
            {
                if (message.State is { } state) Publish(state);
                if (message.Log is { } log) LogReceived?.Invoke(log);
                if (message.Kind == "error") Publish(State with { State = ClientConnectionState.Failed, Error = message.Error });
            }
            if (!token.IsCancellationRequested) Publish(ClientState.Stopped with { State = ClientConnectionState.Failed, Error = "Network worker disconnected." });
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException
            or InvalidDataException or System.Text.Json.JsonException)
        {
            if (!token.IsCancellationRequested) Publish(ClientState.Stopped with { State = ClientConnectionState.Failed, Error = "Network worker stopped." });
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_pipe is { IsConnected: true })
            {
                try
                {
                    await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try { await WorkerProtocol.WriteAsync(_pipe, new("stop"), cancellationToken).ConfigureAwait(false); }
                    finally { _writeGate.Release(); }
                }
                catch (IOException) { }
            }
            // EOF is also a stop signal, including when the UI crashes.
            _connection?.Cancel();
            _pipe?.Dispose();
            if (_readLoop is not null) await _readLoop.ConfigureAwait(false);
            if (_process is not null)
            {
                try { await _process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false); }
                catch (TimeoutException) { throw new IOException("The worker has not exited yet. Check its process before reconnecting."); }
            }
            Cleanup();
            Publish(ClientState.Stopped);
        }
        finally { _gate.Release(); }
    }

    private static Process LaunchWorker(string executable, string bootstrap, bool elevate)
    {
        ProcessStartInfo info;
        if (OperatingSystem.IsWindows())
        {
            info = new(executable) { UseShellExecute = elevate, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
            if (elevate) info.Verb = "runas";
            info.ArgumentList.Add("--bootstrap"); info.ArgumentList.Add(bootstrap);
        }
        else if (elevate && OperatingSystem.IsMacOS())
        {
            var command = ShellQuote(executable) + " --bootstrap " + ShellQuote(bootstrap);
            info = new("/usr/bin/osascript") { UseShellExecute = false, CreateNoWindow = true };
            info.ArgumentList.Add("-e");
            info.ArgumentList.Add("do shell script \"" + command.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\" with administrator privileges");
        }
        else
        {
            info = new(elevate ? "pkexec" : executable) { UseShellExecute = false, CreateNoWindow = true };
            if (elevate) info.ArgumentList.Add(executable);
            info.ArgumentList.Add("--bootstrap"); info.ArgumentList.Add(bootstrap);
        }
        return Process.Start(info) ?? throw new IOException("Could not start the network worker.");
    }
    private static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''") + "'";
    private void Publish(ClientState state) { Volatile.Write(ref _state, state); StateChanged?.Invoke(state); }
    private void Cleanup()
    {
        _connection?.Cancel(); _pipe?.Dispose(); _connection?.Dispose(); _process?.Dispose();
        _connection = null; _pipe = null; _process = null; _readLoop = null;
    }
    public async ValueTask DisposeAsync() => await DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
}
