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
        if (!IsKnownDirectEndpoint(frame.VirtualAddress, remoteEndPoint))
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
            await SendFrameAsync(frame, probe.EndPoint, cancellationToken).ConfigureAwait(false);
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

        return endpoints.ToArray();
    }

    private bool IsInVirtualNetwork(IPAddress address)
    {
        var mask = _options.PrefixLength == 0 ? 0u : uint.MaxValue << (32 - _options.PrefixLength);
        return (IPv4ToUInt32(address) & mask) == (IPv4ToUInt32(_options.NodeAddress) & mask);
    }

    private void UpdateDirectCandidates(IPAddress virtualAddress, IReadOnlyCollection<IPEndPoint> candidates)
    {
        if (virtualAddress.Equals(_options.NodeAddress))
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
            path.EwmaRttMs = path.EwmaRttMs is null
                ? rttMilliseconds
                : (path.EwmaRttMs.Value * 0.8) + (rttMilliseconds * 0.2);
            path.LastRttMs = rttMilliseconds;
            path.LastSuccess = measuredAt;
            path.Received++;
            SelectBestPathLocked(state);
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
                var targetEndPoint = remoteEndPoint;
                if (_options.EnableDirectPaths &&
                    TryReadIPv4Destination(packet, out var destination) &&
                    !IsBroadcastOrMulticast(destination))
                {
                    targetEndPoint = TryGetDirectEndPoint(destination) ?? remoteEndPoint;
                }

                await SendPacketFrameAsync(packet, targetEndPoint, CancellationToken.None).ConfigureAwait(false);
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
        var expiresBefore = DateTimeOffset.UtcNow - _options.PeerTimeout;
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
                Log(ProxyNetworkerLogLevel.Information, $"Virtual Network peer {peer.VirtualAddress} expired.");
            }
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

        public long LastRttMs { get; set; }

        public DateTimeOffset LastSuccess { get; set; }

        public DateTimeOffset LastProbeSent { get; set; }

        public int Received { get; set; }
    }
}
