using System.Buffers;
using System.Net;
using System.Net.Sockets;
using ProxyNetworker.Core.Platforms.Linux;

namespace ProxyNetworker.Core;

public sealed class VirtualNetworkRelay : IVirtualNetworkRelay
{
    private readonly VirtualNetworkTunnelOptions _options;
    private readonly Action<ProxyNetworkerLogEntry>? _logger;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly object _peersLock = new();
    private readonly Dictionary<string, VirtualNetworkPeer> _peersByAddress = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _addressByEndpoint = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, MeshAdvertisementObservation> _meshAdvertisements = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, MeshTrafficObservation> _meshTrafficReports = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TopologyDirectLinkObservation> _topologyDirectLinks = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _bannedAddresses = new(StringComparer.OrdinalIgnoreCase);
    private UdpClient? _udpClient;
    private LinuxUdpBatchSocket? _linuxUdpBatchSocket;
    private CancellationTokenSource? _runtimeCancellation;
    private Task? _receiveLoop;
    private Task? _cleanupLoop;
    private int _started;

    public VirtualNetworkRelay(
        VirtualNetworkTunnelOptions options,
        Action<ProxyNetworkerLogEntry>? logger = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger;
        _options.RemoteEndPoint = null;
        _options.Validate();
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

    public void SetPeerBanned(IPAddress virtualAddress, bool isBanned)
    {
        lock (_peersLock)
        {
            var key = AddressKey(virtualAddress);
            if (isBanned)
            {
                _bannedAddresses.Add(key);
                if (_peersByAddress.Remove(key, out var peer))
                {
                    _addressByEndpoint.Remove(EndPointKey(peer.EndPoint));
                    _meshAdvertisements.Remove(key);
                    _meshTrafficReports.Remove(key);
                    RemoveTopologyDirectLinksLocked(key);
                }
            }
            else
            {
                _bannedAddresses.Remove(key);
            }
        }
    }

    public IReadOnlyList<VirtualNetworkPeerSnapshot> GetPeerSnapshots()
    {
        lock (_peersLock)
        {
            return _peersByAddress.Values
                .Select(static peer => peer.ToSnapshot())
                .OrderBy(static peer => peer.VirtualAddress.ToString())
                .ToArray();
        }
    }

    public VirtualNetworkTopologySnapshot GetTopologySnapshot()
    {
        var capturedAt = DateTimeOffset.UtcNow;
        var serverInfo = VirtualNetworkNodeInfo.FromEnvironment(
            string.IsNullOrWhiteSpace(_options.NodeName) ? _options.NodeAddress.ToString() : _options.NodeName);
        var nodes = new List<VirtualNetworkTopologyNodeSnapshot>
        {
            new(
                _options.NodeAddress,
                "server",
                serverInfo.NodeName,
                serverInfo.MachineName,
                serverInfo.OsDescription,
                serverInfo.OsArchitecture,
                serverInfo.ProcessArchitecture,
                serverInfo.ClientVersion,
                true,
                0,
                0,
                null,
                capturedAt)
        };
        var edges = new List<VirtualNetworkTopologyEdgeSnapshot>();

        lock (_peersLock)
        {
            foreach (var peer in _peersByAddress.Values)
            {
                peer.UpdateRates(capturedAt);
                nodes.Add(peer.ToTopologyNode());
                edges.Add(new VirtualNetworkTopologyEdgeSnapshot(
                    _options.NodeAddress,
                    peer.VirtualAddress,
                    VirtualNetworkTopologyConnectionTypes.Relay,
                    VirtualNetworkTransportProtocols.Udp,
                    null,
                    null,
                    null,
                    null,
                    peer.BytesSent,
                    peer.BytesReceived,
                    peer.BytesPerSecondSent,
                    peer.BytesPerSecondReceived,
                    peer.LastSeen));
            }

            var onlineAddresses = _peersByAddress.Values
                .Select(static peer => peer.VirtualAddress.ToString())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var observation in _topologyDirectLinks.Values
                .Where(item => capturedAt - item.LastObservedAt <= _options.PeerTimeout)
                .OrderByDescending(static item => item.LastObservedAt))
            {
                if (!onlineAddresses.Contains(observation.SourceAddress.ToString()) ||
                    !onlineAddresses.Contains(observation.DestinationAddress.ToString()))
                {
                    continue;
                }

                var traffic = GetTopologyTrafficLocked(observation.SourceAddress, observation.DestinationAddress, capturedAt);
                edges.Add(new VirtualNetworkTopologyEdgeSnapshot(
                    observation.SourceAddress,
                    observation.DestinationAddress,
                    VirtualNetworkTopologyConnectionTypes.Direct,
                    VirtualNetworkTransportProtocols.Udp,
                    observation.RttMilliseconds,
                    observation.JitterMilliseconds,
                    observation.LossPercent,
                    observation.CostMilliseconds,
                    traffic?.BytesSent ?? 0,
                    traffic?.BytesReceived ?? 0,
                    traffic?.BytesPerSecondSent ?? 0,
                    traffic?.BytesPerSecondReceived ?? 0,
                    observation.LastObservedAt));
            }
        }

        return new VirtualNetworkTopologySnapshot(
            VirtualNetworkTransportProtocols.Udp,
            capturedAt,
            nodes,
            edges);
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (Interlocked.Exchange(ref _started, 1) == 1)
        {
            return Task.CompletedTask;
        }

        try
        {
            _udpClient = new UdpClient(_options.BindEndPoint);
            _linuxUdpBatchSocket = LinuxUdpBatchSocket.TryCreate(
                _udpClient.Client,
                _options.MaxPacketSize,
                _logger);
            _runtimeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _receiveLoop = Task.Run(() => _linuxUdpBatchSocket is null
                ? ReceiveLoopAsync(_runtimeCancellation.Token)
                : ReceiveLoopBatchAsync(_linuxUdpBatchSocket, _runtimeCancellation.Token));
            _cleanupLoop = Task.Run(() => CleanupLoopAsync(_runtimeCancellation.Token));

            Log(ProxyNetworkerLogLevel.Information, $"Virtual Network relay listening on UDP {_options.BindEndPoint} as gateway {_options.NodeAddress}/{_options.PrefixLength}.");
            return Task.CompletedTask;
        }
        catch
        {
            Interlocked.Exchange(ref _started, 0);
            _udpClient?.Dispose();
            _linuxUdpBatchSocket?.Dispose();
            _linuxUdpBatchSocket = null;
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
            _runtimeCancellation?.Cancel();
            _udpClient?.Dispose();
            _linuxUdpBatchSocket?.Dispose();
            _linuxUdpBatchSocket = null;

            try
            {
                Task.WaitAll(new[] { _receiveLoop, _cleanupLoop }.Where(static item => item is not null).Cast<Task>().ToArray(), TimeSpan.FromSeconds(2));
            }
            catch (AggregateException ex) when (ex.InnerExceptions.All(static item => item is OperationCanceledException or ObjectDisposedException or SocketException))
            {
            }

            _runtimeCancellation?.Dispose();
        }

        _sendLock.Dispose();
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

                if (!VirtualNetworkFrame.TryParse(received.Buffer, _options.AuthenticationKey, _options.EncryptionEnabled, out var frame))
                {
                    Log(ProxyNetworkerLogLevel.Warning, $"Ignored malformed Virtual Network datagram from {received.RemoteEndPoint}.");
                    continue;
                }

                await HandleFrameAsync(frame, received.RemoteEndPoint, cancellationToken).ConfigureAwait(false);
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
                Log(ProxyNetworkerLogLevel.Error, "Virtual Network relay receive loop failed.", ex);
                return;
            }
        }
    }

    private async Task ReceiveLoopBatchAsync(LinuxUdpBatchSocket transport, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var count = transport.ReceiveBatch(cancellationToken);
                for (var index = 0; index < count; index++)
                {
                    var received = transport.GetReceived(index);
                    if (received.Length == 0 || received.Length > _options.MaxPacketSize)
                    {
                        continue;
                    }

                    if (!VirtualNetworkFrame.TryParse(
                        received.Memory,
                        _options.AuthenticationKey,
                        _options.EncryptionEnabled,
                        out var frame))
                    {
                        Log(ProxyNetworkerLogLevel.Warning, $"Ignored malformed Virtual Network datagram from {received.RemoteEndPoint}.");
                        continue;
                    }

                    await HandleFrameAsync(frame, received.RemoteEndPoint, cancellationToken).ConfigureAwait(false);
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
            catch (SocketException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (IOException ex) when (cancellationToken.IsCancellationRequested && ex.Message.Contains("errno=9", StringComparison.Ordinal))
            {
                return;
            }
            catch (Exception ex)
            {
                Log(ProxyNetworkerLogLevel.Error, "Virtual Network batched relay receive loop failed.", ex);
                return;
            }
        }
    }

    private async Task CleanupLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_options.HeartbeatInterval, cancellationToken).ConfigureAwait(false);
                CleanupExpiredPeers();
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
                Log(ProxyNetworkerLogLevel.Warning, "Virtual Network relay cleanup failed.", ex);
            }
        }
    }

    private async Task HandleFrameAsync(VirtualNetworkFrame frame, IPEndPoint remoteEndPoint, CancellationToken cancellationToken)
    {
        switch (frame.Type)
        {
            case VirtualNetworkFrameType.Hello:
            case VirtualNetworkFrameType.Heartbeat:
                var heartbeatPeer = RegisterPeer(frame.VirtualAddress, remoteEndPoint, frame.Payload.Span);
                if (heartbeatPeer is not null)
                {
                    await BroadcastPeerCandidatesAsync(heartbeatPeer, cancellationToken).ConfigureAwait(false);
                }
                break;

            case VirtualNetworkFrameType.PeerCandidates:
                var candidatePeer = RegisterPeer(frame.VirtualAddress, remoteEndPoint);
                if (candidatePeer is null)
                {
                    return;
                }

                if (!VirtualNetworkCandidateCodec.TryDecode(frame.Payload.Span, out var candidates))
                {
                    Log(ProxyNetworkerLogLevel.Warning, $"Ignored malformed direct-path candidates from {frame.VirtualAddress}.");
                    return;
                }

                candidatePeer.LocalEndPoints.Clear();
                candidatePeer.LocalEndPoints.AddRange(candidates);
                await BroadcastPeerCandidatesAsync(candidatePeer, cancellationToken).ConfigureAwait(false);
                break;

            case VirtualNetworkFrameType.RelayRequired:
                var relayPeer = RegisterPeer(frame.VirtualAddress, remoteEndPoint);
                if (relayPeer is null)
                {
                    return;
                }

                relayPeer.RelayRequired = frame.Payload.Length > 0 && frame.Payload.Span[0] != 0;
                await BroadcastPeerCandidatesAsync(relayPeer, cancellationToken).ConfigureAwait(false);
                break;

            case VirtualNetworkFrameType.MeshLinkState:
                if (RegisterPeer(frame.VirtualAddress, remoteEndPoint) is not null &&
                    VirtualNetworkMeshAdvertisement.TryDecode(frame.Payload.Span, out var advertisement))
                {
                    lock (_peersLock)
                    {
                        var key = AddressKey(frame.VirtualAddress);
                        if (!_meshAdvertisements.TryGetValue(key, out var existing) ||
                            existing.Advertisement.Sequence < advertisement.Sequence)
                        {
                            var observedAt = DateTimeOffset.UtcNow;
                            _meshAdvertisements[key] = new MeshAdvertisementObservation(
                                frame.VirtualAddress,
                                advertisement,
                                observedAt);
                            foreach (var link in advertisement.Links)
                            {
                                UpdateTopologyDirectLinkLocked(frame.VirtualAddress, link, observedAt);
                            }
                        }
                    }
                    await BroadcastMeshFrameAsync(frame, remoteEndPoint, cancellationToken).ConfigureAwait(false);
                }
                break;

            case VirtualNetworkFrameType.MeshTrafficReport:
                if (RegisterPeer(frame.VirtualAddress, remoteEndPoint) is not null &&
                    VirtualNetworkMeshTrafficReport.TryDecode(frame.Payload.Span, out var trafficReport))
                {
                    lock (_peersLock)
                    {
                        var key = AddressKey(frame.VirtualAddress);
                        if (!_meshTrafficReports.TryGetValue(key, out var existing) ||
                            existing.Telemetry.Sequence < trafficReport.Sequence)
                        {
                            var observedAt = DateTimeOffset.UtcNow;
                            var elapsedSeconds = existing is null
                                ? 0
                                : Math.Max(0, (observedAt - existing.ObservedAt).TotalSeconds);
                            var rates = new Dictionary<string, MeshTrafficRate>(StringComparer.OrdinalIgnoreCase);
                            foreach (var link in trafficReport.Links)
                            {
                                var previous = existing?.Telemetry.Links.FirstOrDefault(
                                    item => item.VirtualAddress.Equals(link.VirtualAddress));
                                var sentDelta = previous is null ? 0 : Math.Max(0, link.BytesSent - previous.BytesSent);
                                var receivedDelta = previous is null ? 0 : Math.Max(0, link.BytesReceived - previous.BytesReceived);
                                rates[AddressKey(link.VirtualAddress)] = elapsedSeconds > 0
                                    ? new MeshTrafficRate(sentDelta / elapsedSeconds, receivedDelta / elapsedSeconds)
                                    : new MeshTrafficRate(0, 0);
                            }

                            _meshTrafficReports[key] = new MeshTrafficObservation(
                                frame.VirtualAddress,
                                trafficReport,
                                observedAt,
                                rates);
                            foreach (var link in trafficReport.Links)
                            {
                                var rate = rates.GetValueOrDefault(AddressKey(link.VirtualAddress));
                                if (rate is not null && rate.BytesSent + rate.BytesReceived > 0)
                                {
                                    UpdateTopologyDirectTrafficLocked(frame.VirtualAddress, link.VirtualAddress, observedAt);
                                }
                            }
                        }
                    }
                }
                break;

            case VirtualNetworkFrameType.PathProbe:
            case VirtualNetworkFrameType.PathProbeAck:
                if (!VirtualNetworkPathProbe.TryParse(frame.Payload.Span, out var probe))
                {
                    return;
                }

                var probePeer = FindPeer(probe.DestinationAddress);
                if (probePeer is null || IsSamePeer(probePeer.EndPoint, remoteEndPoint))
                {
                    return;
                }

                var routedProbe = VirtualNetworkFrame.Create(
                    frame.Type,
                    frame.VirtualAddress,
                    frame.Payload.Span,
                    _options.AuthenticationKey, _options.EncryptionEnabled);
                await SendFrameAsync(routedProbe, probePeer.EndPoint, cancellationToken).ConfigureAwait(false);
                break;

            case VirtualNetworkFrameType.Packet:
                var sourceAddress = TryReadIPv4Source(frame.Payload.Span, out var parsedSource)
                    ? parsedSource
                    : frame.VirtualAddress;
                RegisterPeer(sourceAddress, remoteEndPoint)?.RecordReceived(frame.Payload.Length);
                await RoutePacketAsync(frame.Payload, remoteEndPoint, cancellationToken).ConfigureAwait(false);
                break;

            case VirtualNetworkFrameType.MeshPacket:
                if (!VirtualNetworkMeshPacket.TryParse(frame.Payload, out var meshPacket))
                {
                    return;
                }

                RegisterPeer(frame.VirtualAddress, remoteEndPoint)?.RecordReceived(frame.Payload.Length);

                lock (_peersLock)
                {
                    if (_bannedAddresses.Contains(AddressKey(frame.VirtualAddress)))
                    {
                        return;
                    }
                }

                if (meshPacket.Destination.Equals(_options.NodeAddress))
                {
                    return;
                }

                var meshTarget = FindPeer(meshPacket.Destination);
                if (meshTarget is null || IsSamePeer(meshTarget.EndPoint, remoteEndPoint))
                {
                    return;
                }

                var forwardedMeshFrame = VirtualNetworkFrame.Create(
                    VirtualNetworkFrameType.MeshPacket,
                    frame.VirtualAddress,
                    frame.Payload.Span,
                    _options.AuthenticationKey, _options.EncryptionEnabled);
                await SendFrameAsync(forwardedMeshFrame, meshTarget.EndPoint, cancellationToken).ConfigureAwait(false);
                meshTarget.RecordSent(frame.Payload.Length);
                break;
        }
    }

    private async Task RoutePacketAsync(ReadOnlyMemory<byte> packet, IPEndPoint sourcePeer, CancellationToken cancellationToken)
    {
        if (!TryReadIPv4Destination(packet.Span, out var destination))
        {
            return;
        }

        if (IsBroadcastOrMulticast(destination))
        {
            await SendPacketToPeersAsync(packet, exceptPeer: sourcePeer, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (destination.Equals(_options.NodeAddress))
        {
            return;
        }

        var peer = FindPeer(destination);
        if (peer is not null && !IsSamePeer(peer.EndPoint, sourcePeer))
        {
            await SendPacketFrameAsync(packet, peer.EndPoint, cancellationToken).ConfigureAwait(false);
            peer.RecordSent(packet.Length);
        }
    }

    private async Task SendPacketToPeersAsync(ReadOnlyMemory<byte> packet, IPEndPoint exceptPeer, CancellationToken cancellationToken)
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
            peer.RecordSent(packet.Length);
        }
    }

    private async Task SendPacketFrameAsync(ReadOnlyMemory<byte> packet, IPEndPoint remoteEndPoint, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var frameLength = VirtualNetworkFrame.GetFrameLength(packet.Length);
        var frame = ArrayPool<byte>.Shared.Rent(frameLength);
        try
        {
            VirtualNetworkFrame.CreateInto(
                VirtualNetworkFrameType.Packet,
                _options.NodeAddress,
                packet.Span,
                _options.AuthenticationKey,
                _options.EncryptionEnabled,
                frame.AsSpan(0, frameLength));
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(frame);
            throw;
        }

        await SendOwnedFrameAsync(frame, frameLength, remoteEndPoint, cancellationToken).ConfigureAwait(false);
    }

    private async Task SendOwnedFrameAsync(byte[] frame, int frameLength, IPEndPoint remoteEndPoint, CancellationToken cancellationToken)
    {
        var batchSocket = _linuxUdpBatchSocket;
        if (batchSocket is not null)
        {
            if (!batchSocket.EnqueueOwned(frame, frameLength, remoteEndPoint))
            {
                ArrayPool<byte>.Shared.Return(frame);
            }

            return;
        }

        try
        {
            var udpClient = _udpClient;
            if (udpClient is null)
            {
                return;
            }

            await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await udpClient.SendAsync(frame, frameLength, remoteEndPoint).ConfigureAwait(false);
            }
            finally
            {
                _sendLock.Release();
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(frame);
        }
    }

    private async Task SendFrameAsync(byte[] frame, IPEndPoint remoteEndPoint, CancellationToken cancellationToken)
    {
        var batchSocket = _linuxUdpBatchSocket;
        if (batchSocket is not null)
        {
            batchSocket.Enqueue(frame, remoteEndPoint);
            return;
        }

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

    private async Task BroadcastPeerCandidatesAsync(VirtualNetworkPeer peer, CancellationToken cancellationToken)
    {
        VirtualNetworkPeer[] peers;
        lock (_peersLock)
        {
            peers = _peersByAddress.Values.ToArray();
        }

        var endpoints = peer.RelayRequired
            ? Array.Empty<IPEndPoint>()
            : new[] { peer.EndPoint }.Concat(peer.LocalEndPoints).ToArray();
        var payload = VirtualNetworkCandidateCodec.Encode(endpoints);
        foreach (var target in peers)
        {
            if (IsSamePeer(target.EndPoint, peer.EndPoint))
            {
                continue;
            }

            var frame = VirtualNetworkFrame.Create(
                VirtualNetworkFrameType.PeerCandidates,
                peer.VirtualAddress,
                payload,
                _options.AuthenticationKey, _options.EncryptionEnabled);
            await SendFrameAsync(frame, target.EndPoint, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task BroadcastMeshFrameAsync(
        VirtualNetworkFrame frame,
        IPEndPoint sourceEndPoint,
        CancellationToken cancellationToken)
    {
        VirtualNetworkPeer[] peers;
        lock (_peersLock)
        {
            peers = _peersByAddress.Values.ToArray();
        }

        foreach (var target in peers)
        {
            if (IsSamePeer(target.EndPoint, sourceEndPoint))
            {
                continue;
            }

            var forwardedFrame = VirtualNetworkFrame.Create(
                frame.Type,
                frame.VirtualAddress,
                frame.Payload.Span,
                _options.AuthenticationKey, _options.EncryptionEnabled);
            await SendFrameAsync(forwardedFrame, target.EndPoint, cancellationToken).ConfigureAwait(false);
        }
    }

    private VirtualNetworkPeer? RegisterPeer(
        IPAddress virtualAddress,
        IPEndPoint remoteEndPoint,
        ReadOnlySpan<byte> nodeInfoPayload = default)
    {
        if (virtualAddress.Equals(_options.NodeAddress))
        {
            return null;
        }

        var addressKey = AddressKey(virtualAddress);
        var endpointKey = EndPointKey(remoteEndPoint);

        lock (_peersLock)
        {
            if (_bannedAddresses.Contains(addressKey))
            {
                return null;
            }

            if (_addressByEndpoint.TryGetValue(endpointKey, out var oldAddressKey) && oldAddressKey != addressKey)
            {
                _peersByAddress.Remove(oldAddressKey);
                _meshAdvertisements.Remove(oldAddressKey);
                _meshTrafficReports.Remove(oldAddressKey);
                RemoveTopologyDirectLinksLocked(oldAddressKey);
            }

            _addressByEndpoint[endpointKey] = addressKey;

            if (_peersByAddress.TryGetValue(addressKey, out var peer))
            {
                peer.EndPoint = remoteEndPoint;
                peer.LastSeen = DateTimeOffset.UtcNow;
                peer.ApplyNodeInfo(nodeInfoPayload);
                return peer;
            }

            peer = new VirtualNetworkPeer(virtualAddress, remoteEndPoint);
            peer.ApplyNodeInfo(nodeInfoPayload);
            _peersByAddress[addressKey] = peer;
            Log(ProxyNetworkerLogLevel.Information, $"Virtual Network peer {virtualAddress} registered from {remoteEndPoint}.");
            return peer;
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
                _meshAdvertisements.Remove(key);
                _meshTrafficReports.Remove(key);
                RemoveTopologyDirectLinksLocked(key);
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

    private static bool IsSamePeer(IPEndPoint left, IPEndPoint right)
    {
        return EndPointEquals(left, right);
    }

    private static bool EndPointEquals(IPEndPoint left, IPEndPoint right)
    {
        return left.Port == right.Port && left.Address.Equals(right.Address);
    }

    private void UpdateTopologyDirectLinkLocked(
        IPAddress sourceAddress,
        VirtualNetworkMeshLink link,
        DateTimeOffset observedAt)
    {
        if (sourceAddress.Equals(link.VirtualAddress))
        {
            return;
        }

        var key = TopologyDirectLinkKey(sourceAddress, link.VirtualAddress);
        var lastTrafficAt = _topologyDirectLinks.TryGetValue(key, out var existing)
            ? existing.LastTrafficAt
            : default;
        _topologyDirectLinks[key] = new TopologyDirectLinkObservation(
            sourceAddress,
            link.VirtualAddress,
            link.RttMilliseconds,
            link.JitterMilliseconds,
            link.LossBasisPoints / 100d,
            link.CostMilliseconds,
            observedAt,
            lastTrafficAt);
    }

    private void UpdateTopologyDirectTrafficLocked(
        IPAddress sourceAddress,
        IPAddress destinationAddress,
        DateTimeOffset observedAt)
    {
        if (sourceAddress.Equals(destinationAddress))
        {
            return;
        }

        var key = TopologyDirectLinkKey(sourceAddress, destinationAddress);
        if (_topologyDirectLinks.TryGetValue(key, out var existing))
        {
            _topologyDirectLinks[key] = existing with { LastTrafficAt = observedAt };
            return;
        }

        _topologyDirectLinks[key] = new TopologyDirectLinkObservation(
            sourceAddress,
            destinationAddress,
            null,
            null,
            null,
            null,
            default,
            observedAt);
    }

    private TopologyTrafficObservation? GetTopologyTrafficLocked(
        IPAddress sourceAddress,
        IPAddress destinationAddress,
        DateTimeOffset capturedAt)
    {
        if (_meshTrafficReports.TryGetValue(AddressKey(sourceAddress), out var sourceObservation) &&
            capturedAt - sourceObservation.ObservedAt <= _options.PeerTimeout)
        {
            var sourceTraffic = sourceObservation.Telemetry.Links.FirstOrDefault(
                item => item.VirtualAddress.Equals(destinationAddress));
            if (sourceTraffic is not null)
            {
                var rate = sourceObservation.Rates.GetValueOrDefault(AddressKey(destinationAddress));
                return new TopologyTrafficObservation(
                    sourceTraffic.BytesSent,
                    sourceTraffic.BytesReceived,
                    rate?.BytesSent ?? 0,
                    rate?.BytesReceived ?? 0);
            }
        }

        if (_meshTrafficReports.TryGetValue(AddressKey(destinationAddress), out var destinationObservation) &&
            capturedAt - destinationObservation.ObservedAt <= _options.PeerTimeout)
        {
            var destinationTraffic = destinationObservation.Telemetry.Links.FirstOrDefault(
                item => item.VirtualAddress.Equals(sourceAddress));
            if (destinationTraffic is not null)
            {
                var rate = destinationObservation.Rates.GetValueOrDefault(AddressKey(sourceAddress));
                return new TopologyTrafficObservation(
                    destinationTraffic.BytesReceived,
                    destinationTraffic.BytesSent,
                    rate?.BytesReceived ?? 0,
                    rate?.BytesSent ?? 0);
            }
        }

        return null;
    }

    private void RemoveTopologyDirectLinksLocked(string addressKey)
    {
        foreach (var key in _topologyDirectLinks
            .Where(item => AddressKey(item.Value.SourceAddress).Equals(addressKey, StringComparison.OrdinalIgnoreCase) ||
                AddressKey(item.Value.DestinationAddress).Equals(addressKey, StringComparison.OrdinalIgnoreCase))
            .Select(static item => item.Key)
            .ToArray())
        {
            _topologyDirectLinks.Remove(key);
        }
    }

    private static string TopologyDirectLinkKey(IPAddress first, IPAddress second)
    {
        var firstKey = AddressKey(first);
        var secondKey = AddressKey(second);
        return string.Compare(firstKey, secondKey, StringComparison.OrdinalIgnoreCase) <= 0
            ? $"{firstKey}|{secondKey}"
            : $"{secondKey}|{firstKey}";
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
        private readonly object _rateLock = new();
        private long _bytesSent;
        private long _bytesReceived;
        private long _rateSampleBytesSent;
        private long _rateSampleBytesReceived;
        private DateTimeOffset _rateSampleAt = DateTimeOffset.UtcNow;
        private double _bytesPerSecondSent;
        private double _bytesPerSecondReceived;

        public VirtualNetworkPeer(IPAddress virtualAddress, IPEndPoint endPoint)
        {
            VirtualAddress = virtualAddress;
            EndPoint = endPoint;
            LastSeen = DateTimeOffset.UtcNow;
        }

        public IPAddress VirtualAddress { get; }

        public IPEndPoint EndPoint { get; set; }

        public List<IPEndPoint> LocalEndPoints { get; } = new();

        public bool RelayRequired { get; set; }

        public DateTimeOffset LastSeen { get; set; }

        public DateTimeOffset ConnectedAt { get; } = DateTimeOffset.UtcNow;

        public string NodeName { get; set; } = string.Empty;

        public string MachineName { get; set; } = string.Empty;

        public string OsDescription { get; set; } = string.Empty;

        public string OsArchitecture { get; set; } = string.Empty;

        public string ProcessArchitecture { get; set; } = string.Empty;

        public string ClientVersion { get; set; } = string.Empty;

        public long BytesSent => Interlocked.Read(ref _bytesSent);

        public long BytesReceived => Interlocked.Read(ref _bytesReceived);

        public double BytesPerSecondSent { get { lock (_rateLock) { return _bytesPerSecondSent; } } }

        public double BytesPerSecondReceived { get { lock (_rateLock) { return _bytesPerSecondReceived; } } }

        public void UpdateRates(DateTimeOffset now)
        {
            lock (_rateLock)
            {
                var elapsedSeconds = (now - _rateSampleAt).TotalSeconds;
                if (elapsedSeconds < 0.25)
                {
                    return;
                }

                var bytesSent = Interlocked.Read(ref _bytesSent);
                var bytesReceived = Interlocked.Read(ref _bytesReceived);
                _bytesPerSecondSent = Math.Max(0, bytesSent - _rateSampleBytesSent) / elapsedSeconds;
                _bytesPerSecondReceived = Math.Max(0, bytesReceived - _rateSampleBytesReceived) / elapsedSeconds;
                _rateSampleBytesSent = bytesSent;
                _rateSampleBytesReceived = bytesReceived;
                _rateSampleAt = now;
            }
        }

        public void RecordSent(int bytes)
        {
            if (bytes > 0)
            {
                Interlocked.Add(ref _bytesSent, bytes);
            }
        }

        public void RecordReceived(int bytes)
        {
            if (bytes > 0)
            {
                Interlocked.Add(ref _bytesReceived, bytes);
            }
        }

        public void ApplyNodeInfo(ReadOnlySpan<byte> payload)
        {
            if (!VirtualNetworkNodeInfo.TryParse(payload, out var nodeInfo))
            {
                return;
            }

            NodeName = nodeInfo.NodeName;
            MachineName = nodeInfo.MachineName;
            OsDescription = nodeInfo.OsDescription;
            OsArchitecture = nodeInfo.OsArchitecture;
            ProcessArchitecture = nodeInfo.ProcessArchitecture;
            ClientVersion = nodeInfo.ClientVersion;
        }

        public VirtualNetworkPeerSnapshot ToSnapshot()
        {
            return new VirtualNetworkPeerSnapshot(
                VirtualAddress,
                NodeName,
                MachineName,
                OsDescription,
                OsArchitecture,
                ProcessArchitecture,
                ClientVersion,
                EndPoint,
                LocalEndPoints.ToArray(),
                BytesSent,
                BytesReceived,
                ConnectedAt,
                LastSeen);
        }

        public VirtualNetworkTopologyNodeSnapshot ToTopologyNode()
        {
            return new VirtualNetworkTopologyNodeSnapshot(
                VirtualAddress,
                "client",
                NodeName,
                MachineName,
                OsDescription,
                OsArchitecture,
                ProcessArchitecture,
                ClientVersion,
                true,
                BytesSent,
                BytesReceived,
                ConnectedAt,
                LastSeen);
        }
    }

    private sealed record MeshAdvertisementObservation(
        IPAddress Origin,
        VirtualNetworkMeshAdvertisement Advertisement,
        DateTimeOffset ObservedAt);

    private sealed record MeshTrafficObservation(
        IPAddress Origin,
        VirtualNetworkMeshTrafficReport Telemetry,
        DateTimeOffset ObservedAt,
        IReadOnlyDictionary<string, MeshTrafficRate> Rates);

    private sealed record MeshTrafficRate(double BytesSent, double BytesReceived);

    private sealed record TopologyTrafficObservation(
        long BytesSent,
        long BytesReceived,
        double BytesPerSecondSent,
        double BytesPerSecondReceived);

    private sealed record TopologyDirectLinkObservation(
        IPAddress SourceAddress,
        IPAddress DestinationAddress,
        int? RttMilliseconds,
        int? JitterMilliseconds,
        double? LossPercent,
        int? CostMilliseconds,
        DateTimeOffset LastLinkStateAt,
        DateTimeOffset LastTrafficAt)
    {
        public DateTimeOffset LastObservedAt => LastTrafficAt > LastLinkStateAt ? LastTrafficAt : LastLinkStateAt;
    }
}
