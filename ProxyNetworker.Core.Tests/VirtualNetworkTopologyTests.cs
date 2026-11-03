using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using ProxyNetworker.Core;

namespace ProxyNetworker.Core.Tests;

public sealed class VirtualNetworkTopologyTests
{
    [Fact]
    public async Task EmptyAdvertisementDoesNotImmediatelyRemoveConfirmedDirectEdge()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var relayEndPoint = new IPEndPoint(IPAddress.Loopback, GetAvailableUdpPort());
        var serverAddress = IPAddress.Parse("10.77.0.1");
        var clientAAddress = IPAddress.Parse("10.77.0.5");
        var clientBAddress = IPAddress.Parse("10.77.0.70");
        using var relay = CreateRelay(relayEndPoint, serverAddress, key);
        using var clientA = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var clientB = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        await relay.StartAsync();
        await RegisterPeersAsync(clientA, clientB, relayEndPoint, clientAAddress, clientBAddress, key);

        var confirmed = new VirtualNetworkMeshAdvertisement(
            1,
            [VirtualNetworkMeshLink.Create(clientBAddress, rttMilliseconds: 3, jitterMilliseconds: 1, lossBasisPoints: 0)],
            DateTimeOffset.UtcNow);
        await SendAdvertisementAsync(clientA, relayEndPoint, clientAAddress, confirmed, key);
        await WaitForTopologyAsync(relay, snapshot => HasDirectEdge(snapshot, clientAAddress, clientBAddress));

        var transientlyEmpty = new VirtualNetworkMeshAdvertisement(2, [], DateTimeOffset.UtcNow);
        await SendAdvertisementAsync(clientA, relayEndPoint, clientAAddress, transientlyEmpty, key);
        await Task.Delay(200);

        Assert.True(HasDirectEdge(relay.GetTopologySnapshot(), clientAAddress, clientBAddress));
    }

    [Fact]
    public async Task MovingDirectTrafficCreatesTopologyEdgeWithoutLinkAdvertisement()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var relayEndPoint = new IPEndPoint(IPAddress.Loopback, GetAvailableUdpPort());
        var serverAddress = IPAddress.Parse("10.77.0.1");
        var clientAAddress = IPAddress.Parse("10.77.0.5");
        var clientBAddress = IPAddress.Parse("10.77.0.70");
        using var relay = CreateRelay(relayEndPoint, serverAddress, key);
        using var clientA = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var clientB = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        await relay.StartAsync();
        await RegisterPeersAsync(clientA, clientB, relayEndPoint, clientAAddress, clientBAddress, key);

        await SendTrafficReportAsync(
            clientA,
            relayEndPoint,
            clientAAddress,
            new VirtualNetworkMeshTrafficReport(1, [new VirtualNetworkMeshTraffic(clientBAddress, 1024, 2048)]),
            key);
        await Task.Delay(250);
        await SendTrafficReportAsync(
            clientA,
            relayEndPoint,
            clientAAddress,
            new VirtualNetworkMeshTrafficReport(2, [new VirtualNetworkMeshTraffic(clientBAddress, 4096, 8192)]),
            key);

        var topology = await WaitForTopologyAsync(
            relay,
            snapshot => snapshot.Edges.Any(edge =>
                edge.ConnectionType == VirtualNetworkTopologyConnectionTypes.Direct &&
                edge.SourceAddress.Equals(clientAAddress) &&
                edge.DestinationAddress.Equals(clientBAddress) &&
                edge.BytesPerSecondSent > 0));
        var directEdge = Assert.Single(topology.Edges, edge =>
            edge.ConnectionType == VirtualNetworkTopologyConnectionTypes.Direct &&
            edge.SourceAddress.Equals(clientAAddress) &&
            edge.DestinationAddress.Equals(clientBAddress));
        Assert.Null(directEdge.RttMilliseconds);
        Assert.True(directEdge.BytesPerSecondSent > 0);
        Assert.True(directEdge.BytesPerSecondReceived > 0);
    }

    [Fact]
    public async Task ConfirmedDirectEdgeExpiresWithoutFreshTelemetry()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var relayEndPoint = new IPEndPoint(IPAddress.Loopback, GetAvailableUdpPort());
        var serverAddress = IPAddress.Parse("10.77.0.1");
        var clientAAddress = IPAddress.Parse("10.77.0.5");
        var clientBAddress = IPAddress.Parse("10.77.0.70");
        using var relay = CreateRelay(relayEndPoint, serverAddress, key, TimeSpan.FromSeconds(2));
        using var clientA = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var clientB = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        await relay.StartAsync();
        await RegisterPeersAsync(clientA, clientB, relayEndPoint, clientAAddress, clientBAddress, key);

        var advertisement = new VirtualNetworkMeshAdvertisement(
            1,
            [VirtualNetworkMeshLink.Create(clientBAddress, rttMilliseconds: 3, jitterMilliseconds: 1, lossBasisPoints: 0)],
            DateTimeOffset.UtcNow);
        await SendAdvertisementAsync(clientA, relayEndPoint, clientAAddress, advertisement, key);
        await WaitForTopologyAsync(relay, snapshot => HasDirectEdge(snapshot, clientAAddress, clientBAddress));

        await Task.Delay(2300);

        Assert.False(HasDirectEdge(relay.GetTopologySnapshot(), clientAAddress, clientBAddress));
    }

    [Fact]
    public async Task RelaySnapshotIncludesServerRelayAndDirectTraffic()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var relayEndPoint = new IPEndPoint(IPAddress.Loopback, GetAvailableUdpPort());
        var serverAddress = IPAddress.Parse("10.77.0.1");
        var clientAAddress = IPAddress.Parse("10.77.0.5");
        var clientBAddress = IPAddress.Parse("10.77.0.70");
        using var relay = new VirtualNetworkRelay(new VirtualNetworkTunnelOptions
        {
            BindEndPoint = relayEndPoint,
            NodeAddress = serverAddress,
            PrefixLength = 24,
            NodeName = "test-server",
            AuthenticationKey = key
        });
        using var clientA = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var clientB = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        await relay.StartAsync();

        await SendAsync(clientA, relayEndPoint, VirtualNetworkFrame.Create(
            VirtualNetworkFrameType.Hello,
            clientAAddress,
            VirtualNetworkNodeInfo.FromEnvironment("client-a").Encode(),
            key));
        await SendAsync(clientB, relayEndPoint, VirtualNetworkFrame.Create(
            VirtualNetworkFrameType.Hello,
            clientBAddress,
            VirtualNetworkNodeInfo.FromEnvironment("client-b").Encode(),
            key));

        var packet = CreateIPv4Packet(clientAAddress, clientBAddress, payloadLength: 128);
        await SendAsync(clientA, relayEndPoint, VirtualNetworkFrame.Create(
            VirtualNetworkFrameType.Packet,
            clientAAddress,
            packet,
            key));

        var advertisement = new VirtualNetworkMeshAdvertisement(
            1,
            [VirtualNetworkMeshLink.Create(clientBAddress, rttMilliseconds: 8, jitterMilliseconds: 1, lossBasisPoints: 25)],
            DateTimeOffset.UtcNow);
        await SendAsync(clientA, relayEndPoint, VirtualNetworkFrame.Create(
            VirtualNetworkFrameType.MeshLinkState,
            clientAAddress,
            advertisement.Encode(),
            key));
        var trafficReport = new VirtualNetworkMeshTrafficReport(
            1,
            [new VirtualNetworkMeshTraffic(clientBAddress, BytesSent: 4096, BytesReceived: 8192)]);
        await SendAsync(clientA, relayEndPoint, VirtualNetworkFrame.Create(
            VirtualNetworkFrameType.MeshTrafficReport,
            clientAAddress,
            trafficReport.Encode(),
            key));
        await Task.Delay(200);
        var secondTrafficReport = new VirtualNetworkMeshTrafficReport(
            2,
            [new VirtualNetworkMeshTraffic(clientBAddress, BytesSent: 8192, BytesReceived: 16384)]);
        await SendAsync(clientA, relayEndPoint, VirtualNetworkFrame.Create(
            VirtualNetworkFrameType.MeshTrafficReport,
            clientAAddress,
            secondTrafficReport.Encode(),
            key));

        var topology = await WaitForTopologyAsync(
            relay,
            snapshot => snapshot.Nodes.Count == 3 &&
                snapshot.Edges.Count >= 3 &&
                snapshot.Edges.Any(edge => edge.ConnectionType == VirtualNetworkTopologyConnectionTypes.Direct && edge.BytesPerSecondSent > 0));

        var serverNode = Assert.Single(topology.Nodes, node => node.Role == "server");
        Assert.Equal(serverAddress, serverNode.VirtualAddress);
        Assert.Contains(topology.Edges, edge =>
            edge.ConnectionType == VirtualNetworkTopologyConnectionTypes.Relay &&
            edge.SourceAddress.Equals(serverAddress) &&
            edge.DestinationAddress.Equals(clientAAddress) &&
            edge.BytesReceived == packet.Length);
        Assert.Contains(topology.Edges, edge =>
            edge.ConnectionType == VirtualNetworkTopologyConnectionTypes.Relay &&
            edge.SourceAddress.Equals(serverAddress) &&
            edge.DestinationAddress.Equals(clientBAddress) &&
            edge.BytesSent == packet.Length);
        var directEdge = Assert.Single(topology.Edges, edge =>
            edge.ConnectionType == VirtualNetworkTopologyConnectionTypes.Direct &&
            edge.SourceAddress.Equals(clientAAddress) &&
            edge.DestinationAddress.Equals(clientBAddress));
        Assert.Equal(8, directEdge.RttMilliseconds);
        Assert.Equal(0.25, directEdge.LossPercent);
        Assert.Equal(8192, directEdge.BytesSent);
        Assert.Equal(16384, directEdge.BytesReceived);
        Assert.True(directEdge.BytesPerSecondSent > 0);
        Assert.True(directEdge.BytesPerSecondReceived > 0);
    }

    private static async Task<VirtualNetworkTopologySnapshot> WaitForTopologyAsync(
        VirtualNetworkRelay relay,
        Func<VirtualNetworkTopologySnapshot, bool> predicate)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(3);
        VirtualNetworkTopologySnapshot? snapshot = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            snapshot = relay.GetTopologySnapshot();
            if (predicate(snapshot))
            {
                return snapshot;
            }

            await Task.Delay(25);
        }

        Assert.NotNull(snapshot);
        return snapshot!;
    }

    private static async Task SendAsync(UdpClient client, IPEndPoint endPoint, byte[] frame)
    {
        await client.SendAsync(frame, frame.Length, endPoint);
    }

    private static VirtualNetworkRelay CreateRelay(
        IPEndPoint relayEndPoint,
        IPAddress serverAddress,
        byte[] key,
        TimeSpan? peerTimeout = null) =>
        new(new VirtualNetworkTunnelOptions
        {
            BindEndPoint = relayEndPoint,
            NodeAddress = serverAddress,
            PrefixLength = 24,
            NodeName = "test-server",
            AuthenticationKey = key,
            HeartbeatInterval = TimeSpan.FromSeconds(1),
            PeerTimeout = peerTimeout ?? TimeSpan.FromSeconds(20)
        });

    private static async Task RegisterPeersAsync(
        UdpClient clientA,
        UdpClient clientB,
        IPEndPoint relayEndPoint,
        IPAddress clientAAddress,
        IPAddress clientBAddress,
        byte[] key)
    {
        await SendAsync(clientA, relayEndPoint, VirtualNetworkFrame.Create(
            VirtualNetworkFrameType.Hello,
            clientAAddress,
            VirtualNetworkNodeInfo.FromEnvironment("client-a").Encode(),
            key));
        await SendAsync(clientB, relayEndPoint, VirtualNetworkFrame.Create(
            VirtualNetworkFrameType.Hello,
            clientBAddress,
            VirtualNetworkNodeInfo.FromEnvironment("client-b").Encode(),
            key));
        await Task.Delay(100);
    }

    private static Task SendAdvertisementAsync(
        UdpClient client,
        IPEndPoint relayEndPoint,
        IPAddress sourceAddress,
        VirtualNetworkMeshAdvertisement advertisement,
        byte[] key) =>
        SendAsync(client, relayEndPoint, VirtualNetworkFrame.Create(
            VirtualNetworkFrameType.MeshLinkState,
            sourceAddress,
            advertisement.Encode(),
            key));

    private static Task SendTrafficReportAsync(
        UdpClient client,
        IPEndPoint relayEndPoint,
        IPAddress sourceAddress,
        VirtualNetworkMeshTrafficReport report,
        byte[] key) =>
        SendAsync(client, relayEndPoint, VirtualNetworkFrame.Create(
            VirtualNetworkFrameType.MeshTrafficReport,
            sourceAddress,
            report.Encode(),
            key));

    private static bool HasDirectEdge(
        VirtualNetworkTopologySnapshot snapshot,
        IPAddress sourceAddress,
        IPAddress destinationAddress) =>
        snapshot.Edges.Any(edge =>
            edge.ConnectionType == VirtualNetworkTopologyConnectionTypes.Direct &&
            edge.SourceAddress.Equals(sourceAddress) &&
            edge.DestinationAddress.Equals(destinationAddress));

    private static byte[] CreateIPv4Packet(IPAddress source, IPAddress destination, int payloadLength)
    {
        var packet = new byte[20 + payloadLength];
        packet[0] = 0x45;
        source.GetAddressBytes().CopyTo(packet, 12);
        destination.GetAddressBytes().CopyTo(packet, 16);
        RandomNumberGenerator.Fill(packet.AsSpan(20));
        return packet;
    }

    private static int GetAvailableUdpPort()
    {
        using var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)client.Client.LocalEndPoint!).Port;
    }
}
