using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;

internal static class AcceptanceProbe
{
    internal static async Task<int> RunAsync(string labPath, string target)
    {
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(labPath));
        var client = document.RootElement.GetProperty("clients").EnumerateArray()
            .Single(item => item.GetProperty("target").GetString() == target);
        var ports = client.GetProperty("publicPorts");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var expected = RandomNumberGenerator.GetBytes(256 * 1024);
        var received = new byte[expected.Length];
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, ports.GetProperty("tcp").GetInt32(), deadline.Token);
        var stream = tcp.GetStream();
        await Task.WhenAll(
            stream.WriteAsync(expected, deadline.Token).AsTask(),
            stream.ReadExactlyAsync(received, deadline.Token).AsTask());
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(expected), SHA256.HashData(received)))
            throw new InvalidDataException("TCP port tunnel changed the test payload.");
        using var udp = new UdpClient();
        udp.Connect(IPAddress.Loopback, ports.GetProperty("udp").GetInt32());
        for (var index = 0; index < 20; index++)
        {
            var packet = RandomNumberGenerator.GetBytes(1200);
            BinaryPrimitives.WriteInt32LittleEndian(packet, index);
            await udp.SendAsync(packet, deadline.Token);
            var response = await udp.ReceiveAsync(deadline.Token);
            if (!response.Buffer.AsSpan().SequenceEqual(packet))
                throw new InvalidDataException($"UDP port tunnel changed datagram {index}.");
        }
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            target, tcpBytes = expected.Length, tcpSha256Verified = true,
            udpPackets = 20, udpPayloadBytes = 1200, udpPayloadsVerified = true,
            passed = true, capturedAt = DateTimeOffset.UtcNow
        }));
        return 0;
    }
}
