namespace ProxyNetworker.Client.Runtime;

/// <summary>Hosts a session in a worker process or Android foreground service.</summary>
public sealed class LocalClientController(IClientPlatform platform) : IClientController
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ClientState _state = ClientState.Stopped;
    private CancellationTokenSource? _lifetime;
    private Task? _run;
    public ClientState State => Volatile.Read(ref _state);
    public event Action<ClientState>? StateChanged;
    public event Action<ClientLog>? LogReceived;

    public async Task ConnectAsync(ProxyNetworkerClientConfig configuration, CancellationToken cancellationToken)
    {
        ClientConfigurationStore.Validate(configuration);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_run is { IsCompleted: false }) throw new InvalidOperationException("The client is already running.");
            _lifetime?.Dispose();
            _lifetime = new CancellationTokenSource();
            var token = _lifetime.Token;
            Publish(ClientState.Stopped with { State = ClientConnectionState.Starting });
            _run = Task.Run(() => RunAsync(configuration, token), CancellationToken.None);
        }
        finally { _gate.Release(); }
    }

    private async Task RunAsync(ProxyNetworkerClientConfig configuration, CancellationToken token)
    {
        var session = new ClientSession(platform);
        session.LogReceived += entry => LogReceived?.Invoke(entry);
        var run = session.RunAsync(configuration, token);
        try
        {
            while (!run.IsCompleted)
            {
                Publish(session.GetState());
                await Task.WhenAny(run, Task.Delay(500, token)).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
            }
            await run.ConfigureAwait(false);
            Publish(session.GetState());
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            try { await run.ConfigureAwait(false); } catch (OperationCanceledException) { }
            Publish(ClientState.Stopped);
        }
        catch (Exception)
        {
            Publish(session.GetState() with { State = ClientConnectionState.Failed });
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_run is null) return;
            Publish(State with { State = ClientConnectionState.Stopping });
            _lifetime?.Cancel();
            await _run.ConfigureAwait(false);
            _run = null;
            Publish(ClientState.Stopped);
        }
        finally { _gate.Release(); }
    }

    private void Publish(ClientState state)
    {
        Volatile.Write(ref _state, state);
        StateChanged?.Invoke(state);
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
        _lifetime?.Dispose();
    }
}
