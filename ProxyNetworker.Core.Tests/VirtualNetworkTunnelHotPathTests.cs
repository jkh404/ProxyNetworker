using System.Collections;
using System.Net;
using System.Reflection;
using ProxyNetworker.Core;

namespace ProxyNetworker.Core.Tests;

public sealed class VirtualNetworkTunnelHotPathTests
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public void ThrottledCleanupAllocatesNothingAndKeepsTheRouteSnapshot()
    {
        using var tunnel = CreateTunnel();
        AddDirectPath(tunnel, "10.66.0.2");
        Invoke(tunnel, "CleanupExpiredPeers");
        var routes = GetRoutes(tunnel);
        SetField(tunnel, "_nextPeerCleanupTick", long.MaxValue);
        var cleanup = (Action)GetMethod("CleanupExpiredPeers").CreateDelegate(typeof(Action), tunnel);
        cleanup();
        var before = GC.GetAllocatedBytesForCurrentThread();

        for (var index = 0; index < 1000; index++)
        {
            cleanup();
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
        Assert.Same(routes, GetRoutes(tunnel));
    }

    [Fact]
    public void CleanupWithoutTopologyChangesKeepsTheRouteSnapshot()
    {
        using var tunnel = CreateTunnel();
        AddDirectPath(tunnel, "10.66.0.2");
        Invoke(tunnel, "CleanupExpiredPeers");
        var routes = GetRoutes(tunnel);
        SetField(tunnel, "_nextPeerCleanupTick", 0L);

        Invoke(tunnel, "CleanupExpiredPeers");

        Assert.Same(routes, GetRoutes(tunnel));
    }

    [Fact]
    public void CleanupRemovesExpiredDirectRoutesAndInvalidatesForwardingCache()
    {
        using var tunnel = CreateTunnel();
        var address = AddDirectPath(tunnel, "10.66.0.2");
        Assert.NotNull(Invoke(tunnel, "SelectForwardingDecision", address));
        var routes = GetRoutes(tunnel);
        var peers = (IDictionary)GetField(tunnel, "_directPeersByAddress")!;
        var state = peers[address.ToString()]!;
        var paths = (IDictionary)state.GetType().GetProperty("Paths")!.GetValue(state)!;
        foreach (var path in paths.Values)
        {
            path!.GetType().GetProperty("LastSuccess")!
                .SetValue(path, DateTimeOffset.UtcNow - TimeSpan.FromMinutes(1));
        }
        SetField(tunnel, "_nextPeerCleanupTick", 0L);

        Invoke(tunnel, "CleanupExpiredPeers");

        Assert.NotSame(routes, GetRoutes(tunnel));
        Assert.False(GetRoutes(tunnel).TryGetRoute(address, out _));
        Assert.Null(GetField(tunnel, "_cachedForwardingDecision"));
    }

    [Fact]
    public void LinkStateChangesInvalidateTheForwardingCacheImmediately()
    {
        using var tunnel = CreateTunnel();
        var nextHop = AddDirectPath(tunnel, "10.66.0.3");
        var destination = IPAddress.Parse("10.66.0.4");
        var advertisement = new VirtualNetworkMeshAdvertisement(
            1, [VirtualNetworkMeshLink.Create(destination, 2, 0, 0)], DateTimeOffset.UtcNow);
        Invoke(tunnel, "HandleMeshLinkState", new VirtualNetworkFrame(
            VirtualNetworkFrameType.MeshLinkState, nextHop, advertisement.Encode()));
        Assert.NotNull(Invoke(tunnel, "SelectForwardingDecision", destination));
        Assert.True(GetRoutes(tunnel).TryGetRoute(destination, out _));
        var replacement = new VirtualNetworkMeshAdvertisement(2, [], DateTimeOffset.UtcNow);

        Invoke(tunnel, "HandleMeshLinkState", new VirtualNetworkFrame(
            VirtualNetworkFrameType.MeshLinkState, nextHop, replacement.Encode()));

        Assert.False(GetRoutes(tunnel).TryGetRoute(destination, out _));
        Assert.Null(GetField(tunnel, "_cachedForwardingDecision"));
    }

    [Fact]
    public void CandidateReplacementInvalidatesTheCachedEndpoint()
    {
        using var tunnel = CreateTunnel();
        var address = AddDirectPath(tunnel, "10.66.0.2");
        Assert.NotNull(Invoke(tunnel, "SelectForwardingDecision", address));

        Invoke(tunnel, "UpdateDirectCandidates", address,
            new[] { new IPEndPoint(IPAddress.Parse("192.0.2.22"), 13322) });

        Assert.Null(GetField(tunnel, "_cachedForwardingDecision"));
    }

    [Fact]
    public void FirstConfirmedDirectProbeRequestsImmediateTopologyPublish()
    {
        using var tunnel = CreateTunnel();
        var address = IPAddress.Parse("10.66.0.2");
        var endpoint = new IPEndPoint(IPAddress.Parse("192.0.2.2"), 13302);
        Invoke(tunnel, "UpdateDirectCandidates", address, new[] { endpoint });

        var firstProbe = new VirtualNetworkPathProbe(address, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 1, 1);
        QueueProbe(tunnel, address, endpoint, firstProbe);
        var first = (bool)Invoke(tunnel, "RecordPathMeasurement", address, endpoint, firstProbe)!;
        var secondProbe = new VirtualNetworkPathProbe(address, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 1, 2);
        QueueProbe(tunnel, address, endpoint, secondProbe);
        var second = (bool)Invoke(tunnel, "RecordPathMeasurement", address, endpoint, secondProbe)!;

        Assert.True(first);
        Assert.False(second);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CandidateAllowlistFiltersLocalAndRemoteAddressesWithoutRemovingRelay(bool restricted)
    {
        var privateAddress = IPAddress.Parse("192.0.2.2");
        var otherAddress = IPAddress.Parse("198.51.100.2");
        var relay = new IPEndPoint(IPAddress.Parse("203.0.113.1"), 13302);
        var candidates = new[] { new IPEndPoint(privateAddress, 13303), new IPEndPoint(otherAddress, 13304) };
        using var tunnel = new VirtualNetworkTunnel(new RecordingTunDevice(), new VirtualNetworkTunnelOptions
        {
            AuthenticationKey = new byte[32],
            NodeAddress = IPAddress.Parse("10.66.0.1"),
            RemoteEndPoint = relay,
            IncludeLocalInterfaceCandidates = false,
            AdditionalCandidateEndPoints = candidates,
            AllowedDirectCandidateAddresses = restricted ? new[] { privateAddress } : Array.Empty<IPAddress>()
        });

        var local = (IPEndPoint[])Invoke(tunnel, "GetLocalCandidateEndPoints", 13303)!;
        Assert.Equal(restricted ? candidates[..1] : candidates, local);
        var peer = IPAddress.Parse("10.66.0.2");
        Invoke(tunnel, "UpdateDirectCandidates", peer, candidates);
        var peers = (IDictionary)GetField(tunnel, "_directPeersByAddress")!;
        var state = peers[peer.ToString()]!;
        var paths = (IDictionary)state.GetType().GetProperty("Paths")!.GetValue(state)!;
        Assert.Equal(restricted ? 2 : 3, paths.Count);
        Assert.Contains(paths.Values.Cast<object>(), path =>
            (bool)path.GetType().GetProperty("IsRelay")!.GetValue(path)! &&
            relay.Equals(path.GetType().GetProperty("EndPoint")!.GetValue(path)));
    }

    [Fact]
    public void CandidateAllowlistRejectsIpv6()
    {
        var options = new VirtualNetworkTunnelOptions
        {
            AuthenticationKey = new byte[32],
            AllowedDirectCandidateAddresses = new[] { IPAddress.IPv6Loopback }
        };
        Assert.Throws<ArgumentException>(options.Validate);
    }

    [Fact]
    public async Task MeshBufferViewsAreConsumedBeforeTheTunBatchIsReleased()
    {
        var tun = new RecordingTunDevice();
        using var tunnel = CreateTunnel(tun);
        var batch = new TunPacketBatch();
        var buffers = new List<byte[]>();
        var expected = new List<byte[]>();
        for (var index = 0; index < 32; index++)
        {
            var packet = new byte[1400];
            packet.AsSpan().Fill((byte)index);
            expected.Add(packet);
            var payload = VirtualNetworkMeshPacket.Create(IPAddress.Parse("10.66.0.1"), packet);
            buffers.Add(payload);
            await (Task)Invoke(tunnel, "HandleMeshPacketAsync",
                new VirtualNetworkFrame(VirtualNetworkFrameType.MeshPacket, IPAddress.Parse("10.66.0.2"), payload),
                CancellationToken.None, batch, null)!;
        }

        Assert.Equal(32, batch.Count);
        await batch.FlushAsync(tun);
        foreach (var buffer in buffers)
        {
            buffer.AsSpan().Fill(0xff);
        }

        Assert.Equal(0, batch.Count);
        Assert.Equal(expected.Count, tun.Received.Count);
        for (var index = 0; index < expected.Count; index++)
        {
            Assert.Equal(expected[index], tun.Received[index]);
        }
    }

    private static VirtualNetworkTunnel CreateTunnel(ITunDevice? tun = null) => new(
        tun ?? new RecordingTunDevice(),
        new VirtualNetworkTunnelOptions
        {
            AuthenticationKey = new byte[32],
            NodeAddress = IPAddress.Parse("10.66.0.1"),
            IncludeLocalInterfaceCandidates = false
        });

    private static IPAddress AddDirectPath(VirtualNetworkTunnel tunnel, string value)
    {
        var address = IPAddress.Parse(value);
        var endpoint = new IPEndPoint(IPAddress.Parse("192.0.2." + value.Split('.')[^1]), 13302);
        Invoke(tunnel, "UpdateDirectCandidates", address, new[] { endpoint });
        var now = DateTimeOffset.UtcNow.AddMilliseconds(-1);
        var probe = new VirtualNetworkPathProbe(address, now.ToUnixTimeMilliseconds(), 1);
        var peers = (IDictionary)GetField(tunnel, "_directPeersByAddress")!;
        var state = peers[address.ToString()]!;
        var paths = (IDictionary)state.GetType().GetProperty("Paths")!.GetValue(state)!;
        var path = paths[endpoint.ToString()]!;
        var history = (VirtualNetworkPathProbeHistory)path.GetType().GetProperty("ProbeHistory")!.GetValue(path)!;
        history.RecordSent(probe, now, TimeSpan.FromSeconds(20));
        Invoke(tunnel, "RecordPathMeasurement", address, endpoint,
            probe);
        return address;
    }

    private static MethodInfo GetMethod(string name) => typeof(VirtualNetworkTunnel).GetMethod(name, PrivateInstance)!;

    private static object? Invoke(VirtualNetworkTunnel tunnel, string name, params object?[] arguments) =>
        GetMethod(name).Invoke(tunnel, arguments);

    private static object? GetField(VirtualNetworkTunnel tunnel, string name) =>
        typeof(VirtualNetworkTunnel).GetField(name, PrivateInstance)!.GetValue(tunnel);

    private static void SetField(VirtualNetworkTunnel tunnel, string name, object value) =>
        typeof(VirtualNetworkTunnel).GetField(name, PrivateInstance)!.SetValue(tunnel, value);

    private static void QueueProbe(
        VirtualNetworkTunnel tunnel,
        IPAddress address,
        IPEndPoint endpoint,
        VirtualNetworkPathProbe probe)
    {
        var peers = (IDictionary)GetField(tunnel, "_directPeersByAddress")!;
        var state = peers[address.ToString()]!;
        var paths = (IDictionary)state.GetType().GetProperty("Paths")!.GetValue(state)!;
        var path = paths[endpoint.ToString()]!;
        var history = path.GetType().GetProperty("ProbeHistory")!.GetValue(path)!;
        history.GetType().GetMethod("RecordSent", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(history, new object[] { probe, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(20) });
    }

    private static VirtualNetworkMeshRouteTable GetRoutes(VirtualNetworkTunnel tunnel) =>
        (VirtualNetworkMeshRouteTable)GetField(tunnel, "_meshRoutes")!;

    private sealed class RecordingTunDevice : ITunDevice, ITunBatchDevice
    {
        public string Name => "hot-path-test-tun";

        public bool IsStarted => false;

        public List<byte[]> Received { get; } = [];

        public event EventHandler<TunPacketReceivedEventArgs>? PacketReceived
        {
            add { }
            remove { }
        }

        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public ValueTask SendAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken = default)
        {
            Received.Add(packet.ToArray());
            return ValueTask.CompletedTask;
        }

        public async ValueTask SendBatchAsync(
            IReadOnlyList<ReadOnlyMemory<byte>> packets, CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            foreach (var packet in packets)
            {
                await SendAsync(packet, cancellationToken);
            }
        }

        public void Dispose() { }
    }
}
