using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace ProxyNetworker.Core;

public sealed class VirtualNetworkTunnel : IDisposable
{
    private readonly ITunDevice _tunDevice;
    private readonly VirtualNetworkTunnelOptions _options;
    private readonly Action<ProxyNetworkerLogEntry>? _logger;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly object _peersLock = new();
    private readonly Dictionary<string, VirtualNetworkPeer> _peersByAddress = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _addressByEndpoint = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PeerPathState> _directPeersByAddress = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (IPAddress Origin, VirtualNetworkMeshAdvertisement Advertisement)> _meshAdvertisements = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _lastForwardingPaths = new(StringComparer.OrdinalIgnoreCase);
    private VirtualNetworkMeshRouteTable _meshRoutes = VirtualNetworkMeshRouteTable.Empty;
    private long _meshAdvertisementSequence;
    private UdpClient? _udpClient;
    private IPEndPoint? _remoteEndPoint;
    private CancellationTokenSource? _runtimeCancellation;
    private Task? _receiveLoop;
    private Task? _heartbeatLoop;
    private int _started;

    public VirtualNetworkTunnel(
        ITunDevice tunDevice,
        VirtualNetworkTunnelOptions options,
        Action<ProxyNetworkerLogEntry>? logger = null)
    {
        _tunDevice = tunDevice ?? throw new ArgumentNullException(nameof(tunDevice));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger;
        _options.Validate();
        _remoteEndPoint = _options.RemoteEndPoint;
    }

    public bool IsServer => _options.RemoteEndPoint is null;

    public IPEndPoint? RemoteEndPoint
    {
        get
        {
            lock (_peersLock)
            {
                return _remoteEndPoint;
            }
        }
    }

    public int PeerCount
    {
        get
        {
            lock (_peersLock)
            {
                return _peersByAddress.Count;
            }
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (Interlocked.Exchange(ref _started, 1) == 1)
        {
            return;
        }

        try
        {
            _udpClient = new UdpClient(_options.BindEndPoint);
            _runtimeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _tunDevice.PacketReceived += OnTunPacketReceived;

            await _tunDevice.StartAsync(cancellationToken).ConfigureAwait(false);

            _receiveLoop = Task.Run(() => ReceiveLoopAsync(_runtimeCancellation.Token));
            _heartbeatLoop = Task.Run(() => HeartbeatLoopAsync(_runtimeCancellation.Token));

            if (!IsServer)
            {
                await SendControlFrameAsync(VirtualNetworkFrameType.Hello, _options.RemoteEndPoint!, cancellationToken).ConfigureAwait(false);
                if (_options.EnableDirectPaths)
                {
                    await SendLocalCandidatesAsync(cancellationToken).ConfigureAwait(false);
                    await SendMeshLinkStateAsync(cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    Log(ProxyNetworkerLogLevel.Information, "Virtual Network direct paths are disabled; using relay only.");
                }
            }

            var mode = IsServer ? "server" : "client";
            Log(ProxyNetworkerLogLevel.Information, $"Virtual Network {mode} listening on UDP {_options.BindEndPoint} as {_options.NodeAddress}/{_options.PrefixLength}.");
        }
        catch
        {
            Interlocked.Exchange(ref _started, 0);
            _tunDevice.PacketReceived -= OnTunPacketReceived;
            _udpClient?.Dispose();
            _udpClient = null;
            _runtimeCancellation?.Dispose();
            _runtimeCancellation = null;
            throw;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _started, 0) == 1)
        {
            _tunDevice.PacketReceived -= OnTunPacketReceived;
            _runtimeCancellation?.Cancel();
            _udpClient?.Dispose();

            try
            {
                Task.WaitAll(new[] { _receiveLoop, _heartbeatLoop }.Where(static item => item is not null).Cast<Task>().ToArray(), TimeSpan.FromSeconds(2));
            }
            catch (AggregateException ex) when (ex.InnerExceptions.All(static item => item is OperationCanceledException or ObjectDisposedException or SocketException))
            {
            }

            _runtimeCancellation?.Dispose();
        }

        _sendLock.Dispose();
        _tunDevice.Dispose();
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var udpClient = _udpClient;
                if (udpClient is null)
                {
                    return;
                }

                var received = await udpClient.ReceiveAsync().ConfigureAwait(false);
                if (received.Buffer.Length == 0 || received.Buffer.Length > _options.MaxPacketSize)
                {
                    continue;
                }

                if (!VirtualNetworkFrame.TryParse(received.Buffer, _options.AuthenticationKey, out var frame))
                {
                    Log(ProxyNetworkerLogLevel.Warning, $"Ignored malformed Virtual Network datagram from {received.RemoteEndPoint}.");
                    continue;
                }

                CleanupExpiredPeers();

                if (IsServer)
                {
                    await HandleServerFrameAsync(frame, received.RemoteEndPoint, cancellationToken).ConfigureAwait(false);
                }
                else if (IsConfiguredServer(received.RemoteEndPoint))
                {
                    await HandleClientFrameAsync(frame, received.RemoteEndPoint, cancellationToken).ConfigureAwait(false);
                }
                else if (!_options.EnableDirectPaths)
                {
                    Log(ProxyNetworkerLogLevel.Debug, "Ignored direct Virtual Network datagram because relay-only mode is enabled.");
                }
                else
                {
                    await HandleDirectFrameAsync(frame, received.RemoteEndPoint, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (SocketException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Log(ProxyNetworkerLogLevel.Error, "Virtual Network receive loop failed.", ex);
                return;
            }
        }
    }

    private async Task HeartbeatLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_options.HeartbeatInterval, cancellationToken).ConfigureAwait(false);

                if (IsServer)
                {
                    CleanupExpiredPeers();
                    continue;
                }

                var remoteEndPoint = _options.RemoteEndPoint;
                if (remoteEndPoint is not null)
                {
                    await SendControlFrameAsync(VirtualNetworkFrameType.Heartbeat, remoteEndPoint, cancellationToken).ConfigureAwait(false);
                    if (_options.EnableDirectPaths)
                    {
                        await ProbeDirectPeersAsync(cancellationToken).ConfigureAwait(false);
                        await SendMeshLinkStateAsync(cancellationToken).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Log(ProxyNetworkerLogLevel.Warning, "Virtual Network heartbeat failed.", ex);
            }
        }
    }

    private async Task HandleServerFrameAsync(VirtualNetworkFrame frame, IPEndPoint remoteEndPoint, CancellationToken cancellationToken)
    {
        switch (frame.Type)
        {
            case VirtualNetworkFrameType.Hello:
            case VirtualNetworkFrameType.Heartbeat:
                RegisterPeer(frame.VirtualAddress, remoteEndPoint);
                break;

            case VirtualNetworkFrameType.Packet:
                var sourceAddress = TryReadIPv4Source(frame.Payload.Span, out var parsedSource)
                    ? parsedSource
                    : frame.VirtualAddress;
                RegisterPeer(sourceAddress, remoteEndPoint);
                await RoutePacketAsync(frame.Payload, remoteEndPoint, cancellationToken).ConfigureAwait(false);
                break;
        }
    }

    private async Task HandleClientFrameAsync(VirtualNetworkFrame frame, IPEndPoint remoteEndPoint, CancellationToken cancellationToken)
    {
        var configuredRemote = _options.RemoteEndPoint;
        if (configuredRemote is null || !EndPointEquals(configuredRemote, remoteEndPoint))
        {
            Log(ProxyNetworkerLogLevel.Warning, $"Ignored Virtual Network datagram from unexpected peer {remoteEndPoint}.");
            return;
        }

        if (frame.Type == VirtualNetworkFrameType.PeerCandidates)
        {
            if (!_options.EnableDirectPaths)
            {
                return;
            }

            if (VirtualNetworkCandidateCodec.TryDecode(frame.Payload.Span, out var candidates))
            {
                UpdateDirectCandidates(frame.VirtualAddress, candidates);
            }
            else
            {
                Log(ProxyNetworkerLogLevel.Warning, $"Ignored malformed direct-path candidates for {frame.VirtualAddress}.");
            }
        }
        else if (frame.Type == VirtualNetworkFrameType.MeshLinkState)
        {
            if (_options.EnableDirectPaths)
            {
                HandleMeshLinkState(frame);
            }
        }
        else if (frame.Type == VirtualNetworkFrameType.MeshPacket)
        {
            await HandleMeshPacketAsync(frame, cancellationToken).ConfigureAwait(false);
        }
        else if (frame.Type == VirtualNetworkFrameType.Packet)
        {
            NotePathActivity(frame.VirtualAddress, remoteEndPoint);
            await _tunDevice.SendAsync(frame.Payload, cancellationToken).ConfigureAwait(false);
        }
        else if (frame.Type == VirtualNetworkFrameType.PathProbe)
        {
            if (VirtualNetworkPathProbe.TryParse(frame.Payload.Span, out var probe) && probe.DestinationAddress.Equals(_options.NodeAddress))
            {
                var response = VirtualNetworkFrame.Create(
                    VirtualNetworkFrameType.PathProbeAck,
                    _options.NodeAddress,
                    frame.Payload.Span,
                    _options.AuthenticationKey);
                await SendFrameAsync(response, remoteEndPoint, cancellationToken).ConfigureAwait(false);
            }
        }
        else if (frame.Type == VirtualNetworkFrameType.PathProbeAck &&
            VirtualNetworkPathProbe.TryParse(frame.Payload.Span, out var probeAck))
        {
            RecordPathMeasurement(frame.VirtualAddress, remoteEndPoint, probeAck);
        }
    }

    private async Task HandleDirectFrameAsync(VirtualNetworkFrame frame, IPEndPoint remoteEndPoint, CancellationToken cancellationToken)
    {
        var isKnownPath = frame.Type == VirtualNetworkFrameType.MeshPacket
            ? IsKnownDirectEndpoint(remoteEndPoint)
            : IsKnownDirectEndpoint(frame.VirtualAddress, remoteEndPoint);
        if (!isKnownPath)
        {
            Log(ProxyNetworkerLogLevel.Debug, $"Ignored direct Virtual Network datagram from unexpected endpoint {remoteEndPoint}.");
            return;
        }

        switch (frame.Type)
        {
            case VirtualNetworkFrameType.PathProbe:
                if (VirtualNetworkPathProbe.TryParse(frame.Payload.Span, out var probe) && probe.DestinationAddress.Equals(_options.NodeAddress))
                {
                    var response = VirtualNetworkFrame.Create(
                        VirtualNetworkFrameType.PathProbeAck,
                        _options.NodeAddress,
                        frame.Payload.Span,
                        _options.AuthenticationKey);
                    await SendFrameAsync(response, remoteEndPoint, cancellationToken).ConfigureAwait(false);
                }
                break;

            case VirtualNetworkFrameType.PathProbeAck:
                if (VirtualNetworkPathProbe.TryParse(frame.Payload.Span, out var probeAck))
                {
                    RecordPathMeasurement(frame.VirtualAddress, remoteEndPoint, probeAck);
                }
                break;

            case VirtualNetworkFrameType.MeshLinkState:
                HandleMeshLinkState(frame);
                break;

            case VirtualNetworkFrameType.MeshPacket:
                await HandleMeshPacketAsync(frame, cancellationToken).ConfigureAwait(false);
                break;

            case VirtualNetworkFrameType.Packet:
                NotePathActivity(frame.VirtualAddress, remoteEndPoint);
                await _tunDevice.SendAsync(frame.Payload, cancellationToken).ConfigureAwait(false);
                break;
        }
    }

    private async Task SendLocalCandidatesAsync(CancellationToken cancellationToken)
    {
        var remoteEndPoint = _options.RemoteEndPoint;
        var udpClient = _udpClient;
        if (remoteEndPoint is null || udpClient?.Client.LocalEndPoint is not IPEndPoint localEndPoint)
        {
            return;
        }

        var candidates = GetLocalCandidateEndPoints(localEndPoint.Port);
        var payload = VirtualNetworkCandidateCodec.Encode(candidates);
        var frame = VirtualNetworkFrame.Create(
            VirtualNetworkFrameType.PeerCandidates,
            _options.NodeAddress,
            payload,
            _options.AuthenticationKey);
        await SendFrameAsync(frame, remoteEndPoint, cancellationToken).ConfigureAwait(false);
    }

    private async Task SendMeshLinkStateAsync(CancellationToken cancellationToken)
    {
        var remoteEndPoint = _options.RemoteEndPoint;
        if (remoteEndPoint is null || !_options.EnableDirectPaths)
        {
            return;
        }

        RefreshMeshRoutes();
        var sequence = Interlocked.Increment(ref _meshAdvertisementSequence);
        var links = GetLocalMeshLinks();
        var advertisement = new VirtualNetworkMeshAdvertisement(sequence, links, DateTimeOffset.UtcNow);
        var frame = VirtualNetworkFrame.Create(
            VirtualNetworkFrameType.MeshLinkState,
            _options.NodeAddress,
            advertisement.Encode(),
            _options.AuthenticationKey);
        await SendFrameAsync(frame, remoteEndPoint, cancellationToken).ConfigureAwait(false);
    }

    private void HandleMeshLinkState(VirtualNetworkFrame frame)
    {
        if (frame.VirtualAddress.Equals(_options.NodeAddress))
        {
            return;
        }

        if (!VirtualNetworkMeshAdvertisement.TryDecode(frame.Payload.Span, out var advertisement))
        {
            Log(ProxyNetworkerLogLevel.Warning, $"Ignored malformed mesh link state from {frame.VirtualAddress}.");
            return;
        }

        lock (_peersLock)
        {
            var key = AddressKey(frame.VirtualAddress);
            if (_meshAdvertisements.TryGetValue(key, out var existing) &&
                existing.Advertisement.Sequence >= advertisement.Sequence)
            {
                return;
            }

            _meshAdvertisements[key] = (frame.VirtualAddress, advertisement);
            RebuildMeshRoutesLocked();
        }
    }

    private async Task HandleMeshPacketAsync(VirtualNetworkFrame frame, CancellationToken cancellationToken)
    {
        if (!VirtualNetworkMeshPacket.TryParse(frame.Payload.Span, out var meshPacket))
        {
            Log(ProxyNetworkerLogLevel.Warning, $"Ignored malformed mesh packet from {frame.VirtualAddress}.");
            return;
        }

        if (meshPacket.Destination.Equals(_options.NodeAddress))
        {
            await _tunDevice.SendAsync(meshPacket.Payload, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!meshPacket.TryCreateForwarded(out var forwardedPayload))
        {
            Log(ProxyNetworkerLogLevel.Debug, $"Dropped mesh packet from {frame.VirtualAddress} to {meshPacket.Destination} after reaching the hop limit.");
            return;
        }

        await SendMeshPacketAsync(
            frame.VirtualAddress,
            meshPacket.Destination,
            forwardedPayload,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task SendRoutedPacketAsync(
        ReadOnlyMemory<byte> packet,
        IPAddress destination,
        CancellationToken cancellationToken)
    {
        var decision = SelectForwardingDecision(destination);
        if (decision is null)
        {
            var relay = _options.RemoteEndPoint;
            if (relay is not null)
            {
                await SendPacketFrameAsync(packet, relay, cancellationToken).ConfigureAwait(false);
            }
            return;
        }

        if (decision.IsRelay)
        {
            await SendPacketFrameAsync(packet, decision.EndPoint, cancellationToken).ConfigureAwait(false);
            return;
        }

        var meshPayload = VirtualNetworkMeshPacket.Create(destination, packet.Span);
        await SendMeshPacketFrameAsync(
            _options.NodeAddress,
            meshPayload,
            decision.EndPoint,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task SendMeshPacketAsync(
        IPAddress sourceAddress,
        IPAddress destination,
        byte[] meshPayload,
        CancellationToken cancellationToken)
    {
        var decision = SelectForwardingDecision(destination);
        if (decision is null)
        {
            return;
        }

        await SendMeshPacketFrameAsync(
            sourceAddress,
            meshPayload,
            decision.EndPoint,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task SendMeshPacketFrameAsync(
        IPAddress sourceAddress,
        byte[] meshPayload,
        IPEndPoint nextHopEndPoint,
        CancellationToken cancellationToken)
    {
        var frame = VirtualNetworkFrame.Create(
            VirtualNetworkFrameType.MeshPacket,
            sourceAddress,
            meshPayload,
            _options.AuthenticationKey);
        await SendFrameAsync(frame, nextHopEndPoint, cancellationToken).ConfigureAwait(false);
    }

    private ForwardingDecision? SelectForwardingDecision(IPAddress destination)
    {
        lock (_peersLock)
        {
            var relayPath = TryGetBestRelayPathLocked(destination);
            VirtualNetworkMeshRoute? meshRoute = null;
            if (!_meshRoutes.TryGetRoute(destination, out var route))
            {
                if (TryGetBestDirectPathLocked(destination) is null)
                {
                    return relayPath is null && _options.RemoteEndPoint is null
                        ? null
                        : new ForwardingDecision(_options.RemoteEndPoint!, true, relayPath?.ScoreMilliseconds, "relay");
                }
            }
            else
            {
                meshRoute = route;
            }

            var nextHopAddress = meshRoute?.NextHop ?? destination;
            var directPath = TryGetBestDirectPathLocked(nextHopAddress);
            var meshCost = meshRoute?.CostMilliseconds ?? directPath?.ScoreMilliseconds;
            var relayScore = relayPath?.ScoreMilliseconds;
            var preferMesh = directPath is not null &&
                (relayScore is null || meshCost is null || meshCost <= relayScore + 3);
            if (preferMesh && directPath is not null)
            {
                var path = meshRoute?.Path ?? $"{_options.NodeAddress} -> {destination}";
                var signature = $"mesh:{path}";
                if (!_lastForwardingPaths.TryGetValue(AddressKey(destination), out var previous) || previous != signature)
                {
                    _lastForwardingPaths[AddressKey(destination)] = signature;
                    var pathType = meshRoute is { HopCount: > 1 } ? "mesh" : "direct";
                    Log(ProxyNetworkerLogLevel.Information,
                        $"Virtual Network {pathType} path selected for {destination} via {directPath.EndPoint} ({meshCost:0.0} ms; route {path}).");
                }

                return new ForwardingDecision(directPath.EndPoint, false, meshCost, path);
            }

            var relay = _options.RemoteEndPoint;
            if (relay is not null)
            {
                var signature = "relay";
                if (!_lastForwardingPaths.TryGetValue(AddressKey(destination), out var previous) || previous != signature)
                {
                    _lastForwardingPaths[AddressKey(destination)] = signature;
                    var rtt = relayScore is null ? "pending" : $"{relayScore.Value:0.0} ms";
                    Log(ProxyNetworkerLogLevel.Information,
                        $"Virtual Network relay path selected for {destination} via {relay} ({rtt}).");
                }

                return new ForwardingDecision(relay, true, relayScore, "relay");
            }
        }

        return null;
    }

    private void RefreshMeshRoutes()
    {
        lock (_peersLock)
        {
            RebuildMeshRoutesLocked();
        }
    }

    private void RebuildMeshRoutesLocked()
    {
        _meshRoutes = VirtualNetworkMeshRouteTable.Build(
            _options.NodeAddress,
            GetLocalMeshLinksLocked(),
            _meshAdvertisements,
            DateTimeOffset.UtcNow,
            TimeSpan.FromTicks(Math.Max(_options.PeerTimeout.Ticks * 2, _options.PeerTimeout.Ticks)));
    }

    private VirtualNetworkMeshLink[] GetLocalMeshLinks()
    {
        lock (_peersLock)
        {
            return GetLocalMeshLinksLocked();
        }
    }

    private VirtualNetworkMeshLink[] GetLocalMeshLinksLocked()
    {
        var now = DateTimeOffset.UtcNow;
        var links = new List<VirtualNetworkMeshLink>();
        foreach (var state in _directPeersByAddress.Values)
        {
            var best = state.Paths.Values
                .Where(path => !path.IsRelay && path.EwmaRttMs is not null && now - path.LastSuccess <= _options.PeerTimeout)
                .OrderBy(static path => path.ScoreMilliseconds)
                .FirstOrDefault();
            if (best is null)
            {
                continue;
            }

            var lossBasisPoints = best.ProbesSent <= 0
                ? 0
                : Math.Clamp((int)((Math.Max(best.ProbesSent - best.ProbesReceived, 0) * 10_000L) / best.ProbesSent), 0, 10_000);
            links.Add(VirtualNetworkMeshLink.Create(
                state.VirtualAddress,
                (int)Math.Round(best.EwmaRttMs!.Value, MidpointRounding.AwayFromZero),
                (int)Math.Round(best.JitterMilliseconds, MidpointRounding.AwayFromZero),
                lossBasisPoints));
        }

        return links.ToArray();
    }

    private PathMeasurement? TryGetBestDirectPathLocked(IPAddress virtualAddress)
    {
        var now = DateTimeOffset.UtcNow;
        return _directPeersByAddress.TryGetValue(AddressKey(virtualAddress), out var state)
            ? state.Paths.Values
                .Where(path => !path.IsRelay && path.EwmaRttMs is not null && now - path.LastSuccess <= _options.PeerTimeout)
                .OrderBy(static path => path.ScoreMilliseconds)
                .FirstOrDefault()
            : null;
    }

    private PathMeasurement? TryGetBestRelayPathLocked(IPAddress virtualAddress)
    {
        var now = DateTimeOffset.UtcNow;
        return _directPeersByAddress.TryGetValue(AddressKey(virtualAddress), out var state)
            ? state.Paths.Values
                .Where(path => path.IsRelay && path.EwmaRttMs is not null && now - path.LastSuccess <= _options.PeerTimeout)
                .OrderBy(static path => path.ScoreMilliseconds)
                .FirstOrDefault()
            : null;
    }

    private async Task ProbeDirectPeersAsync(CancellationToken cancellationToken)
    {
        List<(IPAddress VirtualAddress, IPEndPoint EndPoint)> probes = new();
        var now = DateTimeOffset.UtcNow;
        lock (_peersLock)
        {
            foreach (var state in _directPeersByAddress.Values)
            {
                foreach (var path in state.Paths.Values)
                {
                    path.LastProbeSent = now;
                    path.ProbesSent++;
                    if (path.ProbesSent > 256)
                    {
                        path.ProbesSent /= 2;
                        path.ProbesReceived /= 2;
                    }
                    probes.Add((state.VirtualAddress, path.EndPoint));
                }
            }
        }

        foreach (var probe in probes)
        {
            if (probe.VirtualAddress.Equals(_options.NodeAddress))
            {
                continue;
            }

            var payload = VirtualNetworkPathProbe.Create(probe.VirtualAddress);
            var frame = VirtualNetworkFrame.Create(
                VirtualNetworkFrameType.PathProbe,
                _options.NodeAddress,
                payload,
                _options.AuthenticationKey);
            try
            {
                await SendFrameAsync(frame, probe.EndPoint, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
            {
                Log(ProxyNetworkerLogLevel.Debug,
                    $"Virtual Network path probe to {probe.EndPoint} for {probe.VirtualAddress} failed: {ex.Message}");
            }
        }
    }

    private IPEndPoint[] GetLocalCandidateEndPoints(int port)
    {
        var endpoints = new List<IPEndPoint>();
        foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (networkInterface.OperationalStatus != OperationalStatus.Up ||
                networkInterface.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
            {
                continue;
            }

            foreach (var unicast in networkInterface.GetIPProperties().UnicastAddresses)
            {
                var address = unicast.Address;
                if (address.AddressFamily != AddressFamily.InterNetwork ||
                    IPAddress.IsLoopback(address) ||
                    address.Equals(IPAddress.Any) ||
                    IsInVirtualNetwork(address))
                {
                    continue;
                }

                var bytes = address.GetAddressBytes();
                if (bytes[0] == 169 && bytes[1] == 254)
                {
                    continue;
                }

                endpoints.Add(new IPEndPoint(address, port));
            }
        }

        endpoints.AddRange(_options.AdditionalCandidateEndPoints);
        return endpoints
            .DistinctBy(static endpoint => endpoint.ToString(), StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private bool IsInVirtualNetwork(IPAddress address)
    {
        var mask = _options.PrefixLength == 0 ? 0u : uint.MaxValue << (32 - _options.PrefixLength);
        return (IPv4ToUInt32(address) & mask) == (IPv4ToUInt32(_options.NodeAddress) & mask);
    }

    private void UpdateDirectCandidates(IPAddress virtualAddress, IReadOnlyCollection<IPEndPoint> candidates)
    {
        if (virtualAddress.Equals(_options.NodeAddress) ||
            _options.DirectPathExcludedAddresses.Contains(virtualAddress))
        {
            return;
        }

        lock (_peersLock)
        {
            if (!_directPeersByAddress.TryGetValue(AddressKey(virtualAddress), out var state))
            {
                state = new PeerPathState(virtualAddress);
                _directPeersByAddress[AddressKey(virtualAddress)] = state;
            }

            var previous = state.Candidates.ToArray();
            state.Candidates.Clear();
            state.Candidates.AddRange(candidates.Where(static endpoint => endpoint.Port > 0).DistinctBy(static endpoint => endpoint.ToString()));
            var relayEndPoint = _options.RemoteEndPoint;
            if (relayEndPoint is not null)
            {
                GetOrCreatePathLocked(state, relayEndPoint, isRelay: true);
            }

            foreach (var candidate in state.Candidates)
            {
                GetOrCreatePathLocked(state, candidate, isRelay: false);
            }

            var validEndpoints = state.Candidates
                .Append(relayEndPoint)
                .Where(static endpoint => endpoint is not null)
                .Select(static endpoint => EndPointKey(endpoint!))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var stalePath in state.Paths.Keys.Where(key => !validEndpoints.Contains(key)).ToArray())
            {
                state.Paths.Remove(stalePath);
            }

            if (!previous.SequenceEqual(state.Candidates))
            {
                Log(ProxyNetworkerLogLevel.Information,
                    $"Virtual Network direct candidates for {virtualAddress}: {string.Join(", ", state.Candidates)}.");
            }

            SelectBestPathLocked(state);
        }
    }

    private bool IsKnownDirectEndpoint(IPAddress virtualAddress, IPEndPoint remoteEndPoint)
    {
        lock (_peersLock)
        {
            return _directPeersByAddress.TryGetValue(AddressKey(virtualAddress), out var state) &&
                state.Candidates.Any(candidate => EndPointEquals(candidate, remoteEndPoint));
        }
    }

    private bool IsKnownDirectEndpoint(IPEndPoint remoteEndPoint)
    {
        lock (_peersLock)
        {
            return _directPeersByAddress.Values.Any(state =>
                state.Candidates.Any(candidate => EndPointEquals(candidate, remoteEndPoint)));
        }
    }

    private void RecordPathMeasurement(IPAddress virtualAddress, IPEndPoint remoteEndPoint, VirtualNetworkPathProbe probe)
    {
        var measuredAt = DateTimeOffset.UtcNow;
        var rttMilliseconds = measuredAt.ToUnixTimeMilliseconds() - probe.TimestampMilliseconds;
        if (rttMilliseconds < 0 || rttMilliseconds > 60_000)
        {
            return;
        }

        lock (_peersLock)
        {
            if (!_directPeersByAddress.TryGetValue(AddressKey(virtualAddress), out var state))
            {
                return;
            }

            var isRelay = IsConfiguredServer(remoteEndPoint);
            var path = GetOrCreatePathLocked(state, remoteEndPoint, isRelay);
            var previousRtt = path.LastRttMs > 0 ? path.LastRttMs : rttMilliseconds;
            var jitterSample = Math.Abs(rttMilliseconds - previousRtt);
            path.EwmaRttMs = path.EwmaRttMs is null
                ? rttMilliseconds
                : (path.EwmaRttMs.Value * 0.8) + (rttMilliseconds * 0.2);
            path.JitterMilliseconds = path.Received == 0
                ? jitterSample
                : (path.JitterMilliseconds * 0.8) + (jitterSample * 0.2);
            path.LastRttMs = rttMilliseconds;
            path.LastSuccess = measuredAt;
            path.ProbesReceived++;
            path.Received++;
            SelectBestPathLocked(state);
            RebuildMeshRoutesLocked();
        }
    }

    private void NotePathActivity(IPAddress virtualAddress, IPEndPoint remoteEndPoint)
    {
        lock (_peersLock)
        {
            if (!_directPeersByAddress.TryGetValue(AddressKey(virtualAddress), out var state))
            {
                return;
            }

            var path = GetOrCreatePathLocked(state, remoteEndPoint, IsConfiguredServer(remoteEndPoint));
            path.LastSuccess = DateTimeOffset.UtcNow;
        }
    }

    private static PathMeasurement GetOrCreatePathLocked(PeerPathState state, IPEndPoint endPoint, bool isRelay)
    {
        var key = EndPointKey(endPoint);
        if (!state.Paths.TryGetValue(key, out var path))
        {
            path = new PathMeasurement(endPoint, isRelay);
            state.Paths[key] = path;
        }

        return path;
    }

    private void SelectBestPathLocked(PeerPathState state)
    {
        var now = DateTimeOffset.UtcNow;
        var relay = _options.RemoteEndPoint is null
            ? null
            : state.Paths.GetValueOrDefault(EndPointKey(_options.RemoteEndPoint));
        var best = state.Paths.Values
            .Where(path => path.EwmaRttMs is not null && now - path.LastSuccess <= _options.PeerTimeout)
            .OrderBy(path => path.EwmaRttMs)
            .ThenBy(path => path.IsRelay)
            .FirstOrDefault() ?? relay;
        if (best is null)
        {
            return;
        }

        var changed = state.ActiveEndPoint is null || !EndPointEquals(state.ActiveEndPoint, best.EndPoint);
        state.ActiveEndPoint = best.EndPoint;
        state.LastDirectSeen = best.LastSuccess;
        if (changed)
        {
            var pathType = best.IsRelay ? "relay" : "direct";
            var rtt = best.EwmaRttMs is null ? "pending" : $"{best.EwmaRttMs.Value:0.0} ms";
            Log(ProxyNetworkerLogLevel.Information,
                $"Virtual Network {pathType} path selected for {state.VirtualAddress} via {best.EndPoint} ({rtt}).");
        }
    }

    private IPEndPoint? TryGetDirectEndPoint(IPAddress virtualAddress)
    {
        lock (_peersLock)
        {
            if (_directPeersByAddress.TryGetValue(AddressKey(virtualAddress), out var state) &&
                state.ActiveEndPoint is not null &&
                DateTimeOffset.UtcNow - state.LastDirectSeen <= _options.PeerTimeout)
            {
                return state.ActiveEndPoint;
            }
        }

        return null;
    }

    private bool IsConfiguredServer(IPEndPoint remoteEndPoint)
    {
        return _options.RemoteEndPoint is not null && EndPointEquals(_options.RemoteEndPoint, remoteEndPoint);
    }

    private void OnTunPacketReceived(object? sender, TunPacketReceivedEventArgs e)
    {
        _ = HandleTunPacketAsync(e.Packet);
    }

    private async Task HandleTunPacketAsync(byte[] packet)
    {
        try
        {
            if (IsServer)
            {
                await RoutePacketAsync(packet, sourcePeer: null, CancellationToken.None).ConfigureAwait(false);
                return;
            }

            var remoteEndPoint = _options.RemoteEndPoint;
            if (remoteEndPoint is not null)
            {
                if (!_options.EnableDirectPaths ||
                    !TryReadIPv4Destination(packet, out var destination) ||
                    IsBroadcastOrMulticast(destination))
                {
                    await SendPacketFrameAsync(packet, remoteEndPoint, CancellationToken.None).ConfigureAwait(false);
                    return;
                }

                await SendRoutedPacketAsync(packet, destination, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            Log(ProxyNetworkerLogLevel.Warning, "Failed to process packet from TUN device.", ex);
        }
    }

    private async Task RoutePacketAsync(ReadOnlyMemory<byte> packet, IPEndPoint? sourcePeer, CancellationToken cancellationToken)
    {
        if (!TryReadIPv4Destination(packet.Span, out var destination))
        {
            return;
        }

        if (IsBroadcastOrMulticast(destination))
        {
            if (sourcePeer is not null)
            {
                await _tunDevice.SendAsync(packet, cancellationToken).ConfigureAwait(false);
            }

            await SendPacketToPeersAsync(packet, exceptPeer: sourcePeer, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (destination.Equals(_options.NodeAddress))
        {
            if (sourcePeer is not null)
            {
                await _tunDevice.SendAsync(packet, cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        var peer = FindPeer(destination);
        if (peer is not null && !IsSamePeer(peer.EndPoint, sourcePeer))
        {
            await SendPacketFrameAsync(packet, peer.EndPoint, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task SendPacketToPeersAsync(ReadOnlyMemory<byte> packet, IPEndPoint? exceptPeer, CancellationToken cancellationToken)
    {
        VirtualNetworkPeer[] peers;
        lock (_peersLock)
        {
            peers = _peersByAddress.Values.ToArray();
        }

        foreach (var peer in peers)
        {
            if (IsSamePeer(peer.EndPoint, exceptPeer))
            {
                continue;
            }

            await SendPacketFrameAsync(packet, peer.EndPoint, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task SendControlFrameAsync(VirtualNetworkFrameType type, IPEndPoint remoteEndPoint, CancellationToken cancellationToken)
    {
        var nodeName = string.IsNullOrWhiteSpace(_options.NodeName) ? _options.NodeAddress.ToString() : _options.NodeName;
        var payload = VirtualNetworkNodeInfo.FromEnvironment(nodeName).Encode();
        var frame = VirtualNetworkFrame.Create(type, _options.NodeAddress, payload, _options.AuthenticationKey);
        await SendFrameAsync(frame, remoteEndPoint, cancellationToken).ConfigureAwait(false);
    }

    private async Task SendPacketFrameAsync(ReadOnlyMemory<byte> packet, IPEndPoint remoteEndPoint, CancellationToken cancellationToken)
    {
        var frame = VirtualNetworkFrame.Create(VirtualNetworkFrameType.Packet, _options.NodeAddress, packet.Span, _options.AuthenticationKey);
        await SendFrameAsync(frame, remoteEndPoint, cancellationToken).ConfigureAwait(false);
    }

    private async Task SendFrameAsync(byte[] frame, IPEndPoint remoteEndPoint, CancellationToken cancellationToken)
    {
        var udpClient = _udpClient;
        if (udpClient is null)
        {
            return;
        }

        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await udpClient.SendAsync(frame, frame.Length, remoteEndPoint).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private void RegisterPeer(IPAddress virtualAddress, IPEndPoint remoteEndPoint)
    {
        if (virtualAddress.Equals(_options.NodeAddress))
        {
            return;
        }

        var addressKey = AddressKey(virtualAddress);
        var endpointKey = EndPointKey(remoteEndPoint);

        lock (_peersLock)
        {
            if (_addressByEndpoint.TryGetValue(endpointKey, out var oldAddressKey) && oldAddressKey != addressKey)
            {
                _peersByAddress.Remove(oldAddressKey);
            }

            _addressByEndpoint[endpointKey] = addressKey;

            if (_peersByAddress.TryGetValue(addressKey, out var peer))
            {
                peer.EndPoint = remoteEndPoint;
                peer.LastSeen = DateTimeOffset.UtcNow;
                return;
            }

            _peersByAddress[addressKey] = new VirtualNetworkPeer(virtualAddress, remoteEndPoint);
            Log(ProxyNetworkerLogLevel.Information, $"Virtual Network peer {virtualAddress} registered from {remoteEndPoint}.");
        }
    }

    private VirtualNetworkPeer? FindPeer(IPAddress virtualAddress)
    {
        lock (_peersLock)
        {
            return _peersByAddress.TryGetValue(AddressKey(virtualAddress), out var peer) ? peer : null;
        }
    }

    private void CleanupExpiredPeers()
    {
        var now = DateTimeOffset.UtcNow;
        var expiresBefore = now - _options.PeerTimeout;
        var advertisementExpiresBefore = now - TimeSpan.FromTicks(_options.PeerTimeout.Ticks * 2);
        var expired = new List<string>();

        lock (_peersLock)
        {
            foreach (var item in _peersByAddress)
            {
                if (item.Value.LastSeen < expiresBefore)
                {
                    expired.Add(item.Key);
                }
            }

            foreach (var key in expired)
            {
                var peer = _peersByAddress[key];
                _addressByEndpoint.Remove(EndPointKey(peer.EndPoint));
                _peersByAddress.Remove(key);
                _lastForwardingPaths.Remove(key);
                Log(ProxyNetworkerLogLevel.Information, $"Virtual Network peer {peer.VirtualAddress} expired.");
            }

            foreach (var key in _meshAdvertisements
                .Where(item => item.Value.Advertisement.ReceivedAt < advertisementExpiresBefore)
                .Select(static item => item.Key)
                .ToArray())
            {
                _meshAdvertisements.Remove(key);
            }

            RebuildMeshRoutesLocked();
        }
    }

    private bool IsBroadcastOrMulticast(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes[0] >= 224 || address.Equals(IPAddress.Broadcast) || address.Equals(GetSubnetBroadcastAddress());
    }

    private IPAddress GetSubnetBroadcastAddress()
    {
        var address = IPv4ToUInt32(_options.NodeAddress);
        var mask = _options.PrefixLength == 0 ? 0u : uint.MaxValue << (32 - _options.PrefixLength);
        return UInt32ToIPv4(address | ~mask);
    }

    private static bool TryReadIPv4Source(ReadOnlySpan<byte> packet, out IPAddress address)
    {
        return TryReadIPv4Address(packet, 12, out address);
    }

    private static bool TryReadIPv4Destination(ReadOnlySpan<byte> packet, out IPAddress address)
    {
        return TryReadIPv4Address(packet, 16, out address);
    }

    private static bool TryReadIPv4Address(ReadOnlySpan<byte> packet, int offset, out IPAddress address)
    {
        address = IPAddress.None;
        if (packet.Length < 20 || (packet[0] >> 4) != 4 || packet.Length < offset + 4)
        {
            return false;
        }

        address = new IPAddress(new[] { packet[offset], packet[offset + 1], packet[offset + 2], packet[offset + 3] });
        return true;
    }

    private static uint IPv4ToUInt32(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
    }

    private static IPAddress UInt32ToIPv4(uint value)
    {
        return new IPAddress(new[]
        {
            (byte)(value >> 24),
            (byte)(value >> 16),
            (byte)(value >> 8),
            (byte)value
        });
    }

    private static bool IsSamePeer(IPEndPoint left, IPEndPoint? right)
    {
        return right is not null && EndPointEquals(left, right);
    }

    private static bool EndPointEquals(IPEndPoint left, IPEndPoint right)
    {
        return left.Port == right.Port && left.Address.Equals(right.Address);
    }

    private static string AddressKey(IPAddress address)
    {
        return address.ToString();
    }

    private static string EndPointKey(IPEndPoint endPoint)
    {
        return endPoint.ToString();
    }

    private void Log(ProxyNetworkerLogLevel level, string message, Exception? exception = null)
    {
        _logger?.Invoke(new ProxyNetworkerLogEntry(level, message, exception));
    }

    private sealed class VirtualNetworkPeer
    {
        public VirtualNetworkPeer(IPAddress virtualAddress, IPEndPoint endPoint)
        {
            VirtualAddress = virtualAddress;
            EndPoint = endPoint;
            LastSeen = DateTimeOffset.UtcNow;
        }

        public IPAddress VirtualAddress { get; }

        public IPEndPoint EndPoint { get; set; }

        public DateTimeOffset LastSeen { get; set; }
    }

    private sealed class PeerPathState
    {
        public PeerPathState(IPAddress virtualAddress)
        {
            VirtualAddress = virtualAddress;
        }

        public IPAddress VirtualAddress { get; }

        public List<IPEndPoint> Candidates { get; } = new();

        public Dictionary<string, PathMeasurement> Paths { get; } = new(StringComparer.OrdinalIgnoreCase);

        public IPEndPoint? ActiveEndPoint { get; set; }

        public DateTimeOffset LastDirectSeen { get; set; }
    }

    private sealed class PathMeasurement
    {
        public PathMeasurement(IPEndPoint endPoint, bool isRelay)
        {
            EndPoint = endPoint;
            IsRelay = isRelay;
        }

        public IPEndPoint EndPoint { get; }

        public bool IsRelay { get; }

        public double? EwmaRttMs { get; set; }

        public double JitterMilliseconds { get; set; }

        public long LastRttMs { get; set; }

        public DateTimeOffset LastSuccess { get; set; }

        public DateTimeOffset LastProbeSent { get; set; }

        public int ProbesSent { get; set; }

        public int ProbesReceived { get; set; }

        public int Received { get; set; }

        public double ScoreMilliseconds => EwmaRttMs is null
            ? double.MaxValue
            : VirtualNetworkMeshLink.CalculateCost(
                (int)Math.Round(EwmaRttMs.Value, MidpointRounding.AwayFromZero),
                (int)Math.Round(JitterMilliseconds, MidpointRounding.AwayFromZero),
                ProbesSent <= 0
                    ? 0
                    : Math.Clamp((int)((Math.Max(ProbesSent - ProbesReceived, 0) * 10_000L) / ProbesSent), 0, 10_000));
    }

    private sealed record ForwardingDecision(
        IPEndPoint EndPoint,
        bool IsRelay,
        double? CostMilliseconds,
        string Path);
}
