using System.Buffers;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using ProxyNetworker.Core.Platforms.Linux;

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
    private readonly Dictionary<string, DateTimeOffset> _directPathCooldownByAddress = new(StringComparer.OrdinalIgnoreCase);
    private VirtualNetworkMeshRouteTable _meshRoutes = VirtualNetworkMeshRouteTable.Empty;
    private long _meshAdvertisementSequence;
    private long _meshTrafficReportSequence;
    private DateTimeOffset _globalDirectPathCooldownUntil;
    private UdpClient? _udpClient;
    private LinuxUdpBatchSocket? _linuxUdpBatchSocket;
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
            _linuxUdpBatchSocket = LinuxUdpBatchSocket.TryCreate(
                _udpClient.Client,
                _options.MaxPacketSize,
                _logger);
            _runtimeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _tunDevice.PacketReceived += OnTunPacketReceived;

            await _tunDevice.StartAsync(cancellationToken).ConfigureAwait(false);

            _receiveLoop = Task.Run(() => _linuxUdpBatchSocket is null
                ? ReceiveLoopAsync(_runtimeCancellation.Token)
                : ReceiveLoopBatchAsync(_linuxUdpBatchSocket, _runtimeCancellation.Token));
            _heartbeatLoop = Task.Run(() => HeartbeatLoopAsync(_runtimeCancellation.Token));

            if (!IsServer)
            {
                await SendControlFrameAsync(VirtualNetworkFrameType.Hello, _options.RemoteEndPoint!, cancellationToken).ConfigureAwait(false);
                await SendRelayRequiredAsync(!_options.EnableDirectPaths, cancellationToken).ConfigureAwait(false);
                if (_options.EnableDirectPaths)
                {
                    await SendLocalCandidatesAsync(cancellationToken).ConfigureAwait(false);
                    await SendMeshLinkStateAsync(cancellationToken).ConfigureAwait(false);
                    await SendMeshTrafficReportAsync(cancellationToken).ConfigureAwait(false);
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
            _tunDevice.PacketReceived -= OnTunPacketReceived;
            _runtimeCancellation?.Cancel();
            _udpClient?.Dispose();
            _linuxUdpBatchSocket?.Dispose();
            _linuxUdpBatchSocket = null;

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
        var datagram = GC.AllocateUninitializedArray<byte>(_options.MaxPacketSize);
        var anyEndPoint = new IPEndPoint(IPAddress.Any, 0);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var udpClient = _udpClient;
                if (udpClient is null)
                {
                    return;
                }

                var received = await udpClient.Client.ReceiveFromAsync(
                    datagram.AsMemory(),
                    SocketFlags.None,
                    anyEndPoint,
                    cancellationToken).ConfigureAwait(false);
                if (received.ReceivedBytes == 0 || received.ReceivedBytes > _options.MaxPacketSize)
                {
                    continue;
                }

                var remoteEndPoint = (IPEndPoint)received.RemoteEndPoint;
                if (!VirtualNetworkFrame.TryParse(datagram, received.ReceivedBytes, _options.AuthenticationKey, _options.EncryptionEnabled, out var frame))
                {
                    Log(ProxyNetworkerLogLevel.Warning, $"Ignored malformed Virtual Network datagram from {remoteEndPoint}.");
                    continue;
                }

                CleanupExpiredPeers();

                if (IsServer)
                {
                    await HandleServerFrameAsync(frame, remoteEndPoint, cancellationToken).ConfigureAwait(false);
                }
                else if (IsConfiguredServer(remoteEndPoint))
                {
                    await HandleClientFrameAsync(frame, remoteEndPoint, cancellationToken).ConfigureAwait(false);
                }
                else if (!_options.EnableDirectPaths)
                {
                    Log(ProxyNetworkerLogLevel.Debug, "Ignored direct Virtual Network datagram because relay-only mode is enabled.");
                }
                else
                {
                    await HandleDirectFrameAsync(frame, remoteEndPoint, cancellationToken).ConfigureAwait(false);
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
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionRefused)
            {
                // Windows reports ICMP port-unreachable from a failed direct-path probe
                // as WSAECONNRESET on the shared UDP socket. It is a per-path signal,
                // not a reason to stop receiving relay and direct traffic entirely.
                SuspendDirectPaths(null, $"UDP socket error {ex.SocketErrorCode}");
                continue;
            }
            catch (Exception ex)
            {
                Log(ProxyNetworkerLogLevel.Error, "Virtual Network receive loop failed.", ex);
                return;
            }
        }
    }

    private async Task ReceiveLoopBatchAsync(LinuxUdpBatchSocket transport, CancellationToken cancellationToken)
    {
        var tunBatch = new TunPacketBatch();
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var count = transport.ReceiveBatch(cancellationToken);
                if (count == 0)
                {
                    continue;
                }

                for (var index = 0; index < count; index++)
                {
                    var received = transport.GetReceived(index);
                    if (received.Length == 0 || received.Length > _options.MaxPacketSize)
                    {
                        continue;
                    }

                    if (!VirtualNetworkFrame.TryParse(
                        received.Buffer,
                        received.Length,
                        _options.AuthenticationKey,
                        _options.EncryptionEnabled,
                        out var frame))
                    {
                        Log(ProxyNetworkerLogLevel.Warning, $"Ignored malformed Virtual Network datagram from {received.RemoteEndPoint}.");
                        continue;
                    }

                    CleanupExpiredPeers();
                    if (IsServer)
                    {
                        await HandleServerFrameAsync(frame, received.RemoteEndPoint, cancellationToken, tunBatch).ConfigureAwait(false);
                    }
                    else if (IsConfiguredServer(received.RemoteEndPoint))
                    {
                        await HandleClientFrameAsync(frame, received.RemoteEndPoint, cancellationToken, tunBatch).ConfigureAwait(false);
                    }
                    else if (!_options.EnableDirectPaths)
                    {
                        Log(ProxyNetworkerLogLevel.Debug, "Ignored direct Virtual Network datagram because relay-only mode is enabled.");
                    }
                    else
                    {
                        await HandleDirectFrameAsync(frame, received.RemoteEndPoint, cancellationToken, tunBatch).ConfigureAwait(false);
                    }
                }

                await tunBatch.FlushAsync(_tunDevice, cancellationToken).ConfigureAwait(false);
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
                Log(ProxyNetworkerLogLevel.Error, "Virtual Network batched receive loop failed.", ex);
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
                        await SendMeshTrafficReportAsync(cancellationToken).ConfigureAwait(false);
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

    private async Task HandleServerFrameAsync(
        VirtualNetworkFrame frame,
        IPEndPoint remoteEndPoint,
        CancellationToken cancellationToken,
        TunPacketBatch? tunBatch = null)
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
                RoutePacket(frame.Payload, remoteEndPoint, cancellationToken, tunBatch);
                break;
        }
    }

    private async Task HandleClientFrameAsync(
        VirtualNetworkFrame frame,
        IPEndPoint remoteEndPoint,
        CancellationToken cancellationToken,
        TunPacketBatch? tunBatch = null)
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
            await HandleMeshPacketAsync(frame, cancellationToken, tunBatch).ConfigureAwait(false);
        }
        else if (frame.Type == VirtualNetworkFrameType.Packet)
        {
            NotePathActivity(frame.VirtualAddress, remoteEndPoint);
            await SendTunPacketAsync(frame.Payload, tunBatch, cancellationToken).ConfigureAwait(false);
        }
        else if (frame.Type == VirtualNetworkFrameType.PathProbe)
        {
            if (VirtualNetworkPathProbe.TryParse(frame.Payload.Span, out var probe) && probe.DestinationAddress.Equals(_options.NodeAddress))
            {
                var response = VirtualNetworkFrame.Create(
                    VirtualNetworkFrameType.PathProbeAck,
                    _options.NodeAddress,
                    frame.Payload.Span,
                    _options.AuthenticationKey, _options.EncryptionEnabled);
                await SendFrameAsync(response, remoteEndPoint, cancellationToken).ConfigureAwait(false);
            }
        }
        else if (frame.Type == VirtualNetworkFrameType.PathProbeAck &&
            VirtualNetworkPathProbe.TryParse(frame.Payload.Span, out var probeAck))
        {
            RecordPathMeasurement(frame.VirtualAddress, remoteEndPoint, probeAck);
        }
    }

    private async Task HandleDirectFrameAsync(
        VirtualNetworkFrame frame,
        IPEndPoint remoteEndPoint,
        CancellationToken cancellationToken,
        TunPacketBatch? tunBatch = null)
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
                        _options.AuthenticationKey, _options.EncryptionEnabled);
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
                if (VirtualNetworkMeshPacket.TryParse(frame.Payload.Span, out var directMeshPacket))
                {
                    var linkAddress = FindDirectPeerAddressLocked(remoteEndPoint) ?? frame.VirtualAddress;
                    RecordDirectTraffic(linkAddress, remoteEndPoint, 0, frame.Payload.Length);
                }
                await HandleMeshPacketAsync(frame, cancellationToken, tunBatch).ConfigureAwait(false);
                break;

            case VirtualNetworkFrameType.Packet:
                NotePathActivity(frame.VirtualAddress, remoteEndPoint);
                RecordDirectTraffic(frame.VirtualAddress, remoteEndPoint, 0, frame.Payload.Length);
                await SendTunPacketAsync(frame.Payload, tunBatch, cancellationToken).ConfigureAwait(false);
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
            _options.AuthenticationKey, _options.EncryptionEnabled);
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
            _options.AuthenticationKey, _options.EncryptionEnabled);
        await SendFrameAsync(frame, remoteEndPoint, cancellationToken).ConfigureAwait(false);
    }

    private async Task SendMeshTrafficReportAsync(CancellationToken cancellationToken)
    {
        var remoteEndPoint = _options.RemoteEndPoint;
        if (remoteEndPoint is null || !_options.EnableDirectPaths)
        {
            return;
        }

        VirtualNetworkMeshTraffic[] links;
        lock (_peersLock)
        {
            links = _directPeersByAddress.Values
                .Select(static state =>
                {
                    var paths = state.Paths.Values.Where(static path => !path.IsRelay).ToArray();
                    return new VirtualNetworkMeshTraffic(
                        state.VirtualAddress,
                        paths.Aggregate(0L, static (total, path) => total + path.BytesSent),
                        paths.Aggregate(0L, static (total, path) => total + path.BytesReceived));
                })
                .ToArray();
        }

        var sequence = Interlocked.Increment(ref _meshTrafficReportSequence);
        var report = new VirtualNetworkMeshTrafficReport(sequence, links);
        var frame = VirtualNetworkFrame.Create(
            VirtualNetworkFrameType.MeshTrafficReport,
            _options.NodeAddress,
            report.Encode(),
            _options.AuthenticationKey, _options.EncryptionEnabled);
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

    private async Task HandleMeshPacketAsync(
        VirtualNetworkFrame frame,
        CancellationToken cancellationToken,
        TunPacketBatch? tunBatch = null)
    {
        if (!VirtualNetworkMeshPacket.TryParse(frame.Payload.Span, out var meshPacket))
        {
            Log(ProxyNetworkerLogLevel.Warning, $"Ignored malformed mesh packet from {frame.VirtualAddress}.");
            return;
        }

        if (meshPacket.Destination.Equals(_options.NodeAddress))
        {
            await SendTunPacketAsync(meshPacket.Payload, tunBatch, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!meshPacket.TryCreateForwarded(out var forwardedPayload))
        {
            Log(ProxyNetworkerLogLevel.Debug, $"Dropped mesh packet from {frame.VirtualAddress} to {meshPacket.Destination} after reaching the hop limit.");
            return;
        }

        SendMeshPacket(
            frame.VirtualAddress,
            meshPacket.Destination,
            forwardedPayload,
            cancellationToken);
    }

    private void SendRoutedPacket(
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
                SendPacketFrame(packet, relay, cancellationToken);
            }
            return;
        }

        if (decision.IsRelay)
        {
            SendPacketFrame(packet, decision.EndPoint, cancellationToken);
            return;
        }

        var meshPayload = VirtualNetworkMeshPacket.Create(destination, packet.Span);
        SendMeshPacketFrame(
            _options.NodeAddress,
            meshPayload,
            decision.EndPoint,
            cancellationToken);
        RecordDirectTraffic(decision.NextHopAddress, decision.EndPoint, meshPayload.Length, 0);
    }

    private void SendMeshPacket(
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

        SendMeshPacketFrame(
            sourceAddress,
            meshPayload,
            decision.EndPoint,
            cancellationToken);
        if (!decision.IsRelay)
        {
            RecordDirectTraffic(decision.NextHopAddress, decision.EndPoint, meshPayload.Length, 0);
        }
    }

    private void SendMeshPacketFrame(
        IPAddress sourceAddress,
        byte[] meshPayload,
        IPEndPoint nextHopEndPoint,
        CancellationToken cancellationToken)
    {
        var frameLength = VirtualNetworkFrame.GetFrameLength(meshPayload.Length);
        var frame = ArrayPool<byte>.Shared.Rent(frameLength);
        try
        {
            VirtualNetworkFrame.CreateInto(
                VirtualNetworkFrameType.MeshPacket,
                sourceAddress,
                meshPayload,
                _options.AuthenticationKey,
                _options.EncryptionEnabled,
                frame.AsSpan(0, frameLength));
            SendFrame(frame, frameLength, nextHopEndPoint, cancellationToken);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(frame);
        }
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
                        : new ForwardingDecision(_options.RemoteEndPoint!, destination, true, relayPath?.ScoreMilliseconds, "relay");
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

                return new ForwardingDecision(directPath.EndPoint, nextHopAddress, false, meshCost, path);
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

                return new ForwardingDecision(relay, destination, true, relayScore, "relay");
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
        if (now < _globalDirectPathCooldownUntil)
        {
            return [];
        }

        var links = new List<VirtualNetworkMeshLink>();
        foreach (var state in _directPeersByAddress.Values)
        {
            if (IsDirectPathSuspendedLocked(state.VirtualAddress, now))
            {
                continue;
            }

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
        if (IsDirectPathSuspendedLocked(virtualAddress, now))
        {
            return null;
        }

        return _directPeersByAddress.TryGetValue(AddressKey(virtualAddress), out var state)
            ? state.Paths.Values
                .Where(path => !path.IsRelay && path.EwmaRttMs is not null && now - path.LastSuccess <= _options.PeerTimeout)
                .OrderBy(static path => path.ScoreMilliseconds)
                .FirstOrDefault()
            : null;
    }

    private void SuspendDirectPaths(IPAddress? virtualAddress, string reason)
    {
        var now = DateTimeOffset.UtcNow;
        var until = now + _options.DirectPathFailureCooldown;
        var changed = false;

        lock (_peersLock)
        {
            if (virtualAddress is null)
            {
                if (until <= _globalDirectPathCooldownUntil)
                {
                    return;
                }

                _globalDirectPathCooldownUntil = until;
                changed = true;
            }
            else
            {
                var key = AddressKey(virtualAddress);
                if (_directPathCooldownByAddress.TryGetValue(key, out var existing) && until <= existing)
                {
                    return;
                }

                _directPathCooldownByAddress[key] = until;
                changed = true;
            }

            if (changed)
            {
                _lastForwardingPaths.Clear();
                RebuildMeshRoutesLocked();
            }
        }

        if (changed)
        {
            var scope = virtualAddress is null ? "all peers" : virtualAddress.ToString();
            Log(ProxyNetworkerLogLevel.Warning,
                $"Virtual Network direct paths for {scope} suspended until {until:O}: {reason}");
        }
    }

    private bool IsDirectPathSuspendedLocked(IPAddress virtualAddress, DateTimeOffset now)
    {
        return now < _globalDirectPathCooldownUntil ||
            (_directPathCooldownByAddress.TryGetValue(AddressKey(virtualAddress), out var until) && now < until);
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
                _options.AuthenticationKey, _options.EncryptionEnabled);
            try
            {
                await SendFrameAsync(frame, probe.EndPoint, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
            {
                if (ex is SocketException)
                {
                    SuspendDirectPaths(probe.VirtualAddress, ex.Message);
                }
                else
                {
                    Log(ProxyNetworkerLogLevel.Debug,
                        $"Virtual Network path probe to {probe.EndPoint} for {probe.VirtualAddress} failed: {ex.Message}");
                }
            }
        }
    }

    private IPEndPoint[] GetLocalCandidateEndPoints(int port)
    {
        var endpoints = new List<IPEndPoint>();
        if (_options.IncludeLocalInterfaceCandidates)
        {
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

    private void RecordDirectTraffic(IPAddress virtualAddress, IPEndPoint remoteEndPoint, long bytesSent, long bytesReceived)
    {
        lock (_peersLock)
        {
            if (!_directPeersByAddress.TryGetValue(AddressKey(virtualAddress), out var state))
            {
                return;
            }

            var path = GetOrCreatePathLocked(state, remoteEndPoint, isRelay: false);
            path.BytesSent = SaturatingAdd(path.BytesSent, bytesSent);
            path.BytesReceived = SaturatingAdd(path.BytesReceived, bytesReceived);
            path.LastSuccess = DateTimeOffset.UtcNow;
        }
    }

    private IPAddress? FindDirectPeerAddressLocked(IPEndPoint remoteEndPoint)
    {
        foreach (var state in _directPeersByAddress.Values)
        {
            if (state.Candidates.Any(candidate => EndPointEquals(candidate, remoteEndPoint)) ||
                state.Paths.ContainsKey(EndPointKey(remoteEndPoint)))
            {
                return state.VirtualAddress;
            }
        }

        return null;
    }

    private static long SaturatingAdd(long current, long value)
    {
        if (value <= 0)
        {
            return current;
        }

        return current > long.MaxValue - value ? long.MaxValue : current + value;
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
        HandleTunPacket(e.PacketMemory);
    }

    private void HandleTunPacket(ReadOnlyMemory<byte> packet)
    {
        try
        {
            if (IsServer)
            {
                RoutePacket(packet, sourcePeer: null, CancellationToken.None);
                return;
            }

            var remoteEndPoint = _options.RemoteEndPoint;
            if (remoteEndPoint is not null)
            {
                if (!_options.EnableDirectPaths ||
                    !TryReadIPv4Destination(packet.Span, out var destination) ||
                    IsBroadcastOrMulticast(destination))
                {
                    SendPacketFrame(packet, remoteEndPoint, CancellationToken.None);
                    return;
                }

                SendRoutedPacket(packet, destination, CancellationToken.None);
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

    private void RoutePacket(
        ReadOnlyMemory<byte> packet,
        IPEndPoint? sourcePeer,
        CancellationToken cancellationToken,
        TunPacketBatch? tunBatch = null)
    {
        if (!TryReadIPv4Destination(packet.Span, out var destination))
        {
            return;
        }

        if (IsBroadcastOrMulticast(destination))
        {
            if (sourcePeer is not null)
            {
                SendTunPacketAsync(packet, tunBatch, cancellationToken).GetAwaiter().GetResult();
            }

            SendPacketToPeers(packet, exceptPeer: sourcePeer, cancellationToken);
            return;
        }

        if (destination.Equals(_options.NodeAddress))
        {
            if (sourcePeer is not null)
            {
                SendTunPacketAsync(packet, tunBatch, cancellationToken).GetAwaiter().GetResult();
            }

            return;
        }

        var peer = FindPeer(destination);
        if (peer is not null && !IsSamePeer(peer.EndPoint, sourcePeer))
        {
            SendPacketFrame(packet, peer.EndPoint, cancellationToken);
        }
    }

    private void SendPacketToPeers(ReadOnlyMemory<byte> packet, IPEndPoint? exceptPeer, CancellationToken cancellationToken)
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

            SendPacketFrame(packet, peer.EndPoint, cancellationToken);
        }
    }

    private async Task SendControlFrameAsync(VirtualNetworkFrameType type, IPEndPoint remoteEndPoint, CancellationToken cancellationToken)
    {
        var nodeName = string.IsNullOrWhiteSpace(_options.NodeName) ? _options.NodeAddress.ToString() : _options.NodeName;
        var payload = VirtualNetworkNodeInfo.FromEnvironment(nodeName).Encode();
        var frame = VirtualNetworkFrame.Create(type, _options.NodeAddress, payload, _options.AuthenticationKey, _options.EncryptionEnabled);
        await SendFrameAsync(frame, remoteEndPoint, cancellationToken).ConfigureAwait(false);
    }

    private async Task SendRelayRequiredAsync(bool relayRequired, CancellationToken cancellationToken)
    {
        var remoteEndPoint = _options.RemoteEndPoint;
        if (remoteEndPoint is null)
        {
            return;
        }

        var frame = VirtualNetworkFrame.Create(
            VirtualNetworkFrameType.RelayRequired,
            _options.NodeAddress,
            [relayRequired ? (byte)1 : (byte)0],
            _options.AuthenticationKey, _options.EncryptionEnabled);
        await SendFrameAsync(frame, remoteEndPoint, cancellationToken).ConfigureAwait(false);
    }

    private void SendPacketFrame(ReadOnlyMemory<byte> packet, IPEndPoint remoteEndPoint, CancellationToken cancellationToken)
    {
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
            SendFrame(frame, frameLength, remoteEndPoint, cancellationToken);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(frame);
        }
    }

    private void SendFrame(byte[] frame, int frameLength, IPEndPoint remoteEndPoint, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var batchSocket = _linuxUdpBatchSocket;
        if (batchSocket is not null)
        {
            batchSocket.Enqueue(frame.AsSpan(0, frameLength), remoteEndPoint);
            return;
        }

        var udpClient = _udpClient;
        if (udpClient is null)
        {
            return;
        }

        udpClient.Client.SendTo(frame, 0, frameLength, SocketFlags.None, remoteEndPoint);
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

    private ValueTask SendTunPacketAsync(
        ReadOnlyMemory<byte> packet,
        TunPacketBatch? tunBatch,
        CancellationToken cancellationToken)
    {
        if (tunBatch is not null)
        {
            tunBatch.Add(packet);
            return ValueTask.CompletedTask;
        }

        return _tunDevice.SendAsync(packet, cancellationToken);
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

            if (_globalDirectPathCooldownUntil <= now)
            {
                _globalDirectPathCooldownUntil = default;
            }

            foreach (var key in _directPathCooldownByAddress
                .Where(item => item.Value <= now)
                .Select(static item => item.Key)
                .ToArray())
            {
                _directPathCooldownByAddress.Remove(key);
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

        public long BytesSent { get; set; }

        public long BytesReceived { get; set; }

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
        IPAddress NextHopAddress,
        bool IsRelay,
        double? CostMilliseconds,
        string Path);
}
