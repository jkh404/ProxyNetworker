using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using ProxyNetworker.Client.Runtime;
using ProxyNetworker.Core;

if (args is ["acceptance", var directory, var serverPath])
    return await AcceptanceLab.RunAsync(directory, serverPath);
if (args is ["probe", var labPath, var target])
    return await AcceptanceProbe.RunAsync(labPath, target);

if (args is not [var configurationPath])
{
    Console.Error.WriteLine("Usage: ProxyNetworker.ClientLab <private-android-config-path>");
    return 2;
}

using var lifetime = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; lifetime.Cancel(); };
using var reservation = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
var port = ((IPEndPoint)reservation.Client.LocalEndPoint!).Port;
var key = RandomNumberGenerator.GetBytes(32);
var device = new EchoTun();
using var server = new VirtualNetworkTunnel(device, new()
{
    BindEndPoint = new(IPAddress.Loopback, port),
    NodeAddress = IPAddress.Parse("198.18.11.2"),
    PrefixLength = 30,
    AuthenticationKey = key,
    EncryptionEnabled = true,
    EnableDirectPaths = false
}, entry => Console.WriteLine($"{entry.Level}: {entry.Message}"));
reservation.Dispose();
await server.StartAsync(lifetime.Token);
await new ClientConfigurationStore(configurationPath).SaveAsync(new()
{
    VirtualNetwork = new()
    {
        Enabled = true, Name = "pn-android-lab",
        ServerHost = "10.0.2.2", ServerPort = port,
        TunAddress = "198.18.11.1", PrefixLength = 30,
        AuthenticationKey = key, EncryptionEnabled = true, RelayOnly = true
    }
}, lifetime.Token);
Console.WriteLine($"Loopback-only encrypted ICMP lab ready on UDP {port}; private configuration: {Path.GetFullPath(configurationPath)}");
try { await Task.Delay(Timeout.Infinite, lifetime.Token); }
catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
Console.WriteLine($"ICMP replies: {device.Replies}");
return 0;

internal sealed class EchoTun : ITunDevice
{
    private long _replies;
    public string Name => "loopback-icmp-lab";
    public bool IsStarted { get; private set; }
    internal long Replies => Interlocked.Read(ref _replies);
    public event EventHandler<TunPacketReceivedEventArgs>? PacketReceived;
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IsStarted = true;
        return Task.CompletedTask;
    }
    public ValueTask SendAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var source = packet.Span;
        if (source.Length < 28 || source[0] >> 4 != 4 || source[9] != 1) return ValueTask.CompletedTask;
        var header = (source[0] & 15) * 4;
        var length = BinaryPrimitives.ReadUInt16BigEndian(source[2..]);
        if (header < 20 || length < header + 8 || length > source.Length || source[header] != 8 || source[header + 1] != 0 ||
            !source.Slice(16, 4).SequenceEqual(new byte[] { 198, 18, 11, 2 })) return ValueTask.CompletedTask;
        var reply = source[..length].ToArray();
        source.Slice(16, 4).CopyTo(reply.AsSpan(12));
        source.Slice(12, 4).CopyTo(reply.AsSpan(16));
        reply[8] = 64;
        reply[10] = reply[11] = 0;
        BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(10), Checksum(reply.AsSpan(0, header)));
        reply[header] = 0;
        reply[header + 2] = reply[header + 3] = 0;
        BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(header + 2), Checksum(reply.AsSpan(header)));
        PacketReceived?.Invoke(this, new(reply));
        if (Interlocked.Increment(ref _replies) is var count && (count <= 3 || count % 10 == 0))
            Console.WriteLine($"ICMP reply {count}, bytes={length}");
        return ValueTask.CompletedTask;
    }
    private static ushort Checksum(ReadOnlySpan<byte> bytes)
    {
        uint sum = 0;
        var index = 0;
        for (; index + 1 < bytes.Length; index += 2) sum += BinaryPrimitives.ReadUInt16BigEndian(bytes[index..]);
        if (index < bytes.Length) sum += (uint)bytes[index] << 8;
        while (sum >> 16 != 0) sum = (sum & 65535) + (sum >> 16);
        return (ushort)~sum;
    }
    public void Dispose() => IsStarted = false;
}
