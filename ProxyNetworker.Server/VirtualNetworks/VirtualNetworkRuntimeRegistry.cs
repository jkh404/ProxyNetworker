using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ProxyNetworker.Core;
using ProxyNetworker.Server.Data;
using ProxyNetworker.Server.Management;

namespace ProxyNetworker.Server.VirtualNetworks;

public sealed class VirtualNetworkRuntimeRegistry : IDisposable
{
    private readonly ConcurrentDictionary<string, ActiveVirtualNetwork> _networks = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<VirtualNetworkRuntimeRegistry> _logger;
    private readonly IPAddress _publicListenAddress;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly CancellationTokenSource _sessionTrackingCancellation = new();
    private readonly Task _sessionTrackingTask;

    public VirtualNetworkRuntimeRegistry(
        ILogger<VirtualNetworkRuntimeRegistry> logger,
        IOptions<NetworkDefaultsOptions> networkDefaults,
        IServiceScopeFactory scopeFactory)
    {
        _logger = logger;
        _publicListenAddress = IPAddress.Parse(networkDefaults.Value.PublicListenAddress);
        _scopeFactory = scopeFactory;
        _sessionTrackingTask = Task.Run(() => TrackVirtualNetworkSessionsAsync(_sessionTrackingCancellation.Token));
    }

    public IReadOnlyCollection<TunnelSummary> List()
    {
        return _networks.Values
            .Select(static item => item.Summary)
            .OrderBy(static item => item.StartedAt)
            .ToArray();
    }

    public IReadOnlyList<VirtualNetworkPeerSnapshot> GetPeerSnapshots(string networkId)
    {
        return _networks.TryGetValue(networkId, out var runtime)
            ? runtime.Relay.GetPeerSnapshots()
            : Array.Empty<VirtualNetworkPeerSnapshot>();
    }

    public async Task<TunnelSummary> EnsureRunningAsync(VirtualNetworkRecord definition, CancellationToken cancellationToken)
    {
        if (!definition.IsEnabled)
        {
            throw new InvalidOperationException("Virtual Network is disabled.");
        }
        if (_networks.TryGetValue(definition.Id, out var existing))
        {
            return existing.Summary;
        }

        var gate = _locks.GetOrAdd(definition.Id, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_networks.TryGetValue(definition.Id, out existing))
            {
                return existing.Summary;
            }

            var runtime = await StartAsync(definition, cancellationToken).ConfigureAwait(false);
            using (var scope = _scopeFactory.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ProxyNetworkerDbContext>();
                var bans = await db.VirtualNetworkNodeBans
                    .AsNoTracking()
                    .Where(item => item.NetworkId == definition.Id)
                    .Select(item => item.VirtualAddress)
                    .ToArrayAsync(cancellationToken)
                    .ConfigureAwait(false);
                foreach (var address in bans)
                {
                    if (IPAddress.TryParse(address, out var bannedAddress))
                    {
                        runtime.Relay.SetPeerBanned(bannedAddress, true);
                    }
                }
            }
            if (!_networks.TryAdd(definition.Id, runtime))
            {
                runtime.Relay.Dispose();
                return _networks[definition.Id].Summary;
            }

            return runtime.Summary;
        }
        finally
        {
            gate.Release();
        }
    }

    public bool TryGetAuthenticationKey(string networkId, out byte[] authenticationKey)
    {
        if (_networks.TryGetValue(networkId, out var runtime))
        {
            authenticationKey = runtime.AuthenticationKey.ToArray();
            return true;
        }

        authenticationKey = Array.Empty<byte>();
        return false;
    }

    public void Stop(string networkId)
    {
        if (_networks.TryRemove(networkId, out var runtime))
        {
            runtime.Relay.Dispose();
        }
    }

    public void SetPeerBanned(string networkId, IPAddress virtualAddress, bool isBanned)
    {
        if (_networks.TryGetValue(networkId, out var runtime))
        {
            runtime.Relay.SetPeerBanned(virtualAddress, isBanned);
        }
    }

    public void Dispose()
    {
        _sessionTrackingCancellation.Cancel();
        try
        {
            _sessionTrackingTask.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException ex) when (ex.InnerExceptions.All(static item => item is OperationCanceledException))
        {
        }

        foreach (var item in _networks.ToArray())
        {
            if (_networks.TryRemove(item.Key, out var runtime))
            {
                runtime.Relay.Dispose();
            }
        }

        foreach (var item in _locks.ToArray())
        {
            if (_locks.TryRemove(item.Key, out var gate))
            {
                gate.Dispose();
            }
        }

        _sessionTrackingCancellation.Dispose();
    }

    private async Task TrackVirtualNetworkSessionsAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await PersistVirtualNetworkSessionsAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Virtual Network session tracking failed.");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private async Task PersistVirtualNetworkSessionsAsync(CancellationToken cancellationToken)
    {
        var runtimes = _networks.ToArray();
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProxyNetworkerDbContext>();
        var now = DateTimeOffset.UtcNow;
        var nowUnixMs = now.ToUnixTimeMilliseconds();
        var runtimeIds = runtimes.Select(static runtime => runtime.Key).ToArray();

        var staleSessionQuery = db.VirtualNetworkSessions
            .Where(item => item.DisconnectedAtUnixMs == null);
        if (runtimeIds.Length > 0)
        {
            staleSessionQuery = staleSessionQuery.Where(item => !runtimeIds.Contains(item.NetworkId));
        }

        var staleSessions = await staleSessionQuery.ToArrayAsync(cancellationToken).ConfigureAwait(false);
        foreach (var session in staleSessions)
        {
            session.DisconnectedAtUnixMs = nowUnixMs;
        }

        if (staleSessions.Length > 0)
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        if (runtimes.Length == 0)
        {
            return;
        }

        foreach (var runtimeEntry in runtimes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var network = await db.VirtualNetworks
                .AsNoTracking()
                .FirstOrDefaultAsync(item => item.Id == runtimeEntry.Key, cancellationToken)
                .ConfigureAwait(false);
            if (network is null)
            {
                continue;
            }

            var tokens = await db.AccessTokens
                .AsNoTracking()
                .Where(item => item.ScopeKind == AccessTokenScopes.VirtualNetwork && item.ResourceId == network.Id)
                .ToArrayAsync(cancellationToken)
                .ConfigureAwait(false);
            var tokenByAddress = BuildTokenAddressMap(network, tokens);
            var peers = runtimeEntry.Value.Relay.GetPeerSnapshots();
            var onlineAddresses = peers.Select(static peer => peer.VirtualAddress.ToString()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var openSessions = await db.VirtualNetworkSessions
                .Where(item => item.NetworkId == network.Id && item.DisconnectedAtUnixMs == null)
                .ToArrayAsync(cancellationToken)
                .ConfigureAwait(false);
            var openByAddress = openSessions.ToDictionary(static item => item.VirtualAddress, StringComparer.OrdinalIgnoreCase);

            foreach (var session in openSessions.Where(session => !onlineAddresses.Contains(session.VirtualAddress)))
            {
                session.DisconnectedAtUnixMs = nowUnixMs;
            }

            foreach (var peer in peers)
            {
                var address = peer.VirtualAddress.ToString();
                tokenByAddress.TryGetValue(address, out var token);
                if (!openByAddress.TryGetValue(address, out var session))
                {
                    session = new VirtualNetworkSessionRecord
                    {
                        NetworkId = network.Id,
                        VirtualAddress = address,
                        ConnectedAtUnixMs = peer.ConnectedAt.ToUnixTimeMilliseconds()
                    };
                    db.VirtualNetworkSessions.Add(session);
                }

                session.TokenId = token?.Id ?? string.Empty;
                session.TokenPreview = token?.TokenPreview ?? string.Empty;
                session.NodeName = peer.NodeName;
                session.MachineName = peer.MachineName;
                session.OsDescription = peer.OsDescription;
                session.OsArchitecture = peer.OsArchitecture;
                session.ProcessArchitecture = peer.ProcessArchitecture;
                session.ClientVersion = peer.ClientVersion;
                session.PublicEndpoint = peer.PublicEndPoint.ToString();
                session.LocalEndpoints = string.Join(", ", peer.LocalEndPoints.Select(static endpoint => endpoint.ToString()));
                session.LastSeenAtUnixMs = peer.LastSeenAt.ToUnixTimeMilliseconds();
                session.DisconnectedAtUnixMs = null;
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static Dictionary<string, AccessTokenRecord> BuildTokenAddressMap(
        VirtualNetworkRecord network,
        IReadOnlyCollection<AccessTokenRecord> tokens)
    {
        var result = new Dictionary<string, AccessTokenRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (var token in tokens)
        {
            var address = VirtualNetworkAccessService.ResolveClientAddress(network, token.Id, tokens);
            if (address is not null)
            {
                result[address.ToString()] = token;
            }
        }

        return result;
    }

    private async Task<ActiveVirtualNetwork> StartAsync(VirtualNetworkRecord definition, CancellationToken cancellationToken)
    {
        var gatewayAddress = IPAddress.Parse(definition.GatewayAddress);
        var authenticationKey = RandomNumberGenerator.GetBytes(32);
        var options = new VirtualNetworkTunnelOptions
        {
            BindEndPoint = new IPEndPoint(_publicListenAddress, definition.ListenPort),
            NodeAddress = gatewayAddress,
            PrefixLength = definition.PrefixLength,
            NodeName = definition.Name,
            AuthenticationKey = authenticationKey
        };
        var transportProtocol = VirtualNetworkTransportProtocols.Normalize(definition.TransportProtocol);
        IVirtualNetworkRelay relay = VirtualNetworkTransportProtocols.UsesReliableStream(transportProtocol)
            ? new VirtualNetworkStreamRelay(options, transportProtocol, LogCoreEntry)
            : new VirtualNetworkRelay(options, LogCoreEntry);

        await relay.StartAsync(cancellationToken).ConfigureAwait(false);

        var summary = new TunnelSummary(
            definition.Id,
            definition.Name,
            "virtual-network",
            "udp",
            $"{_publicListenAddress}:{definition.ListenPort}",
            null,
            DateTimeOffset.UtcNow,
            "running");

        _logger.LogInformation(
            "Virtual Network {NetworkName} relay is running on UDP {PublicListenAddress}:{ListenPort} with gateway {GatewayAddress}/{PrefixLength}.",
            definition.Name,
            _publicListenAddress,
            definition.ListenPort,
            definition.GatewayAddress,
            definition.PrefixLength);

        return new ActiveVirtualNetwork(summary, relay, authenticationKey);
    }

    private void LogCoreEntry(ProxyNetworkerLogEntry entry)
    {
        var level = entry.Level switch
        {
            ProxyNetworkerLogLevel.Trace => LogLevel.Trace,
            ProxyNetworkerLogLevel.Debug => LogLevel.Debug,
            ProxyNetworkerLogLevel.Information => LogLevel.Information,
            ProxyNetworkerLogLevel.Warning => LogLevel.Warning,
            ProxyNetworkerLogLevel.Error => LogLevel.Error,
            _ => LogLevel.Information
        };

        _logger.Log(level, entry.Exception, "{Message}", entry.Message);
    }

    private sealed record ActiveVirtualNetwork(TunnelSummary Summary, IVirtualNetworkRelay Relay, byte[] AuthenticationKey);
}
