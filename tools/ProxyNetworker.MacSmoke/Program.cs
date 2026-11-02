using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using ProxyNetworker.Core;

if (!OperatingSystem.IsMacOS())
{
    Console.Error.WriteLine("This isolated native smoke test requires macOS and administrator privileges.");
    return 2;
}

var local = IPAddress.Parse("198.19.250.1");
var peer = IPAddress.Parse("198.19.250.2");
using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
using var device = TunDeviceFactory.Create(new TunDeviceOptions
{
    Name = "macos-ci-smoke",
    Address = local,
    PrefixLength = 30,
    Mtu = 1400
}, entry => Console.Error.WriteLine(entry.Message));

try
{
    var payload = RandomNumberGenerator.GetBytes(32);
    var reply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    device.PacketReceived += (_, args) =>
    {
        var packet = args.PacketMemory.Span;
        if (packet.Length < 28 || packet[9] != 1)
        {
            return;
        }

        var header = (packet[0] & 15) * 4;
        if (packet.Length == header + 8 + payload.Length &&
            packet[header] == 0 && packet[(header + 8)..].SequenceEqual(payload) &&
            packet.Slice(12, 4).SequenceEqual(local.GetAddressBytes()) &&
            packet.Slice(16, 4).SequenceEqual(peer.GetAddressBytes()))
        {
            reply.TrySetResult();
        }
    };

    await device.StartAsync(deadline.Token);
    var name = device.Name;
    var configuration = await Command("/sbin/ifconfig", [name]);
    Require(configuration.Code == 0 && configuration.Output.Contains("mtu 1400"), "utun MTU configuration");
    var route = await Command("/sbin/route", ["-n", "get", "-inet", peer.ToString()]);
    Require(route.Code == 0 && HasInterface(route.Output, name), "owned virtual-subnet route");
    await device.SendAsync(EchoRequest(peer, local, payload), deadline.Token);
    await reply.Task.WaitAsync(deadline.Token);

    // A competing startup must not overwrite or delete the first interface's route.
    using var conflict = TunDeviceFactory.Create(new TunDeviceOptions
    {
        Name = "macos-ci-conflict",
        Address = IPAddress.Parse("198.19.250.3"),
        PrefixLength = 30
    });
    var rejected = false;
    try
    {
        await conflict.StartAsync(deadline.Token);
    }
    catch (IOException)
    {
        rejected = true;
    }

    Require(rejected && !conflict.IsStarted, "conflicting route rejection");
    var retainedRoute = await Command("/sbin/route", ["-n", "get", "-inet", peer.ToString()]);
    Require(HasInterface(retainedRoute.Output, name), "existing route preservation after failure");
    var watch = Stopwatch.StartNew();
    device.Dispose();
    watch.Stop();
    Require(watch.Elapsed < TimeSpan.FromSeconds(3), "bounded idle read shutdown");
    Require((await Command("/sbin/ifconfig", [name])).Code != 0, "interface cleanup");
    var cleanedRoute = await Command("/sbin/route", ["-n", "get", "-inet", peer.ToString()]);
    Require(!HasInterface(cleanedRoute.Output, name), "interface-owned route cleanup");
    if (args.Length != 1)
    {
        throw new ArgumentException("Supply the published client executable to validate no-argument configuration startup.");
    }
    await ClientProcessSmoke.RunAsync(args[0], deadline.Token);

    Console.WriteLine(JsonSerializer.Serialize(new SmokeResult(
        RuntimeInformation.OSDescription, RuntimeInformation.ProcessArchitecture.ToString(),
        name, true, true, true, true, true, watch.ElapsedMilliseconds),
        SmokeJsonContext.Default.SmokeResult));
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"macOS native smoke: FAIL: {exception.Message}");
    return 1;
}

static bool HasInterface(string output, string name) =>
    output.Split('\n').Any(line => line.Trim().Equals($"interface: {name}", StringComparison.Ordinal));

static void Require(bool condition, string description)
{
    if (!condition)
    {
        throw new IOException("Failed smoke assertion: " + description);
    }
}

static async Task<(int Code, string Output)> Command(string file, string[] arguments)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    using var process = new Process
    {
        StartInfo = new ProcessStartInfo(file)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        }
    };
    foreach (var argument in arguments)
    {
        process.StartInfo.ArgumentList.Add(argument);
    }

    process.Start();
    var stdout = process.StandardOutput.ReadToEndAsync();
    var stderr = process.StandardError.ReadToEndAsync();
    try
    {
        await process.WaitForExitAsync(timeout.Token);
    }
    catch
    {
        if (!process.HasExited)
        {
            process.Kill();
        }

        await process.WaitForExitAsync();
        throw;
    }

    return (process.ExitCode, await stdout + await stderr);
}

static byte[] EchoRequest(IPAddress source, IPAddress destination, byte[] payload)
{
    var packet = new byte[28 + payload.Length];
    packet[0] = 0x45;
    BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), (ushort)packet.Length);
    packet[8] = 64;
    packet[9] = 1;
    source.GetAddressBytes().CopyTo(packet, 12);
    destination.GetAddressBytes().CopyTo(packet, 16);
    packet[20] = 8;
    BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(24), 0x504e);
    BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(26), 1);
    payload.CopyTo(packet, 28);
    BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(22), Checksum(packet.AsSpan(20)));
    BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(10), Checksum(packet.AsSpan(0, 20)));
    return packet;
}

static ushort Checksum(ReadOnlySpan<byte> bytes)
{
    uint sum = 0;
    for (var index = 0; index < bytes.Length; index += 2)
    {
        sum += index + 1 < bytes.Length
            ? BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(index, 2))
            : (uint)bytes[index] << 8;
    }

    while (sum >> 16 != 0)
    {
        sum = (sum & 65535) + (sum >> 16);
    }

    return (ushort)~sum;
}

internal sealed record SmokeResult(
    string OS, string Architecture, string Interface, bool LocalStackRoundTrip,
    bool ConflictRejected, bool InterfaceRemoved, bool OwnedRouteRemoved,
    bool PublishedClientEncryptedRoundTrip, long ShutdownMilliseconds);

[System.Text.Json.Serialization.JsonSerializable(typeof(SmokeResult))]
internal partial class SmokeJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
