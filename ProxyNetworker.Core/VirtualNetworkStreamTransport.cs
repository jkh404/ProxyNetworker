using System.Collections.Concurrent;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Channels;

#pragma warning disable CA1416
#pragma warning disable CA2252
#pragma warning disable SYSLIB0057

namespace ProxyNetworker.Core;

public sealed class VirtualNetworkStreamTunnel : IDisposable
{
    private const string ApplicationProtocol = "pn-vnet-1";
    private readonly ITunDevice _tunDevice;
    private readonly VirtualNetworkTunnelOptions _options;
    private readonly string _transportProtocol;
    private readonly Action<ProxyNetworkerLogEntry>? _logger;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private CancellationTokenSource? _runtimeCancellation;
    private Stream? _stream;
    private QuicConnection? _quicConnection;
    private Task? _receiveLoop;
    private Task? _heartbeatLoop;
    private int _started;

    public VirtualNetworkStreamTunnel(
        ITunDevice tunDevice,
        VirtualNetworkTunnelOptions options,
        string transportProtocol,
        Action<ProxyNetworkerLogEntry>? logger = null)
    {
        _tunDevice = tunDevice ?? throw new ArgumentNullException(nameof(tunDevice));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _transportProtocol = VirtualNetworkTransportProtocols.Normalize(transportProtocol);
        _logger = logger;
        _options.Validate();
        if (!VirtualNetworkTransportProtocols.UsesReliableStream(_transportProtocol))
        {
            throw new ArgumentException("A stream transport protocol is required.", nameof(transportProtocol));
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _started, 1) == 1)
        {
            return;
        }

        try
        {
            _runtimeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            await ConnectAsync(cancellationToken).ConfigureAwait(false);
            _tunDevice.PacketReceived += OnTunPacketReceived;
            await _tunDevice.StartAsync(cancellationToken).ConfigureAwait(false);
            await SendControlFrameAsync(VirtualNetworkFrameType.Hello, cancellationToken).ConfigureAwait(false);
            _receiveLoop = Task.Run(() => ReceiveLoopAsync(_runtimeCancellation.Token));
            _heartbeatLoop = Task.Run(() => HeartbeatLoopAsync(_runtimeCancellation.Token));
            Log(ProxyNetworkerLogLevel.Information, $"Virtual Network {_transportProtocol.ToUpperInvariant()} client connected to {_options.RemoteEndPoint} as {_options.NodeAddress}/{_options.PrefixLength}.");
        }
        catch
        {
            Interlocked.Exchange(ref _started, 0);
            DisposeTransport();
            throw;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _started, 0) == 1)
        {
            _tunDevice.PacketReceived -= OnTunPacketReceived;
            _runtimeCancellation?.Cancel();
            DisposeTransport();
            try
            {
                Task.WaitAll(new[] { _receiveLoop, _heartbeatLoop }.Where(static task => task is not null).Cast<Task>().ToArray(), TimeSpan.FromSeconds(2));
            }
            catch (AggregateException ex) when (ex.InnerExceptions.All(static item => item is OperationCanceledException or ObjectDisposedException or IOException or SocketException))
            {
            }
            _runtimeCancellation?.Dispose();
        }

        _sendLock.Dispose();
        _tunDevice.Dispose();
    }

    private async Task ConnectAsync(CancellationToken cancellationToken)
    {
        var remoteEndPoint = _options.RemoteEndPoint ?? throw new InvalidOperationException("Virtual Network remote endpoint is required.");
        if (!QuicConnection.IsSupported)
        {
            throw new PlatformNotSupportedException("QUIC is not supported by this platform or the required MsQuic library is unavailable.");
        }

        _quicConnection = await QuicConnection.ConnectAsync(new QuicClientConnectionOptions
        {
            RemoteEndPoint = remoteEndPoint,
            DefaultCloseErrorCode = 1,
            DefaultStreamErrorCode = 1,
            ClientAuthenticationOptions = new SslClientAuthenticationOptions
            {
                ApplicationProtocols = [new SslApplicationProtocol(ApplicationProtocol)],
                TargetHost = "ProxyNetworker",
                RemoteCertificateValidationCallback = static (_, _, _, _) => true
            }
        }, cancellationToken).ConfigureAwait(false);
        _stream = await _quicConnection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, cancellationToken).ConfigureAwait(false);
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            var stream = _stream ?? throw new InvalidOperationException("Virtual Network stream is not connected.");
            while (!cancellationToken.IsCancellationRequested)
            {
                var message = await VirtualNetworkMessageFraming.ReadAsync(stream, cancellationToken).ConfigureAwait(false);
                if (message is null)
                {
                    return;
                }

                if (VirtualNetworkFrame.TryParse(message, _options.AuthenticationKey, _options.EncryptionEnabled, out var frame) &&
                    frame.Type == VirtualNetworkFrameType.Packet)
                {
                    await _tunDevice.SendAsync(frame.Payload, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException or AuthenticationException or QuicException)
        {
            Log(ProxyNetworkerLogLevel.Warning, $"Virtual Network {_transportProtocol} receive loop stopped.", ex);
        }
    }

    private async Task HeartbeatLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_options.HeartbeatInterval, cancellationToken).ConfigureAwait(false);
                await SendControlFrameAsync(VirtualNetworkFrameType.Heartbeat, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException or QuicException)
            {
                Log(ProxyNetworkerLogLevel.Warning, $"Virtual Network {_transportProtocol} heartbeat failed.", ex);
            }
        }
    }

    private void OnTunPacketReceived(object? sender, TunPacketReceivedEventArgs e)
    {
        _ = HandleTunPacketAsync(e.Packet);
    }

    private async Task HandleTunPacketAsync(ReadOnlyMemory<byte> packet)
    {
        var cancellationToken = _runtimeCancellation?.Token ?? CancellationToken.None;
        try
        {
            var frame = VirtualNetworkFrame.Create(VirtualNetworkFrameType.Packet, _options.NodeAddress, packet.Span, _options.AuthenticationKey, _options.EncryptionEnabled);
            await SendFrameAsync(frame, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException or QuicException)
        {
            Log(ProxyNetworkerLogLevel.Warning, $"Virtual Network {_transportProtocol} packet send failed.", ex);
        }
    }

    private async Task SendControlFrameAsync(VirtualNetworkFrameType type, CancellationToken cancellationToken)
    {
        var nodeName = string.IsNullOrWhiteSpace(_options.NodeName) ? Environment.MachineName : _options.NodeName;
        var payload = VirtualNetworkNodeInfo.FromEnvironment(nodeName).Encode();
        var frame = VirtualNetworkFrame.Create(type, _options.NodeAddress, payload, _options.AuthenticationKey, _options.EncryptionEnabled);
        await SendFrameAsync(frame, cancellationToken).ConfigureAwait(false);
    }

    private async Task SendFrameAsync(byte[] frame, CancellationToken cancellationToken)
    {
        var stream = _stream ?? throw new InvalidOperationException("Virtual Network stream is not connected.");
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await VirtualNetworkMessageFraming.WriteAsync(stream, frame, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private void DisposeTransport()
    {
        _stream?.Dispose();
        _stream = null;
        _quicConnection?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _quicConnection = null;
    }

    private void Log(ProxyNetworkerLogLevel level, string message, Exception? exception = null)
    {
        _logger?.Invoke(new ProxyNetworkerLogEntry(level, message, exception));
    }

}

public sealed class VirtualNetworkStreamRelay : IVirtualNetworkRelay
{
    private const string ApplicationProtocol = "pn-vnet-1";
    private readonly VirtualNetworkTunnelOptions _options;
    private readonly string _transportProtocol;
    private readonly Action<ProxyNetworkerLogEntry>? _logger;
    private readonly ConcurrentDictionary<string, VirtualNetworkStreamSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _bannedAddresses = new(StringComparer.OrdinalIgnoreCase);
    private readonly Channel<AcceptedVirtualNetworkStream> _acceptedStreams = Channel.CreateUnbounded<AcceptedVirtualNetworkStream>();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _acceptDispatchLock = new(1, 1);
    private Task? _acceptLoop;
    private Task? _acceptDispatchLoop;
    private Task? _cleanupLoop;
    private QuicListener? _quicListener;
    private X509Certificate2? _certificate;
    private int _started;

    public VirtualNetworkStreamRelay(
        VirtualNetworkTunnelOptions options,
        string transportProtocol,
        Action<ProxyNetworkerLogEntry>? logger = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _transportProtocol = VirtualNetworkTransportProtocols.Normalize(transportProtocol);
        _logger = logger;
        _options.RemoteEndPoint = null;
        _options.Validate();
    }

    public int PeerCount => _sessions.Count;

    public void SetPeerBanned(IPAddress virtualAddress, bool isBanned)
    {
        var key = AddressKey(virtualAddress);
        lock (_bannedAddresses)
        {
            if (isBanned)
            {
                _bannedAddresses.Add(key);
            }
            else
            {
                _bannedAddresses.Remove(key);
            }
        }

        if (isBanned && _sessions.TryRemove(key, out var session))
        {
            session.Dispose();
        }
    }

    public IReadOnlyList<VirtualNetworkPeerSnapshot> GetPeerSnapshots()
    {
        return _sessions.Values.Select(static session => session.ToSnapshot()).OrderBy(static peer => peer.VirtualAddress.ToString()).ToArray();
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
        foreach (var session in _sessions.Values.Distinct())
        {
            session.UpdateRates(capturedAt);
            nodes.Add(session.ToTopologyNode());
            edges.Add(new VirtualNetworkTopologyEdgeSnapshot(
                _options.NodeAddress,
                session.VirtualAddress,
                VirtualNetworkTopologyConnectionTypes.Relay,
                _transportProtocol,
                null,
                null,
                null,
                null,
                session.BytesSent,
                session.BytesReceived,
                session.BytesPerSecondSent,
                session.BytesPerSecondReceived,
                session.LastSeenAt));
        }

        return new VirtualNetworkTopologySnapshot(_transportProtocol, capturedAt, nodes, edges);
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _started, 1) == 1)
        {
            return;
        }

        try
        {
            _acceptDispatchLoop = Task.Run(() => AcceptDispatchLoopAsync(_lifetime.Token));
            _cleanupLoop = Task.Run(() => CleanupLoopAsync(_lifetime.Token));
            if (_transportProtocol == VirtualNetworkTransportProtocols.Quic)
            {
                await StartQuicAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                throw new ArgumentException("Stream relay requires QUIC.", nameof(cancellationToken));
            }

            Log(ProxyNetworkerLogLevel.Information, $"Virtual Network {_transportProtocol.ToUpperInvariant()} relay listening on {_options.BindEndPoint} as gateway {_options.NodeAddress}/{_options.PrefixLength}.");
            return;
        }
        catch
        {
            Interlocked.Exchange(ref _started, 0);
            Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _started, 0) == 1)
        {
            _lifetime.Cancel();
            _quicListener?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _quicListener = null;
            _certificate?.Dispose();
            _certificate = null;
            foreach (var session in _sessions.Values)
            {
                session.Dispose();
            }
            _sessions.Clear();
            try
            {
                Task.WaitAll(new[] { _acceptLoop, _acceptDispatchLoop, _cleanupLoop }.Where(static task => task is not null).Cast<Task>().ToArray(), TimeSpan.FromSeconds(2));
            }
            catch (AggregateException ex) when (ex.InnerExceptions.All(static item => item is OperationCanceledException or ObjectDisposedException or IOException or SocketException or QuicException))
            {
            }
        }
        _lifetime.Dispose();
        _acceptDispatchLock.Dispose();
    }

    private async Task StartQuicAsync(CancellationToken cancellationToken)
    {
        if (!QuicConnection.IsSupported)
        {
            throw new PlatformNotSupportedException("QUIC is not supported by this platform or the required MsQuic library is unavailable.");
        }

        _certificate = CreateServerCertificate();
        _quicListener = await QuicListener.ListenAsync(new QuicListenerOptions
        {
            ListenEndPoint = _options.BindEndPoint,
            ApplicationProtocols = [new SslApplicationProtocol(ApplicationProtocol)],
            ConnectionOptionsCallback = (_, _, _) => ValueTask.FromResult(new QuicServerConnectionOptions
            {
                DefaultCloseErrorCode = 1,
                DefaultStreamErrorCode = 1,
                ServerAuthenticationOptions = new SslServerAuthenticationOptions
                {
                    ApplicationProtocols = [new SslApplicationProtocol(ApplicationProtocol)],
                    ServerCertificate = _certificate
                }
            })
        }, cancellationToken).ConfigureAwait(false);

        _acceptLoop = Task.Run(() => AcceptQuicLoopAsync(_lifetime.Token));
    }

    private async Task AcceptQuicLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var listener = _quicListener ?? throw new ObjectDisposedException(nameof(QuicListener));
                var connection = await listener.AcceptConnectionAsync(cancellationToken).ConfigureAwait(false);
                var stream = await connection.AcceptInboundStreamAsync(cancellationToken).ConfigureAwait(false);
                await _acceptedStreams.Writer.WriteAsync(new AcceptedVirtualNetworkStream(stream, connection.RemoteEndPoint, connection), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (QuicException ex)
            {
                Log(ProxyNetworkerLogLevel.Warning, "Virtual Network QUIC listener failed.", ex);
            }
        }
    }

    private async Task AcceptDispatchLoopAsync(CancellationToken cancellationToken)
    {
        await foreach (var accepted in _acceptedStreams.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            _ = Task.Run(async () =>
            {
                var session = new VirtualNetworkStreamSession(
                    $"stream-{Guid.NewGuid():N}",
                    accepted.RemoteEndPoint,
                    accepted.Stream,
                    _options.AuthenticationKey,
                    RemoveSession,
                    Log,
                    accepted.Connection);
                try
                {
                    await HandleSessionAsync(session, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    session.Dispose();
                }
            }, cancellationToken);
        }
    }

    private async Task HandleSessionAsync(VirtualNetworkStreamSession session, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var message = await VirtualNetworkMessageFraming.ReadAsync(session.Stream, cancellationToken).ConfigureAwait(false);
            if (message is null)
            {
                return;
            }

            if (!VirtualNetworkFrame.TryParse(message, _options.AuthenticationKey, _options.EncryptionEnabled, out var frame))
            {
                return;
            }

            session.Touch(frame.VirtualAddress, frame.Payload.Span);
            lock (_bannedAddresses)
            {
                if (_bannedAddresses.Contains(AddressKey(frame.VirtualAddress)))
                {
                    return;
                }
            }
            if (frame.VirtualAddress.Equals(_options.NodeAddress))
            {
                continue;
            }

            var key = AddressKey(frame.VirtualAddress);
            _sessions[key] = session;
            if (frame.Type == VirtualNetworkFrameType.Packet)
            {
                session.RecordReceived(frame.Payload.Length);
                await RoutePacketAsync(session, frame.Payload, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task RoutePacketAsync(VirtualNetworkStreamSession source, ReadOnlyMemory<byte> packet, CancellationToken cancellationToken)
    {
        if (!TryReadIPv4Destination(packet.Span, out var destination))
        {
            return;
        }

        VirtualNetworkStreamSession[] sessions;
        if (IsBroadcastOrMulticast(destination))
        {
            sessions = _sessions.Values.Where(session => !ReferenceEquals(session, source)).Distinct().ToArray();
        }
        else if (destination.Equals(_options.NodeAddress))
        {
            return;
        }
        else if (_sessions.TryGetValue(AddressKey(destination), out var target) && !ReferenceEquals(target, source))
        {
            sessions = [target];
        }
        else
        {
            return;
        }

        var frame = VirtualNetworkFrame.Create(VirtualNetworkFrameType.Packet, _options.NodeAddress, packet.Span, _options.AuthenticationKey, _options.EncryptionEnabled);
        foreach (var session in sessions)
        {
            await session.SendFrameAsync(frame, cancellationToken).ConfigureAwait(false);
            session.RecordSent(packet.Length);
        }
    }

    private async Task CleanupLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_options.HeartbeatInterval, cancellationToken).ConfigureAwait(false);
                var expiresBefore = DateTimeOffset.UtcNow - _options.PeerTimeout;
                foreach (var session in _sessions.Values.Distinct().Where(session => session.LastSeenAt < expiresBefore).ToArray())
                {
                    RemoveSession(session);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Log(ProxyNetworkerLogLevel.Warning, $"Virtual Network {_transportProtocol} cleanup failed.", ex);
            }
        }
    }

    private void RemoveSession(VirtualNetworkStreamSession session)
    {
        foreach (var item in _sessions.Where(pair => ReferenceEquals(pair.Value, session)).ToArray())
        {
            _sessions.TryRemove(item.Key, out _);
        }
        session.Dispose();
    }

    private static X509Certificate2 CreateServerCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=ProxyNetworker", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, false));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, false));
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("ProxyNetworker");
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5));
        return new X509Certificate2(
            generated.Export(X509ContentType.Pfx),
            (string?)null,
            X509KeyStorageFlags.Exportable);
    }

    private static bool TryReadIPv4Destination(ReadOnlySpan<byte> packet, out IPAddress destination)
    {
        destination = IPAddress.None;
        if (packet.Length < 20 || (packet[0] >> 4) != 4)
        {
            return false;
        }
        destination = new IPAddress(packet.Slice(16, 4));
        return true;
    }

    private static bool IsBroadcastOrMulticast(IPAddress destination)
    {
        var bytes = destination.GetAddressBytes();
        return bytes[0] is >= 224 and <= 239 || destination.Equals(IPAddress.Broadcast) || bytes[3] == 255;
    }

    private static string AddressKey(IPAddress address) => address.ToString();
    private void Log(ProxyNetworkerLogLevel level, string message, Exception? exception = null)
    {
        _logger?.Invoke(new ProxyNetworkerLogEntry(level, message, exception));
    }

    private sealed record AcceptedVirtualNetworkStream(Stream Stream, IPEndPoint RemoteEndPoint, QuicConnection? Connection);
}

internal sealed class VirtualNetworkStreamSession : IDisposable
{
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly byte[] _authenticationKey;
    private readonly Action<VirtualNetworkStreamSession> _remove;
    private readonly Action<ProxyNetworkerLogLevel, string, Exception?> _log;
    private readonly QuicConnection? _quicConnection;
    private readonly object _rateLock = new();
    private int _disposed;
    private long _bytesSent;
    private long _bytesReceived;
    private long _rateSampleBytesSent;
    private long _rateSampleBytesReceived;
    private DateTimeOffset _rateSampleAt = DateTimeOffset.UtcNow;
    private double _bytesPerSecondSent;
    private double _bytesPerSecondReceived;

    public VirtualNetworkStreamSession(
        string id,
        IPEndPoint remoteEndPoint,
        Stream stream,
        byte[] authenticationKey,
        Action<VirtualNetworkStreamSession> remove,
        Action<ProxyNetworkerLogLevel, string, Exception?> log,
        QuicConnection? quicConnection = null)
    {
        Id = id;
        RemoteEndPoint = remoteEndPoint;
        Stream = stream;
        _authenticationKey = authenticationKey;
        _remove = remove;
        _log = log;
        _quicConnection = quicConnection;
        NodeInfo = VirtualNetworkNodeInfo.FromEnvironment(Environment.MachineName);
    }

    public string Id { get; }
    public IPEndPoint RemoteEndPoint { get; }
    public Stream Stream { get; }
    public IPAddress VirtualAddress { get; private set; } = IPAddress.None;
    public VirtualNetworkNodeInfo NodeInfo { get; private set; }
    public DateTimeOffset ConnectedAt { get; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastSeenAt { get; private set; } = DateTimeOffset.UtcNow;
    public long BytesSent => Interlocked.Read(ref _bytesSent);
    public long BytesReceived => Interlocked.Read(ref _bytesReceived);
    public double BytesPerSecondSent { get { lock (_rateLock) { return _bytesPerSecondSent; } } }
    public double BytesPerSecondReceived { get { lock (_rateLock) { return _bytesPerSecondReceived; } } }

    public void Touch(IPAddress virtualAddress, ReadOnlySpan<byte> nodeInfoPayload)
    {
        VirtualAddress = virtualAddress;
        LastSeenAt = DateTimeOffset.UtcNow;
        if (VirtualNetworkNodeInfo.TryParse(nodeInfoPayload, out var info))
        {
            NodeInfo = info;
        }
    }

    public async Task SendFrameAsync(byte[] frame, CancellationToken cancellationToken)
    {
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await VirtualNetworkMessageFraming.WriteAsync(Stream, frame, cancellationToken).ConfigureAwait(false);
            await Stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public void RecordReceived(int bytes)
    {
        if (bytes > 0)
        {
            Interlocked.Add(ref _bytesReceived, bytes);
        }
    }

    public void RecordSent(int bytes)
    {
        if (bytes > 0)
        {
            Interlocked.Add(ref _bytesSent, bytes);
        }
    }

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

    public VirtualNetworkPeerSnapshot ToSnapshot()
    {
        return new VirtualNetworkPeerSnapshot(
            VirtualAddress,
            NodeInfo.NodeName,
            NodeInfo.MachineName,
            NodeInfo.OsDescription,
            NodeInfo.OsArchitecture,
            NodeInfo.ProcessArchitecture,
            NodeInfo.ClientVersion,
            RemoteEndPoint,
            Array.Empty<IPEndPoint>(),
            BytesSent,
            BytesReceived,
            ConnectedAt,
            LastSeenAt);
    }

    public VirtualNetworkTopologyNodeSnapshot ToTopologyNode()
    {
        return new VirtualNetworkTopologyNodeSnapshot(
            VirtualAddress,
            "client",
            NodeInfo.NodeName,
            NodeInfo.MachineName,
            NodeInfo.OsDescription,
            NodeInfo.OsArchitecture,
            NodeInfo.ProcessArchitecture,
            NodeInfo.ClientVersion,
            true,
            BytesSent,
            BytesReceived,
            ConnectedAt,
            LastSeenAt);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }
        try { Stream.Dispose(); } catch { }
        try { _quicConnection?.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
        _sendLock.Dispose();
    }
}
