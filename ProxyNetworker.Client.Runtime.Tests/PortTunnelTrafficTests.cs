using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using ProxyNetworker.Core.PortTunnels.Remote;

namespace ProxyNetworker.Client.Runtime.Tests;

public sealed class PortTunnelTrafficTests
{
    [Theory]
    [InlineData("tcp", false)]
    [InlineData("udp", false)]
    [InlineData("tcp", true)]
    [InlineData("udp", true)]
    public async Task CountersOnlyIncludePayloadDeliveredThroughRealSockets(string protocol, bool useSession)
    {
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var token = lifetime.Token;
        var tcp = new TcpListener(IPAddress.Loopback, 0);
        tcp.Start();
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var download = "delivered to the private service"u8.ToArray();
        var upload = "returned to the relay"u8.ToArray();
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, 0));
        builder.Logging.ClearProviders();
        await using var app = builder.Build();
        app.UseWebSockets();
        app.MapGet("/{**path}", async context =>
        {
            try
            {
                using var socket = await context.WebSockets.AcceptWebSocketAsync();
                using var sendLock = new SemaphoreSlim(1, 1);
                var config = new RemotePortTunnelConfig
                {
                    Name = "test", Protocol = protocol, PrivateHost = "127.0.0.1",
                    PrivatePort = protocol == "tcp" ? ((IPEndPoint)tcp.LocalEndpoint).Port : ((IPEndPoint)udp.Client.LocalEndPoint!).Port
                };
                await PortTunnelRelayWebSocket.SendAsync(socket, PortTunnelRelayFrame.Text(PortTunnelRelayFrameType.Ready, 0,
                    JsonSerializer.Serialize(config, ClientJsonContext.Default.RemotePortTunnelConfig)), sendLock, token);
                await PortTunnelRelayWebSocket.SendAsync(socket, new(PortTunnelRelayFrameType.Ping, 0, ReadOnlyMemory<byte>.Empty), sendLock, token);
                using var messages = new MemoryStream();
                var buffer = new byte[65536];
                var pong = await PortTunnelRelayWebSocket.ReceiveAsync(socket, buffer, messages, token);
                Assert.Equal(PortTunnelRelayFrameType.Ping, pong!.Type);
                if (protocol == "tcp")
                {
                    await PortTunnelRelayWebSocket.SendAsync(socket, new(PortTunnelRelayFrameType.TcpOpen, 1, ReadOnlyMemory<byte>.Empty), sendLock, token);
                    using var connection = await tcp.AcceptTcpClientAsync(token);
                    await PortTunnelRelayWebSocket.SendAsync(socket, new(PortTunnelRelayFrameType.TcpData, 1, download), sendLock, token);
                    var actual = new byte[download.Length];
                    await connection.GetStream().ReadExactlyAsync(actual, token);
                    Assert.Equal(download, actual);
                    await connection.GetStream().WriteAsync(upload, token);
                    var response = await PortTunnelRelayWebSocket.ReceiveAsync(socket, buffer, messages, token);
                    Assert.Equal(upload, response!.Payload.ToArray());
                }
                else
                {
                    await PortTunnelRelayWebSocket.SendAsync(socket, new(PortTunnelRelayFrameType.UdpDatagram, 1, download), sendLock, token);
                    var request = await udp.ReceiveAsync(token);
                    Assert.Equal(download, request.Buffer);
                    await udp.SendAsync(upload, request.RemoteEndPoint, token);
                    var response = await PortTunnelRelayWebSocket.ReceiveAsync(socket, buffer, messages, token);
                    Assert.Equal(upload, response!.Payload.ToArray());
                }
                complete.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception exception) { complete.TrySetException(exception); }
        });
        await app.StartAsync(token);
        using var client = new RemotePortTunnelClient(new Uri(app.Urls.Single().Replace("http:", "ws:")), "ptun_test_only");
        var session = new ClientSession(new DesktopClientPlatform());
        var run = useSession ? session.RunAsync(new()
        {
            ApiServer = app.Urls.Single(),
            PortTunnels = [new() { Token = "ptun_" + Guid.NewGuid().ToString("N") }]
        }, token) : client.RunAsync(token);
        long Sent() => useSession ? session.GetState().BytesSent : client.BytesSent;
        long Received() => useSession ? session.GetState().BytesReceived : client.BytesReceived;
        try
        {
            await complete.Task.WaitAsync(token);
            while (Sent() < upload.Length || Received() < download.Length) await Task.Delay(10, token);
            Assert.Equal(download.Length, Received());
            Assert.Equal(upload.Length, Sent());
        }
        finally
        {
            lifetime.Cancel();
            await run;
            tcp.Stop();
            await app.StopAsync(CancellationToken.None);
        }
    }
}
