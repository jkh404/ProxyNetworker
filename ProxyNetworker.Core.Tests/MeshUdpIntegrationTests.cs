using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using ProxyNetworker.Core;

namespace ProxyNetworker.Core.Tests;

public sealed class MeshUdpIntegrationTests
{
    [Fact]
    public async Task ThreeNodesForwardPacketAcrossLowestCostMeshPath()
    {
        var authenticationKey = RandomNumberGenerator.GetBytes(32);
        var serverPort = GetFreeUdpPort();
        var relayPort = GetFreeUdpPort();
        var clientAPort = GetFreeUdpPort();
        var clientBPort = GetFreeUdpPort();
        var logs = new ConcurrentQueue<string>();

        using var server = new VirtualNetworkRelay(
            CreateOptions("10.66.0.1", serverPort, null, "mesh-test-server", authenticationKey, []),
            entry => logs.Enqueue($"server: {entry.Message}"));
        await server.StartAsync();

        var tunA = new TestTunDevice();
        var tunRelay = new TestTunDevice();
        var tunB = new TestTunDevice();
        using var clientA = CreateClient("10.66.0.2", clientAPort, "client-a", tunA, ["10.66.0.4"]);
        using var clientRelay = CreateClient("10.66.0.3", relayPort, "client-relay", tunRelay, []);
        using var clientB = CreateClient("10.66.0.4", clientBPort, "client-b", tunB, ["10.66.0.2"]);

        await clientA.StartAsync();
        await clientRelay.StartAsync();
        await clientB.StartAsync();
        await Task.Delay(TimeSpan.FromSeconds(5));

        var packet = CreateIPv4EchoPacket("10.66.0.2", "10.66.0.4");
        tunA.Inject(packet);
        var received = await tunB.WaitForPacketAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(packet, received);
        Assert.Contains(logs, line =>
            line.Contains("mesh path selected for 10.66.0.4", StringComparison.OrdinalIgnoreCase) &&
            line.Contains("10.66.0.2 -> 10.66.0.3 -> 10.66.0.4", StringComparison.OrdinalIgnoreCase));

        VirtualNetworkTunnel CreateClient(
            string address,
            int port,
            string name,
            ITunDevice tun,
            string[] excludedAddresses)
        {
            var options = CreateOptions(address, port, serverPort, name, authenticationKey, excludedAddresses);
            options.AdditionalCandidateEndPoints = [new IPEndPoint(IPAddress.Loopback, port)];
            return new VirtualNetworkTunnel(tun, options, entry => logs.Enqueue($"{name}: {entry.Message}"));
        }
    }

    private static VirtualNetworkTunnelOptions CreateOptions(
        string address,
        int port,
        int? remotePort,
        string name,
        byte[] authenticationKey,
        string[] excludedAddresses)
    {
        return new VirtualNetworkTunnelOptions
        {
            BindEndPoint = new IPEndPoint(IPAddress.Loopback, port),
            RemoteEndPoint = remotePort is null ? null : new IPEndPoint(IPAddress.Loopback, remotePort.Value),
            NodeAddress = IPAddress.Parse(address),
            PrefixLength = 24,
            NodeName = name,
            AuthenticationKey = authenticationKey,
            HeartbeatInterval = TimeSpan.FromSeconds(1),
            PeerTimeout = TimeSpan.FromSeconds(6),
            DirectPathExcludedAddresses = excludedAddresses.Select(IPAddress.Parse).ToArray()
        };
    }

    private static int GetFreeUdpPort()
    {
        using var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)client.Client.LocalEndPoint!).Port;
    }

    private static byte[] CreateIPv4EchoPacket(string source, string destination)
    {
        var packet = new byte[28];
        packet[0] = 0x45;
        packet[3] = 28;
        packet[8] = 64;
        packet[9] = 1;
        IPAddress.Parse(source).GetAddressBytes().CopyTo(packet, 12);
        IPAddress.Parse(destination).GetAddressBytes().CopyTo(packet, 16);
        packet[20] = 8;
        return packet;
    }

    private sealed class TestTunDevice : ITunDevice
    {
        private readonly TaskCompletionSource<byte[]> _received = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Name => "mesh-test-tun";

        public bool IsStarted { get; private set; }

        public event EventHandler<TunPacketReceivedEventArgs>? PacketReceived;

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            IsStarted = true;
            return Task.CompletedTask;
        }

        public ValueTask SendAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken = default)
        {
            _received.TrySetResult(packet.ToArray());
            return ValueTask.CompletedTask;
        }

        public void Inject(byte[] packet)
        {
            PacketReceived?.Invoke(this, new TunPacketReceivedEventArgs(packet));
        }

        public Task<byte[]> WaitForPacketAsync(TimeSpan timeout) => _received.Task.WaitAsync(timeout);

        public void Dispose()
        {
            IsStarted = false;
        }
    }
}
