using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using ProxyNetworker.Core;
using ProxyNetworker.Core.Platforms.Linux;

namespace ProxyNetworker.Client.Runtime;

/// <summary>Shared token refresh, device lifetime and port-tunnel runtime. It does not depend on UI.</summary>
public sealed class ClientSession(IClientPlatform platform, bool collectTelemetry = true)
{
    private readonly object _gate = new();
    private ClientState _state = ClientState.Stopped;
    private readonly List<(string Name, RemotePortTunnelClient Client)> _ports = [];
    private VirtualNetworkTunnel? _udp;
    private ObservedTun? _observed;
    private string[] _secrets = [];
    private int _running;
    private long _sentBase, _receivedBase;

    public event Action<ClientLog>? LogReceived;

    public ClientState GetState()
    {
        lock (_gate)
        {
            return _state with
            {
                State = _state.State == ClientConnectionState.Starting && _ports.Any(port => port.Client.IsReady)
                    ? ClientConnectionState.Running : _state.State,
                BytesSent = _sentBase + (_observed?.BytesSent ?? 0) + _ports.Sum(port => port.Client.BytesSent),
                BytesReceived = _receivedBase + (_observed?.BytesReceived ?? 0) + _ports.Sum(port => port.Client.BytesReceived),
                Peers = _udp?.GetPeerSnapshots().Select(peer =>
                    new ClientPeerState(peer.Address, peer.Endpoint, peer.Path, peer.RttMilliseconds)).ToArray() ?? [],
                PortTunnels = _ports.Select(port => new ClientPortState(
                    port.Name, port.Client.IsReady, port.Client.Configuration?.Protocol.ToString(),
                    port.Client.Configuration?.PublicPort)).ToArray()
            };
        }
    }

    public async Task RunAsync(ProxyNetworkerClientConfig configuration, CancellationToken cancellationToken)
    {
        ClientConfigurationStore.Validate(configuration);
        if (Interlocked.Exchange(ref _running, 1) != 0)
            throw new InvalidOperationException("The client session is already running.");
        _secrets = configuration.PortTunnels.Select(port => port.Token)
            .Concat([configuration.VirtualNetwork?.Token ?? "",
                Convert.ToBase64String(configuration.VirtualNetwork?.AuthenticationKey ?? [])])
            .Where(value => value.Length > 0).ToArray();
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var tasks = new List<Task>();
        var locks = new List<IDisposable>();
        try
        {
            lock (_gate)
            {
                _sentBase = _receivedBase = 0;
                _state = ClientState.Stopped with { State = ClientConnectionState.Starting, StartedAt = DateTimeOffset.UtcNow };
            }
            foreach (var port in configuration.PortTunnels.Where(item => item.Enabled))
            {
                locks.Add(Acquire(port.Token));
                var server = ClientConfigurationStore.ValidateServer(port.Server ?? configuration.GetDefaultServer());
                var endpoint = new UriBuilder(new Uri(server, "api/client/port-tunnels/connect"))
                { Scheme = server.Scheme == "https" ? "wss" : "ws" }.Uri;
                var client = new RemotePortTunnelClient(endpoint, port.Token, Log);
                lock (_gate) _ports.Add((port.Name ?? "Port tunnel", client));
                tasks.Add(client.RunAsync(lifetime.Token));
            }
            if (configuration.VirtualNetwork is { Enabled: true } network)
            {
                if (!string.IsNullOrWhiteSpace(network.Token)) locks.Add(Acquire(network.Token));
                tasks.Add(RunVirtualNetworkAsync(network, configuration.GetDefaultServer(), lifetime.Token));
            }
            if (tasks.Count == 0) throw new ArgumentException("No enabled client entries were found.");
            await (await Task.WhenAny(tasks).ConfigureAwait(false)).ConfigureAwait(false);
            if (!lifetime.IsCancellationRequested) throw new IOException("A configured client stopped unexpectedly.");
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception exception)
        {
            lock (_gate) _state = _state with { State = ClientConnectionState.Failed, Error = Sanitize(exception.Message) };
            throw;
        }
        finally
        {
            lifetime.Cancel();
            try { await Task.WhenAll(tasks).ConfigureAwait(false); }
            catch (Exception) when (lifetime.IsCancellationRequested) { }
            lock (_gate)
            {
                foreach (var port in _ports) port.Client.Dispose();
                _ports.Clear();
                if (_state.State != ClientConnectionState.Failed) _state = ClientState.Stopped;
            }
            foreach (var tokenLock in locks) tokenLock.Dispose();
            Interlocked.Exchange(ref _running, 0);
        }
    }

    private async Task RunVirtualNetworkAsync(DirectVirtualNetworkClientConfig configuration, string? defaultServer, CancellationToken token)
    {
        var server = configuration.Server ?? defaultServer;
        using var api = string.IsNullOrWhiteSpace(configuration.Token) ? null : new ManagementApiClient(server!);
        var delay = TimeSpan.FromSeconds(2);
        while (!token.IsCancellationRequested)
        {
            try
            {
                var remote = api is null ? null : await api.GetVirtualNetworkConfigAsync(configuration.Token!, token).ConfigureAwait(false);
                var current = remote is null ? configuration : FromRemote(remote, configuration, server!);
                using var tunnel = await StartTunnelAsync(current, token).ConfigureAwait(false);
                delay = TimeSpan.FromSeconds(2);
                lock (_gate) _state = _state with { State = ClientConnectionState.Running, Error = null };
                while (!token.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
                    if (_observed is not null && !_observed.IsStarted)
                        throw new IOException("The virtual interface stopped.");
                    if (api is null) continue;
                    VirtualNetworkClientConfigResponse latest;
                    try { latest = await api.GetVirtualNetworkConfigAsync(configuration.Token!, token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                    catch (Exception exception)
                    {
                        Log(new(ProxyNetworkerLogLevel.Warning, "Configuration refresh failed; retaining the current session.", exception));
                        continue;
                    }
                    if (latest != remote) break;
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            catch (PlatformNotSupportedException) { throw; }
            catch (Exception exception)
            {
                lock (_gate) _state = _state with { State = ClientConnectionState.Reconnecting, Error = Sanitize(exception.Message) };
                Log(new(ProxyNetworkerLogLevel.Warning, "Virtual network failed; retrying.", exception));
                await Task.Delay(delay, token).ConfigureAwait(false);
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 30));
            }
            finally
            {
                lock (_gate)
                {
                    _sentBase += _observed?.BytesSent ?? 0;
                    _receivedBase += _observed?.BytesReceived ?? 0;
                    _udp = null; _observed = null;
                }
            }
        }
    }

    private async Task<IDisposable> StartTunnelAsync(DirectVirtualNetworkClientConfig configuration, CancellationToken token)
    {
        if (configuration.TransportProtocol == "quic" && !platform.SupportsQuic)
            throw new PlatformNotSupportedException("QUIC is unavailable on this host. Select a UDP virtual network on the server.");
        var addresses = await Dns.GetHostAddressesAsync(configuration.ServerHost, token).ConfigureAwait(false);
        var address = addresses.FirstOrDefault(value => value.AddressFamily == AddressFamily.InterNetwork)
            ?? throw new IOException("The tunnel server has no IPv4 address.");
        var device = await platform.CreateTunAsync(new TunDeviceOptions
        {
            Name = OperatingSystem.IsLinux() && !OperatingSystem.IsAndroid()
                ? LinuxInterfaceName.Normalize(configuration.Name) : configuration.Name,
            Address = IPAddress.Parse(configuration.TunAddress),
            PrefixLength = configuration.PrefixLength, Mtu = configuration.Mtu
        }, Log, token).ConfigureAwait(false);
        var observed = new ObservedTun(device, collectTelemetry);
        IDisposable? tunnel = null;
        try
        {
            var options = new VirtualNetworkTunnelOptions
            {
                BindEndPoint = new(IPAddress.Any, configuration.BindPort),
                RemoteEndPoint = new(address, configuration.ServerPort),
                NodeAddress = IPAddress.Parse(configuration.TunAddress),
                PrefixLength = configuration.PrefixLength,
                NodeName = configuration.NodeDisplayName ?? configuration.Name,
                AuthenticationKey = configuration.AuthenticationKey,
                EncryptionEnabled = configuration.EncryptionEnabled,
                EnableDirectPaths = !configuration.RelayOnly,
                EnableStableDirectPathSelection = configuration.StableDirectPathSelection,
                ConfigureTransportSocket = platform.ConfigureTransportSocket
            };
            if (configuration.TransportProtocol == "quic")
            {
                var stream = new VirtualNetworkStreamTunnel(observed, options, configuration.TransportProtocol, Log);
                tunnel = stream;
                await stream.StartAsync(token).ConfigureAwait(false);
            }
            else
            {
                var udp = new VirtualNetworkTunnel(observed, options, Log);
                tunnel = udp;
                await udp.StartAsync(token).ConfigureAwait(false);
                lock (_gate) _udp = udp;
            }
            lock (_gate)
            {
                _observed = observed;
                _state = _state with
                {
                    VirtualAddress = $"{configuration.TunAddress}/{configuration.PrefixLength}",
                    InterfaceName = device.Name, Transport = configuration.TransportProtocol,
                    EncryptionEnabled = configuration.EncryptionEnabled
                };
            }
            return tunnel;
        }
        catch
        {
            if (tunnel is not null) tunnel.Dispose(); else observed.Dispose();
            throw;
        }
    }

    internal static DirectVirtualNetworkClientConfig FromRemote(
        VirtualNetworkClientConfigResponse remote, DirectVirtualNetworkClientConfig local, string server)
    {
        var key = Convert.FromBase64String(remote.AuthenticationKey);
        if (key.Length != 32) throw new InvalidDataException("Server returned an invalid session key.");
        var host = remote.ServerHost is "" or "0.0.0.0" or "::" or "*" ? ClientConfigurationStore.ValidateServer(server).Host : remote.ServerHost;
        return new()
        {
            Enabled = true, Name = local.Name, NodeDisplayName = local.NodeDisplayName,
            ServerHost = host, ServerPort = remote.ServerPort, BindPort = local.BindPort, TunAddress = remote.ClientAddress,
            PrefixLength = remote.PrefixLength, Mtu = remote.Mtu, AuthenticationKey = key,
            TransportProtocol = remote.TransportProtocol, EncryptionEnabled = remote.EncryptionEnabled ?? true,
            RelayOnly = local.RelayOnly, StableDirectPathSelection = local.StableDirectPathSelection
        };
    }

    private static IDisposable Acquire(string token) => TokenSingleInstanceLock.TryAcquire(token)
        ?? throw new InvalidOperationException("This Access Token is already running. Stop the existing client first.");

    private string Sanitize(string value)
    {
        foreach (var secret in _secrets) value = value.Replace(secret, "[redacted]", StringComparison.Ordinal);
        return value.Length > 4096 ? value[..4096] : value;
    }

    private void Log(ProxyNetworkerLogEntry entry) => LogReceived?.Invoke(new(
        DateTimeOffset.UtcNow, entry.Level.ToString(),
        Sanitize(entry.Message + (entry.Exception is null ? "" : " " + entry.Exception.Message))));

    private sealed class ObservedTun(ITunDevice inner, bool enabled) : ITunDevice, ITunBatchDevice
    {
        private long _sent, _received;
        public long BytesSent => Interlocked.Read(ref _sent);
        public long BytesReceived => Interlocked.Read(ref _received);
        public string Name => inner.Name;
        public bool IsStarted => inner.IsStarted;
        public event EventHandler<TunPacketReceivedEventArgs>? PacketReceived;
        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            inner.PacketReceived += OnPacket;
            return inner.StartAsync(cancellationToken);
        }
        private void OnPacket(object? sender, TunPacketReceivedEventArgs args)
        {
            if (enabled) Interlocked.Add(ref _sent, args.PacketMemory.Length);
            PacketReceived?.Invoke(this, args);
        }
        public async ValueTask SendAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken = default)
        {
            await inner.SendAsync(packet, cancellationToken).ConfigureAwait(false);
            if (enabled) Interlocked.Add(ref _received, packet.Length);
        }
        public async ValueTask SendBatchAsync(IReadOnlyList<ReadOnlyMemory<byte>> packets, CancellationToken cancellationToken = default)
        {
            if (inner is ITunBatchDevice batch) await batch.SendBatchAsync(packets, cancellationToken).ConfigureAwait(false);
            else foreach (var packet in packets) await inner.SendAsync(packet, cancellationToken).ConfigureAwait(false);
            if (enabled) Interlocked.Add(ref _received, packets.Sum(packet => (long)packet.Length));
        }
        public void Dispose() { inner.PacketReceived -= OnPacket; inner.Dispose(); }
    }
}
