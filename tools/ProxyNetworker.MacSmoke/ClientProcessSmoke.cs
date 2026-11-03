using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ProxyNetworker.Core;

[SupportedOSPlatform("macos")]
internal static class ClientProcessSmoke
{
    internal static async Task RunAsync(string executable, CancellationToken cancellationToken)
    {
        executable = Path.GetFullPath(executable);
        if (!File.Exists(executable))
        {
            throw new FileNotFoundException("Published client executable is missing.", executable);
        }

        var local = IPAddress.Parse("198.19.249.1");
        var peer = IPAddress.Parse("198.19.249.2");
        using var reservation = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)reservation.Client.LocalEndPoint!).Port;
        reservation.Dispose();
        var key = RandomNumberGenerator.GetBytes(32);
        using var server = new VirtualNetworkTunnel(new EchoTun(), new VirtualNetworkTunnelOptions
        {
            BindEndPoint = new IPEndPoint(IPAddress.Loopback, port),
            NodeAddress = peer,
            PrefixLength = 30,
            AuthenticationKey = key,
            EnableDirectPaths = false
        });
        await server.StartAsync(cancellationToken);
        var directory = Directory.CreateTempSubdirectory("pn-macos-config-");
        using var client = new Process
        {
            StartInfo = new ProcessStartInfo(executable)
            {
                WorkingDirectory = directory.FullName,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var launched = false;
        var diagnostics = new StringBuilder();
        var configPath = Path.Combine(directory.FullName, "appsettings.json");
        try
        {
            using (var stream = File.Create(configPath))
            using (var json = new Utf8JsonWriter(stream))
            {
                json.WriteStartObject();
                json.WritePropertyName("ProxyNetworkerClient");
                json.WriteStartObject();
                json.WritePropertyName("virtualNetwork");
                json.WriteStartObject();
                json.WriteBoolean("enabled", true);
                json.WriteString("name", "macos-published-client-ci");
                json.WriteString("serverHost", "127.0.0.1");
                json.WriteNumber("serverPort", port);
                json.WriteString("tunAddress", local.ToString());
                json.WriteNumber("prefixLength", 30);
                json.WriteNumber("mtu", 1400);
                json.WriteString("transportProtocol", "udp");
                json.WriteBoolean("encryptionEnabled", true);
                json.WriteBoolean("relayOnly", true);
                json.WriteBase64String("authenticationKey", key);
                json.WriteEndObject();
                json.WriteEndObject();
                json.WriteEndObject();
            }

            File.SetUnixFileMode(configPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            client.OutputDataReceived += (_, args) =>
            {
                if (args.Data is null) return;
                lock (diagnostics) diagnostics.AppendLine(args.Data);
                if (args.Data.Contains("client config is running.", StringComparison.Ordinal))
                    started.TrySetResult();
            };
            client.ErrorDataReceived += (_, args) =>
            {
                if (args.Data is not null)
                    lock (diagnostics) diagnostics.AppendLine(args.Data);
            };
            client.Start();
            launched = true;
            client.BeginOutputReadLine();
            client.BeginErrorReadLine();
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            using var ping = Process.Start(new ProcessStartInfo("/sbin/ping")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                ArgumentList = { "-n", "-c", "3", "-i", "0.2", "-W", "1000", peer.ToString() }
            }) ?? throw new IOException("Could not start the isolated ping.");
            var output = ping.StandardOutput.ReadToEndAsync();
            var error = ping.StandardError.ReadToEndAsync();
            await ping.WaitForExitAsync(cancellationToken);
            await Task.WhenAll(output, error);
            if (ping.ExitCode != 0)
            {
                throw new IOException("Published-client encrypted UDP local-stack ping failed.");
            }

            using var stop = Process.Start(new ProcessStartInfo("/bin/kill")
            {
                UseShellExecute = false,
                ArgumentList = { "-INT", client.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) }
            }) ?? throw new IOException("Could not stop the published-client smoke process.");
            await stop.WaitForExitAsync(cancellationToken);
            await client.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            if (client.ExitCode != 0)
            {
                throw new IOException($"Published client did not shut down cleanly: {client.ExitCode}.");
            }
        }
        catch
        {
            lock (diagnostics) Console.Error.WriteLine(diagnostics.ToString());
            throw;
        }
        finally
        {
            if (launched && !client.HasExited)
            {
                client.Kill();
                await client.WaitForExitAsync(CancellationToken.None);
            }

            File.Delete(configPath);
            directory.Delete();
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private sealed class EchoTun : ITunDevice
    {
        public string Name => "ci-echo-only";
        public bool IsStarted { get; private set; }
        public event EventHandler<TunPacketReceivedEventArgs>? PacketReceived;
        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            IsStarted = true;
            return Task.CompletedTask;
        }

        public ValueTask SendAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken = default)
        {
            var source = packet.Span;
            var header = source.Length < 20 ? 0 : (source[0] & 15) * 4;
            if (header < 20 || source.Length < header + 8 || source[9] != 1 || source[header] != 8)
                return ValueTask.CompletedTask;
            var reply = source.ToArray();
            source.Slice(16, 4).CopyTo(reply.AsSpan(12, 4));
            source.Slice(12, 4).CopyTo(reply.AsSpan(16, 4));
            reply[header] = 0;
            reply[header + 2] = reply[header + 3] = 0;
            BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(header + 2), Checksum(reply.AsSpan(header)));
            reply[10] = reply[11] = 0;
            BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(10), Checksum(reply.AsSpan(0, header)));
            PacketReceived?.Invoke(this, new TunPacketReceivedEventArgs(reply));
            return ValueTask.CompletedTask;
        }

        public void Dispose() => IsStarted = false;

        private static ushort Checksum(ReadOnlySpan<byte> bytes)
        {
            uint sum = 0;
            for (var index = 0; index < bytes.Length; index += 2)
                sum += index + 1 < bytes.Length ? BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(index, 2)) : (uint)bytes[index] << 8;
            while (sum >> 16 != 0) sum = (sum & 65535) + (sum >> 16);
            return (ushort)~sum;
        }
    }
}
