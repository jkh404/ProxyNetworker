using System.Net;
using System.Net.Sockets;
using ProxyNetworker.Core;
using ProxyNetworker.Core.Platforms.Linux;
using ProxyNetworker.Core.PortTunnels;
using Serilog;
using Serilog.Events;

internal static class ProxyNetworkerClientApp
{
    public static async Task<int> RunAsync(CommandLine commandLine)
    {
        try
        {
            return await RunCoreAsync(commandLine).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (ArgumentException ex)
        {
            Log.Warning(ex, "Client command failed: {Message}", ex.Message);
            Console.Error.WriteLine(ex.Message);
            return 2;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Client command failed.");
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static async Task<int> RunCoreAsync(CommandLine commandLine)
    {
        var helpRequested = commandLine.CommandIs("help") || commandLine.HasOption("help");
        if (helpRequested)
        {
            PrintUsage();
            return 0;
        }

        if (commandLine.Arguments.Count == 0)
        {
            var defaultConfigPath = ResolveDefaultConfigPath();
            if (File.Exists(defaultConfigPath))
            {
                return await RunConfigAsync(commandLine).ConfigureAwait(false);
            }

            PrintUsage();
            return 2;
        }

        if (commandLine.CommandIs("connect"))
        {
            return await RunTokenConnectAsync(commandLine, tokenArgumentIndex: 1).ConfigureAwait(false);
        }

        if (commandLine.CommandIs("run"))
        {
            return await RunConfigAsync(commandLine).ConfigureAwait(false);
        }

        if (commandLine.CommandIs("init-config"))
        {
            return await InitConfigAsync(commandLine).ConfigureAwait(false);
        }

        if (commandLine.CommandIs("auth"))
        {
            return await RunAuthCommandAsync(commandLine).ConfigureAwait(false);
        }

        if (commandLine.CommandIs("port-tunnel"))
        {
            return await RunPortTunnelCommandAsync(commandLine).ConfigureAwait(false);
        }

        if (commandLine.CommandIs("virtual-network"))
        {
            return await RunVirtualNetworkCommandAsync(commandLine).ConfigureAwait(false);
        }

        return await RunVirtualNetworkDirectAsync(commandLine, serverArgumentIndex: 0).ConfigureAwait(false);
    }

    private static async Task<int> RunPortTunnelCommandAsync(CommandLine commandLine)
    {
        if (commandLine.SubCommandIs("create"))
        {
            return await CreatePortTunnelAndTokenAsync(commandLine).ConfigureAwait(false);
        }

        if (commandLine.SubCommandIs("connect") || commandLine.HasOption("server") || commandLine.HasOption("token"))
        {
            return await RunTokenConnectAsync(commandLine, tokenArgumentIndex: 2).ConfigureAwait(false);
        }

        return await RunLocalPortTunnelAsync(commandLine).ConfigureAwait(false);
    }

    private static async Task<int> RunVirtualNetworkCommandAsync(CommandLine commandLine)
    {
        if (commandLine.SubCommandIs("create"))
        {
            return await CreateVirtualNetworkAndTokenAsync(commandLine).ConfigureAwait(false);
        }

        var token = commandLine.Option("token", "PN_TOKEN");
        if (!string.IsNullOrWhiteSpace(token) || commandLine.SubCommandIs("connect-token"))
        {
            var server = RequiredValue(commandLine.Option("server", "PN_SERVER"), "--server");
            token ??= commandLine.Argument(2);
            token = RequiredValue(token, "--token").Trim();
            return await RunVirtualNetworkTokenAsync(server, token, commandLine.Option("name"), IsRelayOnly(commandLine)).ConfigureAwait(false);
        }

        return await RunVirtualNetworkDirectAsync(commandLine, serverArgumentIndex: 1).ConfigureAwait(false);
    }

    private static async Task<int> RunAuthCommandAsync(CommandLine commandLine)
    {
        if (!commandLine.SubCommandIs("change-password"))
        {
            PrintUsage();
            return 2;
        }

        var server = RequiredValue(commandLine.Option("server", "PN_SERVER"), "--server");
        var username = RequiredValue(commandLine.Option("username", "PN_USERNAME"), "--username");
        var currentPassword = commandLine.Option("password", "PN_PASSWORD") ?? ReadPassword("Current password: ");
        var newPassword = commandLine.Option("new-password") ?? ReadPassword("New password: ");

        using var shutdown = CreateShutdownSource();
        using var api = new ManagementApiClient(server);
        await api.LoginAsync(username, currentPassword, shutdown.Token).ConfigureAwait(false);
        await api.ChangePasswordAsync(currentPassword, newPassword, shutdown.Token).ConfigureAwait(false);

        Console.WriteLine("Password changed.");
        return 0;
    }

    private static async Task<int> CreatePortTunnelAndTokenAsync(CommandLine commandLine)
    {
        var server = RequiredValue(commandLine.Option("server", "PN_SERVER"), "--server");
        var username = RequiredValue(commandLine.Option("username", "PN_USERNAME"), "--username");
        var password = commandLine.Option("password", "PN_PASSWORD") ?? ReadPassword("Password: ");
        var protocol = commandLine.Option("protocol") ?? "tcp";
        var target = ParseHostEndPoint(commandLine.Option("target") ?? commandLine.Option("private") ?? RequiredValue(null, "--target"));
        var name = commandLine.Option("name") ?? $"client-{target.Port}";
        var tokenType = commandLine.Option("token-type") ?? "Permanent";
        var validFrom = ParseOptionalDateTimeOffset(commandLine.Option("valid-from"), "--valid-from");
        var validUntil = ParseOptionalDateTimeOffset(commandLine.Option("valid-until"), "--valid-until");

        using var shutdown = CreateShutdownSource();
        using var api = new ManagementApiClient(server);
        var account = await api.LoginAsync(username, password, shutdown.Token).ConfigureAwait(false);
        var definition = await api.CreatePortTunnelAsync(
            name,
            protocol,
            target.Host,
            target.Port,
            shutdown.Token).ConfigureAwait(false);
        var token = await api.CreatePortTunnelTokenAsync(
            definition.Id,
            tokenType,
            validFrom,
            validUntil,
            shutdown.Token).ConfigureAwait(false);

        Console.WriteLine($"Signed in as {account.Username} ({account.Role}).");
        Console.WriteLine($"Port Tunnel created: {definition.Name}");
        Console.WriteLine($"  protocol: {definition.Protocol}");
        Console.WriteLine($"  public:   {definition.PublicPort}");
        Console.WriteLine($"  private:  {definition.PrivateHost}:{definition.PrivatePort}");
        Console.WriteLine($"  token:    {token.PlainTextToken}");

        if (!commandLine.IsFlagEnabled("run"))
        {
            return 0;
        }

        Console.WriteLine("Starting remote tunnel client with the created token.");
        return await RunRemotePortTunnelAsync(server, token.PlainTextToken).ConfigureAwait(false);
    }

    private static async Task<int> CreateVirtualNetworkAndTokenAsync(CommandLine commandLine)
    {
        var server = RequiredValue(commandLine.Option("server", "PN_SERVER"), "--server");
        var username = RequiredValue(commandLine.Option("username", "PN_USERNAME"), "--username");
        var password = commandLine.Option("password", "PN_PASSWORD") ?? ReadPassword("Password: ");
        var name = commandLine.Option("name") ?? "game-lan";
        var gatewayAddress = commandLine.Option("gateway") ?? commandLine.Option("gateway-address") ?? "10.66.0.1";
        var prefixLength = commandLine.IntOption("prefix", 24);
        var listenPort = commandLine.IntOption("listen-port", 0);
        var mtu = commandLine.IntOption("mtu", 1400);
        var transportProtocol = VirtualNetworkTransportProtocols.Normalize(
            commandLine.Option("transport") ?? Environment.GetEnvironmentVariable("PN_TRANSPORT") ?? VirtualNetworkTransportProtocols.Udp);
        var tokenType = commandLine.Option("token-type") ?? "Permanent";
        var validFrom = ParseOptionalDateTimeOffset(commandLine.Option("valid-from"), "--valid-from");
        var validUntil = ParseOptionalDateTimeOffset(commandLine.Option("valid-until"), "--valid-until");

        using var shutdown = CreateShutdownSource();
        using var api = new ManagementApiClient(server);
        var account = await api.LoginAsync(username, password, shutdown.Token).ConfigureAwait(false);
        var definition = await api.CreateVirtualNetworkAsync(
            name,
            gatewayAddress,
            prefixLength,
            listenPort,
            mtu,
            transportProtocol,
            shutdown.Token).ConfigureAwait(false);
        var token = await api.CreateVirtualNetworkTokenAsync(
            definition.Id,
            tokenType,
            validFrom,
            validUntil,
            shutdown.Token).ConfigureAwait(false);

        Console.WriteLine($"Signed in as {account.Username} ({account.Role}).");
        Console.WriteLine($"Virtual Network created: {definition.Name}");
        Console.WriteLine($"  gateway: {definition.GatewayAddress}/{definition.PrefixLength}");
        Console.WriteLine($"  protocol:{definition.TransportProtocol}");
        Console.WriteLine($"  port:    {definition.ListenPort}");
        Console.WriteLine($"  mtu:     {definition.Mtu}");
        Console.WriteLine($"  token:   {token.PlainTextToken}");

        if (!commandLine.IsFlagEnabled("run"))
        {
            return 0;
        }

        Console.WriteLine("Starting Virtual Network client with the created token.");
        return await RunVirtualNetworkTokenAsync(server, token.PlainTextToken, commandLine.Option("client-name"), IsRelayOnly(commandLine)).ConfigureAwait(false);
    }

    private static async Task<int> RunTokenConnectAsync(CommandLine commandLine, int tokenArgumentIndex)
    {
        var server = RequiredValue(commandLine.Option("server", "PN_SERVER"), "--server");
        var token = commandLine.Option("token", "PN_TOKEN") ?? commandLine.Argument(tokenArgumentIndex);
        token = RequiredValue(token, "--token").Trim();

        if (token.StartsWith("ptun_", StringComparison.OrdinalIgnoreCase))
        {
            return await RunRemotePortTunnelAsync(server, token).ConfigureAwait(false);
        }

        if (token.StartsWith("vnet_", StringComparison.OrdinalIgnoreCase))
        {
            return await RunVirtualNetworkTokenAsync(server, token, commandLine.Option("name"), IsRelayOnly(commandLine)).ConfigureAwait(false);
        }

        Console.Error.WriteLine("Unknown token prefix. Expected ptun_ for port tunnels or vnet_ for virtual networks.");
        return 2;
    }

    private static async Task<int> RunRemotePortTunnelAsync(string server, string token)
    {
        var endpoint = BuildPortTunnelWebSocketEndpoint(server);

        using var shutdown = CreateShutdownSource();
        using var client = new RemotePortTunnelClient(endpoint, token, WriteLog);
        Console.WriteLine($"ProxyNetworker remote Port Tunnel connecting to {endpoint}.");
        Console.WriteLine("Press Ctrl+C to stop.");

        await client.RunAsync(shutdown.Token).ConfigureAwait(false);
        return 0;
    }

    private static async Task<int> RunVirtualNetworkTokenAsync(string server, string token, string? name, bool relayOnly)
    {
        using var shutdown = CreateShutdownSource();
        using var runtime = await StartVirtualNetworkTokenAsync(server, token, name, relayOnly, shutdown.Token).ConfigureAwait(false);
        Console.WriteLine("Press Ctrl+C to stop.");

        await WaitUntilCancelledAsync(shutdown.Token).ConfigureAwait(false);
        return 0;
    }

    private static async Task<int> RunLocalPortTunnelAsync(CommandLine commandLine)
    {
        var protocol = ParseProtocol(commandLine.Option("protocol") ?? "tcp");
        var listen = ParseIPEndPoint(commandLine.Option("listen") ?? "127.0.0.1:8080");
        var targetValue = commandLine.Option("target");
        if (string.IsNullOrWhiteSpace(targetValue))
        {
            PrintUsage();
            return 2;
        }

        var target = ParseHostEndPoint(targetValue);
        var name = commandLine.Option("name") ?? "pn-port-tunnel";

        using var shutdown = CreateShutdownSource();
        using var tunnel = PortTunnelFactory.Create(new PortTunnelRule
        {
            Name = name,
            Protocol = protocol,
            PublicEndpoint = listen,
            PrivateService = target
        }, WriteLog);

        await tunnel.StartAsync(shutdown.Token).ConfigureAwait(false);
        Console.WriteLine($"ProxyNetworker local Port Tunnel started. protocol={protocol}, public={listen}, private={target}.");
        Console.WriteLine("Press Ctrl+C to stop.");

        await WaitUntilCancelledAsync(shutdown.Token).ConfigureAwait(false);
        return 0;
    }

    private static async Task<int> RunVirtualNetworkDirectAsync(CommandLine commandLine, int serverArgumentIndex)
    {
        var serverHost = commandLine.Option("server") ?? commandLine.Argument(serverArgumentIndex);
        if (string.IsNullOrWhiteSpace(serverHost))
        {
            PrintUsage();
            return 2;
        }

        var config = new DirectVirtualNetworkClientConfig
        {
            Name = commandLine.Option("name") ?? "pn-client",
            NodeDisplayName = commandLine.Option("name") ?? "pn-client",
            ServerHost = serverHost,
            ServerPort = commandLine.IntOption("port", commandLine.Argument(serverArgumentIndex + 1), 51820),
            BindPort = commandLine.IntOption("bind-port", 0),
            TunAddress = commandLine.Option("tun-address") ?? "10.66.0.2",
            PrefixLength = commandLine.IntOption("prefix", 24),
            Mtu = commandLine.IntOption("mtu", 1400),
            TransportProtocol = VirtualNetworkTransportProtocols.Normalize(commandLine.Option("transport") ?? Environment.GetEnvironmentVariable("PN_TRANSPORT")),
            AuthenticationKey = ParseAuthenticationKey(commandLine.Option("auth-key", "PN_AUTH_KEY")),
            RelayOnly = IsRelayOnly(commandLine)
        };

        using var shutdown = CreateShutdownSource();
        using var runtime = await StartVirtualNetworkDirectAsync(config, shutdown.Token).ConfigureAwait(false);
        Console.WriteLine("Press Ctrl+C to stop.");

        await WaitUntilCancelledAsync(shutdown.Token).ConfigureAwait(false);
        return 0;
    }

    private static async Task<int> RunConfigAsync(CommandLine commandLine)
    {
        var path = commandLine.Option("config") ?? commandLine.Argument(1) ?? ResolveDefaultConfigPath();
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"Config file was not found: {path}");
            return 2;
        }

        using var shutdown = CreateShutdownSource();
        Console.WriteLine($"Loading client config: {Path.GetFullPath(path)}");
        var config = await ProxyNetworkerClientConfig.LoadAsync(path, shutdown.Token).ConfigureAwait(false);
        var defaultServer = config.GetDefaultServer();
        var disposables = new List<IDisposable>();
        var tasks = new List<Task>();

        try
        {
            foreach (var item in config.PortTunnels.Where(static item => item.Enabled))
            {
                var server = item.Server ?? defaultServer;
                if (string.IsNullOrWhiteSpace(server) || string.IsNullOrWhiteSpace(item.Token))
                {
                    Console.Error.WriteLine($"Skipped Port Tunnel '{item.Name ?? "(unnamed)"}': server or token is empty.");
                    continue;
                }

                if (IsPlaceholderToken(item.Token))
                {
                    Console.Error.WriteLine($"Skipped Port Tunnel '{item.Name ?? "(unnamed)"}': token is still the sample placeholder.");
                    continue;
                }

                if (!item.Token.StartsWith("ptun_", StringComparison.OrdinalIgnoreCase))
                {
                    Console.Error.WriteLine($"Skipped Port Tunnel '{item.Name ?? "(unnamed)"}': token must start with ptun_.");
                    continue;
                }

                var endpoint = BuildPortTunnelWebSocketEndpoint(server);
                var client = new RemotePortTunnelClient(endpoint, item.Token, entry => WriteNamedLog(item.Name ?? item.Token[..Math.Min(item.Token.Length, 8)], entry));
                disposables.Add(client);
                tasks.Add(client.RunAsync(shutdown.Token));
                Console.WriteLine($"Started Port Tunnel client '{item.Name ?? item.Token[..Math.Min(item.Token.Length, 8)]}' -> {endpoint}.");
            }

            if (config.VirtualNetwork is { Enabled: true } virtualNetwork)
            {
                if (!string.IsNullOrWhiteSpace(virtualNetwork.Token))
                {
                    var server = virtualNetwork.Server ?? defaultServer;
                    if (string.IsNullOrWhiteSpace(server))
                    {
                        Console.Error.WriteLine("Skipped Virtual Network: server is empty.");
                    }
                    else
                    {
                        if (IsPlaceholderToken(virtualNetwork.Token))
                        {
                            Console.Error.WriteLine("Skipped Virtual Network: token is still the sample placeholder.");
                        }
                        else if (!virtualNetwork.Token.StartsWith("vnet_", StringComparison.OrdinalIgnoreCase))
                        {
                            Console.Error.WriteLine("Skipped Virtual Network: token must start with vnet_.");
                        }
                        else
                        {
                            Console.WriteLine($"Starting Virtual Network '{virtualNetwork.Name}' by token from {server}.");
                            disposables.Add(await StartVirtualNetworkTokenAsync(server, virtualNetwork.Token, virtualNetwork.Name, virtualNetwork.RelayOnly, shutdown.Token).ConfigureAwait(false));
                            tasks.Add(WaitUntilCancelledAsync(shutdown.Token));
                        }
                    }
                }
                else
                {
                    disposables.Add(await StartVirtualNetworkDirectAsync(virtualNetwork, shutdown.Token).ConfigureAwait(false));
                    tasks.Add(WaitUntilCancelledAsync(shutdown.Token));
                }
            }

            if (tasks.Count == 0)
            {
                Console.Error.WriteLine("No enabled client entries were found in the config file.");
                return 2;
            }

            Console.WriteLine("ProxyNetworker client config is running. Press Ctrl+C to stop.");
            await Task.WhenAll(tasks).ConfigureAwait(false);
            return 0;
        }
        finally
        {
            foreach (var disposable in disposables)
            {
                disposable.Dispose();
            }
        }
    }

    private static async Task<int> InitConfigAsync(CommandLine commandLine)
    {
        var path = commandLine.Option("path") ?? commandLine.Option("config") ?? commandLine.Argument(1) ?? "proxynetworker.client.json";
        if (File.Exists(path) && !commandLine.IsFlagEnabled("force"))
        {
            Console.Error.WriteLine($"Config file already exists: {path}. Use --force to overwrite.");
            return 2;
        }

        using var shutdown = CreateShutdownSource();
        await ProxyNetworkerClientConfig.WriteSampleAsync(path, shutdown.Token).ConfigureAwait(false);
        Console.WriteLine($"Created sample config: {path}");
        return 0;
    }

    private static string ResolveDefaultConfigPath()
    {
        var baseDirectory = AppContext.BaseDirectory;
        var candidates = new[]
        {
            "appsettings.json",
            "proxynetworker.client.json",
            Path.Combine(baseDirectory, "appsettings.json"),
            Path.Combine(baseDirectory, "proxynetworker.client.json")
        };

        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return Path.Combine(baseDirectory, "appsettings.json");
    }

    private static bool IsPlaceholderToken(string token)
    {
        return token.Contains("replace_this_token", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<IDisposable> StartVirtualNetworkDirectAsync(DirectVirtualNetworkClientConfig config, CancellationToken cancellationToken)
    {
        var tunAddress = IPAddress.Parse(config.TunAddress);
        var remoteAddress = await ResolveAddressAsync(config.ServerHost).ConfigureAwait(false);
        var remoteEndPoint = new IPEndPoint(remoteAddress, config.ServerPort);

        var tunDevice = TunDeviceFactory.Create(new TunDeviceOptions
        {
            Name = config.Name,
            TunnelType = "ProxyNetworker",
            Address = tunAddress,
            PrefixLength = config.PrefixLength,
            Mtu = config.Mtu
        }, WriteLog);

        try
        {
            var options = new VirtualNetworkTunnelOptions
            {
                BindEndPoint = new IPEndPoint(IPAddress.Any, config.BindPort),
                RemoteEndPoint = remoteEndPoint,
                NodeAddress = tunAddress,
                PrefixLength = config.PrefixLength,
                NodeName = string.IsNullOrWhiteSpace(config.NodeDisplayName) ? config.Name : config.NodeDisplayName,
                AuthenticationKey = config.AuthenticationKey,
                EnableDirectPaths = !config.RelayOnly
            };
            IDisposable tunnel = VirtualNetworkTransportProtocols.UsesReliableStream(config.TransportProtocol)
                ? new VirtualNetworkStreamTunnel(tunDevice, options, config.TransportProtocol, WriteLog)
                : new VirtualNetworkTunnel(tunDevice, options, WriteLog);

            if (tunnel is VirtualNetworkStreamTunnel streamTunnel)
            {
                await streamTunnel.StartAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await ((VirtualNetworkTunnel)tunnel).StartAsync(cancellationToken).ConfigureAwait(false);
            }
            Console.WriteLine($"ProxyNetworker Virtual Network client started. TUN={config.Name} {tunAddress}/{config.PrefixLength}, peer={remoteEndPoint}.");
            return tunnel;
        }
        catch
        {
            tunDevice.Dispose();
            throw;
        }
    }

    private static async Task<IDisposable> StartVirtualNetworkTokenAsync(
        string server,
        string token,
        string? name,
        bool relayOnly,
        CancellationToken cancellationToken)
    {
        using var api = new ManagementApiClient(server);
        var remoteConfig = await api.GetVirtualNetworkConfigAsync(token, cancellationToken).ConfigureAwait(false);
        var displayName = string.IsNullOrWhiteSpace(name)
            ? CreateClientTunName(remoteConfig.NetworkId)
            : name;
        var nodeName = displayName;
        if (OperatingSystem.IsLinux())
        {
            var normalizedName = LinuxInterfaceName.Normalize(nodeName);
            if (!string.Equals(normalizedName, nodeName, StringComparison.Ordinal))
            {
                Console.WriteLine($"Linux interface name '{nodeName}' is too long; using '{normalizedName}'.");
                nodeName = normalizedName;
            }
        }

        var serverHost = NormalizeHost(remoteConfig.ServerHost, server);
        byte[] authenticationKey;
        try
        {
            authenticationKey = Convert.FromBase64String(remoteConfig.AuthenticationKey);
        }
        catch (FormatException)
        {
            throw new InvalidOperationException("Virtual Network server returned an invalid authentication key.");
        }

        if (authenticationKey.Length != 32)
        {
            throw new InvalidOperationException("Virtual Network server returned an invalid authentication key length.");
        }

        Console.WriteLine($"Virtual Network token accepted. network={remoteConfig.Name}, transport={remoteConfig.TransportProtocol}, assigned={remoteConfig.ClientAddress}/{remoteConfig.PrefixLength}, server={serverHost}:{remoteConfig.ServerPort}.");

        return await StartVirtualNetworkDirectAsync(new DirectVirtualNetworkClientConfig
        {
            Name = nodeName,
            NodeDisplayName = displayName,
            ServerHost = serverHost,
            ServerPort = remoteConfig.ServerPort,
            BindPort = 0,
            TunAddress = remoteConfig.ClientAddress,
            PrefixLength = remoteConfig.PrefixLength,
            Mtu = remoteConfig.Mtu,
            AuthenticationKey = authenticationKey,
            TransportProtocol = remoteConfig.TransportProtocol,
            RelayOnly = relayOnly
        }, cancellationToken).ConfigureAwait(false);
    }

    private static string CreateClientTunName(string networkId)
    {
        return $"pc{networkId[..Math.Min(10, networkId.Length)]}";
    }

    private static byte[] ParseAuthenticationKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                "Direct Virtual Network mode requires --auth-key or PN_AUTH_KEY. Token mode obtains the key automatically.");
        }

        byte[] key;
        try
        {
            key = Convert.FromBase64String(value);
        }
        catch (FormatException)
        {
            throw new InvalidOperationException("Virtual Network authentication key must be base64-encoded.");
        }

        if (key.Length != 32)
        {
            throw new InvalidOperationException("Virtual Network authentication key must decode to 32 bytes.");
        }

        return key;
    }

    private static bool IsRelayOnly(CommandLine commandLine)
    {
        if (commandLine.IsFlagEnabled("relay-only") || commandLine.IsFlagEnabled("no-direct"))
        {
            return true;
        }

        var value = Environment.GetEnvironmentVariable("PN_RELAY_ONLY");
        return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeHost(string host, string fallbackServer)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return NormalizeServerUri(fallbackServer).Host;
        }

        if (Uri.TryCreate(host, UriKind.Absolute, out var absoluteUri))
        {
            return absoluteUri.Host;
        }

        if (host.StartsWith("[", StringComparison.Ordinal))
        {
            var end = host.IndexOf(']', StringComparison.Ordinal);
            return end > 0 ? host[1..end] : host;
        }

        var colon = host.LastIndexOf(':');
        if (colon > 0 && host.IndexOf(':') == colon && int.TryParse(host[(colon + 1)..], out _))
        {
            return host[..colon];
        }

        return host;
    }

    private static CancellationTokenSource CreateShutdownSource()
    {
        var shutdown = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            shutdown.Cancel();
        };
        return shutdown;
    }

    private static async Task WaitUntilCancelledAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static async Task<IPAddress> ResolveAddressAsync(string host)
    {
        if (IPAddress.TryParse(host, out var address))
        {
            return address;
        }

        var addresses = await Dns.GetHostAddressesAsync(host).ConfigureAwait(false);
        return addresses.First(static item => item.AddressFamily == AddressFamily.InterNetwork);
    }

    private static PortTunnelProtocol ParseProtocol(string value)
    {
        return value.ToLowerInvariant() switch
        {
            "tcp" => PortTunnelProtocol.Tcp,
            "udp" => PortTunnelProtocol.Udp,
            _ => throw new ArgumentException($"Unsupported protocol '{value}'. Use tcp or udp.")
        };
    }

    private static IPEndPoint ParseIPEndPoint(string value)
    {
        var (host, port) = SplitHostPort(value);
        if (!IPAddress.TryParse(host, out var address))
        {
            throw new ArgumentException($"Listen endpoint host must be an IP address: '{value}'.");
        }

        return new IPEndPoint(address, port);
    }

    private static HostEndPoint ParseHostEndPoint(string value)
    {
        var (host, port) = SplitHostPort(value);
        return new HostEndPoint(host, port);
    }

    private static (string Host, int Port) SplitHostPort(string value)
    {
        var index = value.LastIndexOf(':');
        if (index <= 0 || index == value.Length - 1)
        {
            throw new ArgumentException($"Endpoint must be formatted as host:port: '{value}'.");
        }

        var host = value[..index];
        var portText = value[(index + 1)..];
        if (!int.TryParse(portText, out var port) || port <= 0 || port > 65535)
        {
            throw new ArgumentException($"Endpoint port must be between 1 and 65535: '{value}'.");
        }

        return (host, port);
    }

    private static Uri BuildPortTunnelWebSocketEndpoint(string server)
    {
        var baseUri = NormalizeServerUri(server);
        var endpoint = new Uri(baseUri, "api/client/port-tunnels/connect");
        var builder = new UriBuilder(endpoint)
        {
            Scheme = baseUri.Scheme.ToLowerInvariant() switch
            {
                "https" => "wss",
                "wss" => "wss",
                "ws" => "ws",
                _ => "ws"
            }
        };

        return builder.Uri;
    }

    private static Uri NormalizeServerUri(string server)
    {
        var value = server.Contains("://", StringComparison.Ordinal)
            ? server
            : $"http://{server}";
        return new Uri(value.EndsWith("/", StringComparison.Ordinal) ? value : $"{value}/");
    }

    private static DateTimeOffset? ParseOptionalDateTimeOffset(string? value, string optionName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (DateTimeOffset.TryParse(value, out var parsed))
        {
            return parsed;
        }

        throw new ArgumentException($"{optionName} must be a valid DateTimeOffset value.");
    }

    private static string RequiredValue(string? value, string optionName)
    {
        return string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException($"{optionName} is required.")
            : value;
    }

    private static string ReadPassword(string prompt)
    {
        Console.Write(prompt);
        if (Console.IsInputRedirected)
        {
            return Console.ReadLine() ?? string.Empty;
        }

        var password = new List<char>();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                return new string(password.ToArray());
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (password.Count > 0)
                {
                    password.RemoveAt(password.Count - 1);
                    Console.Write("\b \b");
                }

                continue;
            }

            password.Add(key.KeyChar);
            Console.Write('*');
        }
    }

    private static void WriteLog(ProxyNetworkerLogEntry entry)
    {
        WriteLogEntry(Log.Logger, entry);
    }

    private static void WriteNamedLog(string name, ProxyNetworkerLogEntry entry)
    {
        WriteLogEntry(Log.ForContext("ClientEntry", name), entry);
    }

    private static void WriteLogEntry(Serilog.ILogger logger, ProxyNetworkerLogEntry entry)
    {
        logger.Write(ToSerilogLevel(entry.Level), entry.Exception, "{Message}", entry.Message);
    }

    private static LogEventLevel ToSerilogLevel(ProxyNetworkerLogLevel level)
    {
        return level switch
        {
            ProxyNetworkerLogLevel.Trace => LogEventLevel.Verbose,
            ProxyNetworkerLogLevel.Debug => LogEventLevel.Debug,
            ProxyNetworkerLogLevel.Information => LogEventLevel.Information,
            ProxyNetworkerLogLevel.Warning => LogEventLevel.Warning,
            ProxyNetworkerLogLevel.Error => LogEventLevel.Error,
            _ => LogEventLevel.Information
        };
    }

    private static void PrintUsage()
    {
        var executableName = OperatingSystem.IsWindows() ? "ProxyNetworker.Client.exe" : "ProxyNetworker.Client";
        Console.WriteLine("ProxyNetworker.Client commands:");
        Console.WriteLine($"  {executableName}");
        Console.WriteLine("    Runs appsettings.json or proxynetworker.client.json from the current directory.");
        Console.WriteLine($"  {executableName} connect --server http://server:13301 --token ptun_xxx");
        Console.WriteLine("  run --config appsettings.json");
        Console.WriteLine("  run --config proxynetworker.client.json");
        Console.WriteLine("  init-config [--path appsettings.json|proxynetworker.client.json] [--force]");
        Console.WriteLine($"  {executableName} port-tunnel create --server http://server:13301 --username sky --target 127.0.0.1:80 [--protocol tcp|udp] [--run]");
        Console.WriteLine($"  {executableName} port-tunnel --protocol tcp|udp --listen 127.0.0.1:8080 --target 127.0.0.1:80 [--name web]");
        Console.WriteLine($"  {executableName} virtual-network create --server http://server:13301 --username sky [--gateway 10.66.0.1] [--prefix 24] [--run]");
        Console.WriteLine($"  {executableName} virtual-network --server http://server:13301 --token vnet_xxx [--name pn-client] [--relay-only]");
        Console.WriteLine($"  {executableName} virtual-network --server <host> [--port 51820] [--tun-address 10.66.0.2] [--prefix 24] [--name pn-client]");
        Console.WriteLine($"  {executableName} auth change-password --server http://server:13301 --username sky [--password current] [--new-password next]");
        Console.WriteLine();
        Console.WriteLine("Environment fallbacks: PN_SERVER, PN_TOKEN, PN_USERNAME, PN_PASSWORD.");
    }
}
