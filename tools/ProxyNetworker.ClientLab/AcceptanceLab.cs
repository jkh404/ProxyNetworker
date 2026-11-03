using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using ProxyNetworker.Client.Runtime;
using ProxyNetworker.Core;

/// <summary>Exercises the real management server with isolated credentials and userspace echo peers.</summary>
internal static class AcceptanceLab
{
    internal static async Task<int> RunAsync(string directory, string serverPath)
    {
        directory = Path.GetFullPath(directory);
        serverPath = Path.GetFullPath(serverPath);
        if (!File.Exists(serverPath)) throw new FileNotFoundException("Build the management server first.", serverPath);
        Directory.CreateDirectory(directory);
        if (File.Exists(Path.Combine(directory, "lab.db")))
            throw new IOException("Use a fresh acceptance directory; an existing lab database is never overwritten.");
        using var lifetime = new CancellationTokenSource();
        Console.CancelKeyPress += (_, args) => { args.Cancel = true; lifetime.Cancel(); };
        var httpPort = FreeTcpPort();
        var baseUri = new Uri($"http://127.0.0.1:{httpPort}/");
        await File.WriteAllTextAsync(Path.Combine(directory, "appsettings.json"), JsonSerializer.Serialize(new
        {
            Urls = baseUri.ToString().TrimEnd('/'),
            ConnectionStrings = new { ProxyNetworker = "Data Source=lab.db" },
            SystemSettings = new { PublicPortRangeStart = 47100, PublicPortRangeEnd = 47199 },
            NetworkDefaults = new
            {
                PublicListenAddress = "127.0.0.1", AdvertisedHost = "",
                VirtualNetworkGatewayAddress = "198.18.11.1", VirtualNetworkPrefixLength = 29, VirtualNetworkMtu = 1400
            }
        }), lifetime.Token);

        var password = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var logGate = new object();
        var info = new ProcessStartInfo(serverPath)
        {
            WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        info.ArgumentList.Add("--contentRoot"); info.ArgumentList.Add(directory);
        using var server = new Process { StartInfo = info };
        void Capture(object sender, DataReceivedEventArgs args)
        {
            if (args.Data is not { } line) return;
            var match = Regex.Match(line, @"Initial password: (\S+)\. Change this password");
            if (match.Success) password.TrySetResult(match.Groups[1].Value);
            line = Regex.Replace(line, @"Initial password: \S+", "Initial password: [redacted]");
            line = Regex.Replace(line, @"(?:vnet_|ptun_)[A-Za-z0-9_-]+", "[redacted]");
            lock (logGate) File.AppendAllText(Path.Combine(directory, "server.log"), line + Environment.NewLine);
        }
        server.OutputDataReceived += Capture;
        server.ErrorDataReceived += Capture;
        if (!server.Start()) throw new IOException("Could not start the isolated management server.");
        server.BeginOutputReadLine(); server.BeginErrorReadLine();
        try
        {
            using var handler = new HttpClientHandler { CookieContainer = new CookieContainer() };
            using var http = new HttpClient(handler) { BaseAddress = baseUri, Timeout = TimeSpan.FromSeconds(15) };
            var initialPassword = await password.Task.WaitAsync(TimeSpan.FromSeconds(30), lifetime.Token);
            await WaitForServerAsync(http, lifetime.Token);
            using (var login = await http.PostAsJsonAsync("api/auth/login", new { username = "sky", password = initialPassword }, lifetime.Token))
                login.EnsureSuccessStatusCode();
            var network = await PostAsync(http, "api/virtual-networks", new
            {
                name = "isolated-client-acceptance", gatewayAddress = "198.18.11.1",
                prefixLength = 29, mtu = 1400, transportProtocol = "udp", encryptionEnabled = true
            }, lifetime.Token);
            var networkId = network.GetProperty("id").GetString()!;
            var echoToken = await CreateTokenAsync(http, $"api/virtual-networks/{networkId}/tokens", lifetime.Token);
            await using var echo = new LocalClientController(new EchoPlatform());
            echo.LogReceived += log => Console.WriteLine($"Echo: {log.Level}: {log.Message}");
            await echo.ConnectAsync(new()
            {
                ApiServer = baseUri.ToString(),
                VirtualNetwork = new() { Enabled = true, Name = "pn-lab-echo", Token = echoToken.Value, RelayOnly = true }
            }, lifetime.Token);
            var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
            while (echo.State.State != ClientConnectionState.Running)
            {
                if (DateTimeOffset.UtcNow > deadline) throw new IOException("The userspace echo peer did not start.");
                await Task.Delay(100, lifetime.Token);
            }
            var echoAddress = echo.State.VirtualAddress!.Split('/')[0];

            var tcp = new TcpListener(IPAddress.Loopback, 0);
            tcp.Start();
            using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            var tcpPort = ((IPEndPoint)tcp.LocalEndpoint).Port;
            var udpPort = ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
            var tcpTask = EchoTcpAsync(tcp, lifetime.Token);
            var udpTask = EchoUdpAsync(udp, lifetime.Token);
            try
            {
                var clients = new List<object>();
                foreach (var target in new[] { "pc", "android" })
                {
                    var apiServer = target == "pc" ? baseUri.ToString() : $"http://10.0.2.2:{httpPort}/";
                    var networkToken = await CreateTokenAsync(http, $"api/virtual-networks/{networkId}/tokens", lifetime.Token);
                    var configuration = new ProxyNetworkerClientConfig
                    {
                        ApiServer = apiServer,
                        VirtualNetwork = new() { Enabled = true, Name = $"pn-{target}-test", Token = networkToken.Value, RelayOnly = true }
                    };
                    var publicPorts = new Dictionary<string, int>();
                    foreach (var protocol in new[] { "tcp", "udp" })
                    {
                        var definition = await PostAsync(http, "api/port-tunnels", new
                        {
                            name = $"{target}-{protocol}-echo", protocol,
                            privateHost = target == "pc" ? "127.0.0.1" : "10.0.2.2",
                            privatePort = protocol == "tcp" ? tcpPort : udpPort
                        }, lifetime.Token);
                        var id = definition.GetProperty("id").GetString()!;
                        publicPorts[protocol] = definition.GetProperty("publicPort").GetInt32();
                        var token = await CreateTokenAsync(http, $"api/port-tunnels/{id}/tokens", lifetime.Token);
                        configuration.PortTunnels.Add(new() { Name = $"{protocol} echo", Token = token.Value });
                    }
                    var configPath = Path.Combine(directory, $"{target}.json");
                    await new ClientConfigurationStore(configPath).SaveAsync(configuration, lifetime.Token);
                    clients.Add(new { target, configuration = configPath, address = networkToken.Address, tokenId = networkToken.Id, publicPorts });
                }
                await File.WriteAllTextAsync(Path.Combine(directory, "lab.json"), JsonSerializer.Serialize(new
                {
                    httpPort, serverPid = server.Id, networkId, echoAddress, clients,
                    stopFile = Path.Combine(directory, "stop")
                }, new JsonSerializerOptions { WriteIndented = true }), lifetime.Token);
                Console.WriteLine($"ACCEPTANCE_READY http={httpPort} echo={echoAddress} directory={directory}");
                while (!File.Exists(Path.Combine(directory, "stop")))
                    await Task.Delay(250, lifetime.Token);
            }
            finally
            {
                lifetime.Cancel();
                tcp.Stop(); udp.Dispose();
                try { await Task.WhenAll(tcpTask, udpTask); }
                catch (Exception exception) when (exception is OperationCanceledException or SocketException or ObjectDisposedException) { }
            }
            return 0;
        }
        finally
        {
            lifetime.Cancel();
            if (!server.HasExited) { server.Kill(entireProcessTree: true); await server.WaitForExitAsync(); }
        }
    }

    private static async Task<JsonElement> PostAsync(HttpClient http, string route, object value, CancellationToken token)
    {
        using var response = await http.PostAsJsonAsync(route, value, token);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>(token);
    }
    private static async Task<(string Id, string Value, string Address)> CreateTokenAsync(HttpClient http, string route, CancellationToken token)
    {
        var result = await PostAsync(http, route, new { tokenType = "Permanent" }, token);
        var details = result.GetProperty("token");
        return (details.GetProperty("id").GetString()!, result.GetProperty("plainTextToken").GetString()!,
            details.GetProperty("assignedVirtualAddress").GetString()!);
    }
    private static async Task WaitForServerAsync(HttpClient http, CancellationToken token)
    {
        var until = DateTimeOffset.UtcNow.AddSeconds(15);
        while (true)
        {
            try { using var response = await http.GetAsync("health", token); if (response.IsSuccessStatusCode) return; }
            catch (HttpRequestException) when (DateTimeOffset.UtcNow < until) { }
            if (DateTimeOffset.UtcNow >= until) throw new IOException("The isolated server did not become ready.");
            await Task.Delay(100, token);
        }
    }
    private static int FreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }
    private static async Task EchoTcpAsync(TcpListener listener, CancellationToken token)
    {
        var connections = new List<Task>();
        try
        {
            while (!token.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(token);
                connections.RemoveAll(task => task.IsCompleted);
                connections.Add(Task.Run(async () =>
                {
                    using (client)
                    {
                        try
                        {
                            var stream = client.GetStream();
                            var buffer = new byte[16384];
                            int count;
                            while ((count = await stream.ReadAsync(buffer, token)) > 0)
                                await stream.WriteAsync(buffer.AsMemory(0, count), token);
                        }
                        catch (Exception exception) when (exception is OperationCanceledException or IOException or SocketException) { }
                    }
                }, CancellationToken.None));
            }
        }
        finally { await Task.WhenAll(connections); }
    }
    private static async Task EchoUdpAsync(UdpClient udp, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var packet = await udp.ReceiveAsync(token);
            await udp.SendAsync(packet.Buffer, packet.RemoteEndPoint, token);
        }
    }

    private sealed class EchoPlatform : IClientPlatform
    {
        public bool SupportsQuic => false;
        public void ConfigureTransportSocket(Socket socket) { }
        public Task<ITunDevice> CreateTunAsync(TunDeviceOptions options, Action<ProxyNetworkerLogEntry> logger, CancellationToken cancellationToken) =>
            Task.FromResult<ITunDevice>(new EchoPeerDevice(options.Address));
    }
    private sealed class EchoPeerDevice(IPAddress address) : ITunDevice
    {
        private readonly byte[] _address = address.GetAddressBytes();
        public string Name => "userspace-echo-peer";
        public bool IsStarted { get; private set; }
        public event EventHandler<TunPacketReceivedEventArgs>? PacketReceived;
        public Task StartAsync(CancellationToken cancellationToken = default) { IsStarted = true; return Task.CompletedTask; }
        public ValueTask SendAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken = default)
        {
            var data = packet.Span;
            if (data.Length < 28 || data[0] != 0x45 || data[9] != 1 || data[20] != 8 || !data.Slice(16, 4).SequenceEqual(_address))
                return ValueTask.CompletedTask;
            var reply = data.ToArray();
            data.Slice(16, 4).CopyTo(reply.AsSpan(12)); data.Slice(12, 4).CopyTo(reply.AsSpan(16));
            reply[20] = 0; reply[22] = reply[23] = 0;
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(22), Checksum(reply.AsSpan(20)));
            // Swapping the two IPv4 addresses preserves the IPv4 header checksum.
            PacketReceived?.Invoke(this, new(reply));
            return ValueTask.CompletedTask;
        }
        private static ushort Checksum(ReadOnlySpan<byte> bytes)
        {
            uint sum = 0;
            var i = 0;
            for (; i + 1 < bytes.Length; i += 2) sum += System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(bytes[i..]);
            if (i < bytes.Length) sum += (uint)bytes[i] << 8;
            while (sum >> 16 != 0) sum = (sum & 65535) + (sum >> 16);
            return (ushort)~sum;
        }
        public void Dispose() => IsStarted = false;
    }
}
