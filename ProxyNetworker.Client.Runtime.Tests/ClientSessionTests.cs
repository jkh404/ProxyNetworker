using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using ProxyNetworker.Core;

namespace ProxyNetworker.Client.Runtime.Tests;

public sealed class ClientSessionTests
{
    [Fact]
    public void RemoteConfigurationPreservesLocalBindPortAndPathPreferences()
    {
        var local = new DirectVirtualNetworkClientConfig
        {
            Name = "local-node", BindPort = 12345, RelayOnly = true, StableDirectPathSelection = false
        };
        var remote = new VirtualNetworkClientConfigResponse("test", "network", "0.0.0.0", 47100, "198.18.11.1",
            "198.18.11.4", 29, 1400, "udp", true, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        var current = ClientSession.FromRemote(remote, local, "https://example.invalid/");
        Assert.Equal(12345, current.BindPort);
        Assert.Equal("example.invalid", current.ServerHost);
        Assert.Equal("local-node", current.Name);
        Assert.True(current.RelayOnly);
        Assert.False(current.StableDirectPathSelection);
    }

    [Fact]
    public async Task UnsupportedQuicFailsWithoutOpeningATunOrSilentlySelectingUdp()
    {
        var platform = new MemoryPlatform();
        var session = new ClientSession(platform);
        await Assert.ThrowsAsync<PlatformNotSupportedException>(() => session.RunAsync(new()
        {
            VirtualNetwork = new()
            {
                Enabled = true, TransportProtocol = "quic", AuthenticationKey = RandomNumberGenerator.GetBytes(32)
            }
        }, CancellationToken.None));
        Assert.False(platform.Device.IsStarted);
        Assert.False(platform.Protected);
        Assert.Equal(ClientConnectionState.Failed, session.GetState().State);
        Assert.Contains("QUIC", session.GetState().Error);
    }

    [Fact]
    public async Task SocketProtectionRunsBeforeTheTunStarts()
    {
        var tun = new MemoryTun();
        var protectedSocket = false;
        tun.Starting = () => Assert.True(protectedSocket);
        using var tunnel = new VirtualNetworkTunnel(tun, new()
        {
            AuthenticationKey = RandomNumberGenerator.GetBytes(32),
            ConfigureTransportSocket = socket => { Assert.True(socket.IsBound); protectedSocket = true; }
        });
        await tunnel.StartAsync();
        Assert.True(protectedSocket);
    }

    [Fact]
    public async Task FailedSocketProtectionDoesNotStartTun()
    {
        var tun = new MemoryTun();
        using var tunnel = new VirtualNetworkTunnel(tun, new()
        {
            AuthenticationKey = RandomNumberGenerator.GetBytes(32),
            ConfigureTransportSocket = _ => throw new IOException("protection denied")
        });
        await Assert.ThrowsAsync<IOException>(() => tunnel.StartAsync());
        Assert.False(tun.IsStarted);
    }

    [Fact]
    public async Task SessionCarriesAuthenticatedPacketsAndStopsWithItsOwnedDevice()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        using var reservation = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)reservation.Client.LocalEndPoint!).Port;
        var serverTun = new MemoryTun();
        using var server = new VirtualNetworkTunnel(serverTun, new()
        {
            BindEndPoint = new(IPAddress.Loopback, port),
            NodeAddress = IPAddress.Parse("198.18.11.2"), AuthenticationKey = key,
            EnableDirectPaths = false
        });
        reservation.Dispose();
        await server.StartAsync();
        var platform = new MemoryPlatform();
        var session = new ClientSession(platform);
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var run = session.RunAsync(new()
        {
            VirtualNetwork = new()
            {
                Enabled = true, ServerHost = "127.0.0.1", ServerPort = port,
                TunAddress = "198.18.11.1", PrefixLength = 30,
                AuthenticationKey = key, RelayOnly = true
            }
        }, lifetime.Token);
        while (session.GetState().State != ClientConnectionState.Running)
            await Task.Delay(10, lifetime.Token);
        var packet = new byte[28]; packet[0] = 0x45; packet[3] = 28;
        IPAddress.Parse("198.18.11.1").GetAddressBytes().CopyTo(packet, 12);
        IPAddress.Parse("198.18.11.2").GetAddressBytes().CopyTo(packet, 16);
        platform.Device.Inject(packet);
        Assert.Equal(packet, await serverTun.Received.Task.WaitAsync(lifetime.Token));
        Assert.True(platform.Protected);
        Assert.Equal(28, session.GetState().BytesSent);
        lifetime.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(platform.Device.Disposed);
    }

    private sealed class MemoryPlatform : IClientPlatform
    {
        internal MemoryTun Device { get; } = new();
        internal bool Protected;
        public bool SupportsQuic => false;
        public void ConfigureTransportSocket(Socket socket) => Protected = true;
        public Task<ITunDevice> CreateTunAsync(TunDeviceOptions options, Action<ProxyNetworkerLogEntry> logger, CancellationToken cancellationToken) =>
            Task.FromResult<ITunDevice>(Device);
    }
    private sealed class MemoryTun : ITunDevice
    {
        public string Name => "memory";
        public bool IsStarted { get; private set; }
        internal bool Disposed;
        internal Action? Starting;
        internal TaskCompletionSource<byte[]> Received { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public event EventHandler<TunPacketReceivedEventArgs>? PacketReceived;
        public Task StartAsync(CancellationToken cancellationToken = default)
        { Starting?.Invoke(); IsStarted = true; return Task.CompletedTask; }
        public ValueTask SendAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken = default)
        { Received.TrySetResult(packet.ToArray()); return ValueTask.CompletedTask; }
        public void Inject(byte[] packet) => PacketReceived?.Invoke(this, new(packet));
        public void Dispose() { Disposed = true; IsStarted = false; }
    }
}
