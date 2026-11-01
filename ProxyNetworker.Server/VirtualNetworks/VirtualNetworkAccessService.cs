using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using ProxyNetworker.Server.Data;
using ProxyNetworker.Server.Security;

namespace ProxyNetworker.Server.VirtualNetworks;

public sealed class VirtualNetworkAccessService : IDisposable
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TokenGenerator _tokenGenerator;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _addressAllocationLocks = new(StringComparer.OrdinalIgnoreCase);

    public VirtualNetworkAccessService(IServiceScopeFactory scopeFactory, TokenGenerator tokenGenerator)
    {
        _scopeFactory = scopeFactory;
        _tokenGenerator = tokenGenerator;
    }

    public async Task<VirtualNetworkAccessGrant?> ValidateVirtualNetworkTokenAsync(string token, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProxyNetworkerDbContext>();
        var tokenHash = _tokenGenerator.HashToken(token.Trim());
        var accessToken = await db.AccessTokens
            .FirstOrDefaultAsync(item => item.TokenHash == tokenHash && item.ScopeKind == AccessTokenScopes.VirtualNetwork, cancellationToken)
            .ConfigureAwait(false);

        if (accessToken is null || !IsTokenUsable(accessToken))
        {
            return null;
        }

        var virtualNetwork = await db.VirtualNetworks
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == accessToken.ResourceId, cancellationToken)
            .ConfigureAwait(false);
        if (virtualNetwork is null)
        {
            return null;
        }

        if (!virtualNetwork.IsEnabled)
        {
            return null;
        }

        var owner = await db.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == virtualNetwork.OwnerUserId, cancellationToken)
            .ConfigureAwait(false);
        if (owner is null || owner.IsDisabled)
        {
            return null;
        }

        var allocationLock = _addressAllocationLocks.GetOrAdd(virtualNetwork.Id, static _ => new SemaphoreSlim(1, 1));
        await allocationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var peerTokens = await db.AccessTokens
                .Where(item => item.ScopeKind == AccessTokenScopes.VirtualNetwork && item.ResourceId == virtualNetwork.Id)
                .OrderBy(item => item.CreatedAtUnixMs)
                .ThenBy(item => item.Id)
                .ToArrayAsync(cancellationToken)
                .ConfigureAwait(false);
            var clientAddress = ResolveClientAddress(virtualNetwork, accessToken.Id, peerTokens) ??
                AllocateClientAddress(virtualNetwork, accessToken.Id, peerTokens);
            if (clientAddress is null)
            {
                return null;
            }

            var clientAddressText = clientAddress.ToString();
            var isBanned = await db.VirtualNetworkNodeBans
                .AsNoTracking()
                .AnyAsync(item => item.NetworkId == virtualNetwork.Id &&
                    (item.VirtualAddress == clientAddressText || item.TokenId == accessToken.Id), cancellationToken)
                .ConfigureAwait(false);
            if (isBanned)
            {
                return null;
            }

            var hasChanges = false;
            if (!string.Equals(accessToken.VirtualAddress, clientAddressText, StringComparison.OrdinalIgnoreCase))
            {
                accessToken.VirtualAddress = clientAddressText;
                hasChanges = true;
            }

            if (accessToken.TokenType == AccessTokenTypes.OneTime)
            {
                accessToken.IsConsumed = true;
                hasChanges = true;
            }

            if (hasChanges)
            {
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }

            return new VirtualNetworkAccessGrant(virtualNetwork, accessToken.Id, clientAddress);
        }
        finally
        {
            allocationLock.Release();
        }
    }

    public void Dispose()
    {
        foreach (var allocationLock in _addressAllocationLocks.Values)
        {
            allocationLock.Dispose();
        }
    }

    public async Task<VirtualNetworkAddressAssignmentResult> AssignClientAddressAsync(
        string networkId,
        string tokenId,
        string requestedAddress,
        CancellationToken cancellationToken)
    {
        var allocationLock = _addressAllocationLocks.GetOrAdd(networkId, static _ => new SemaphoreSlim(1, 1));
        await allocationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ProxyNetworkerDbContext>();
            var network = await db.VirtualNetworks
                .AsNoTracking()
                .FirstOrDefaultAsync(item => item.Id == networkId, cancellationToken)
                .ConfigureAwait(false);
            if (network is null)
            {
                return new VirtualNetworkAddressAssignmentResult(false, "Virtual Network was not found.", string.Empty);
            }

            if (!IPAddress.TryParse(requestedAddress?.Trim(), out var address) ||
                address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork ||
                ParseAssignedClientAddress(network, address.ToString()) is null)
            {
                return new VirtualNetworkAddressAssignmentResult(false, "Virtual address must be usable within the network prefix and cannot be the gateway.", string.Empty);
            }

            var tokens = await db.AccessTokens
                .Where(item => item.ScopeKind == AccessTokenScopes.VirtualNetwork && item.ResourceId == networkId)
                .ToArrayAsync(cancellationToken)
                .ConfigureAwait(false);
            var token = tokens.FirstOrDefault(item => string.Equals(item.Id, tokenId, StringComparison.Ordinal));
            if (token is null)
            {
                return new VirtualNetworkAddressAssignmentResult(false, "Access Token was not found.", string.Empty);
            }

            var normalizedAddress = address.ToString();
            if (tokens.Any(item => !string.Equals(item.Id, tokenId, StringComparison.Ordinal) &&
                string.Equals(item.VirtualAddress, normalizedAddress, StringComparison.OrdinalIgnoreCase)))
            {
                return new VirtualNetworkAddressAssignmentResult(false, "Virtual address is already assigned to another Token.", string.Empty);
            }

            token.VirtualAddress = normalizedAddress;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return new VirtualNetworkAddressAssignmentResult(true, string.Empty, normalizedAddress);
        }
        finally
        {
            allocationLock.Release();
        }
    }

    private static bool IsTokenUsable(AccessTokenRecord token)
    {
        var now = DateTimeOffset.UtcNow;
        if (token.IsConsumed)
        {
            return false;
        }

        if (token.ValidFrom is not null && token.ValidFrom > now)
        {
            return false;
        }

        if (token.ValidUntil is not null && token.ValidUntil <= now)
        {
            return false;
        }

        return true;
    }

    public static IPAddress? ResolveClientAddress(
        VirtualNetworkRecord virtualNetwork,
        string currentTokenId,
        IEnumerable<AccessTokenRecord> tokens)
    {
        var token = tokens.FirstOrDefault(item => string.Equals(item.Id, currentTokenId, StringComparison.Ordinal));
        return token is null ? null : ParseAssignedClientAddress(virtualNetwork, token.VirtualAddress);
    }

    internal static void BackfillClientAddresses(
        VirtualNetworkRecord virtualNetwork,
        IReadOnlyList<AccessTokenRecord> tokens)
    {
        if (!TryGetAddressSpace(virtualNetwork, out var usableStart, out var usableEnd, out var gateway))
        {
            return;
        }

        var usableCount = (ulong)usableEnd - usableStart + 1;
        var orderedTokens = tokens
            .Where(IsTokenUsable)
            .OrderBy(static token => token.CreatedAtUnixMs)
            .ThenBy(static token => token.Id, StringComparer.Ordinal)
            .ToArray();
        var assigned = new HashSet<uint>();
        foreach (var token in orderedTokens)
        {
            var existingAddress = ParseAssignedClientAddress(virtualNetwork, token.VirtualAddress);
            if (existingAddress is not null && assigned.Add(IPv4ToUInt32(existingAddress)))
            {
                continue;
            }

            var candidate = PickAddress(
                virtualNetwork.Id,
                token.Id,
                usableStart,
                usableCount,
                gateway,
                assigned,
                orderedTokens.Length);
            if (candidate is null)
            {
                continue;
            }

            assigned.Add(candidate.Value);
            token.VirtualAddress = UInt32ToIPv4(candidate.Value).ToString();
        }
    }

    private static IPAddress? AllocateClientAddress(
        VirtualNetworkRecord virtualNetwork,
        string currentTokenId,
        IEnumerable<AccessTokenRecord> tokens)
    {
        if (!TryGetAddressSpace(virtualNetwork, out var usableStart, out var usableEnd, out var gateway))
        {
            return null;
        }

        var assigned = tokens
            .Where(item => !string.Equals(item.Id, currentTokenId, StringComparison.Ordinal))
            .Select(item => ParseAssignedClientAddress(virtualNetwork, item.VirtualAddress))
            .Where(static address => address is not null)
            .Select(static address => IPv4ToUInt32(address!))
            .ToHashSet();
        var usableCount = (ulong)usableEnd - usableStart + 1;
        var offset = StableOffset(virtualNetwork.Id, currentTokenId, usableCount);
        for (ulong attempt = 0; attempt < usableCount; attempt++)
        {
            var candidate = usableStart + (uint)((offset + attempt) % usableCount);
            if (candidate != gateway && !assigned.Contains(candidate))
            {
                return UInt32ToIPv4(candidate);
            }
        }

        return null;
    }

    private static IPAddress? ParseAssignedClientAddress(VirtualNetworkRecord virtualNetwork, string? value)
    {
        if (!IPAddress.TryParse(value, out var address) ||
            address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork ||
            !TryGetAddressSpace(virtualNetwork, out var usableStart, out var usableEnd, out var gateway))
        {
            return null;
        }

        var numeric = IPv4ToUInt32(address);
        return numeric >= usableStart && numeric <= usableEnd && numeric != gateway ? address : null;
    }

    private static bool TryGetAddressSpace(
        VirtualNetworkRecord virtualNetwork,
        out uint usableStart,
        out uint usableEnd,
        out uint gateway)
    {
        usableStart = 0;
        usableEnd = 0;
        gateway = 0;
        if (!IPAddress.TryParse(virtualNetwork.GatewayAddress, out var gatewayAddress) ||
            gatewayAddress.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            return false;
        }

        gateway = IPv4ToUInt32(gatewayAddress);
        var mask = PrefixMask(virtualNetwork.PrefixLength);
        var network = gateway & mask;
        var broadcast = network | ~mask;
        if (broadcast <= network + 1)
        {
            return false;
        }

        usableStart = network + 1;
        usableEnd = broadcast - 1;
        return gateway >= usableStart && gateway <= usableEnd;
    }

    private static uint? PickAddress(
        string networkId,
        string tokenId,
        uint usableStart,
        ulong usableCount,
        uint gateway,
        HashSet<uint> assigned,
        int tokenCount)
    {
        var offset = StableOffset(networkId, tokenId, usableCount);
        var maxAttempts = Math.Min(usableCount, (ulong)tokenCount + 2);
        for (ulong attempt = 0; attempt < maxAttempts; attempt++)
        {
            var candidate = usableStart + (uint)((offset + attempt) % usableCount);
            if (candidate == gateway || assigned.Contains(candidate))
            {
                continue;
            }

            return candidate;
        }

        return null;
    }

    private static ulong StableOffset(string networkId, string tokenId, ulong modulo)
    {
        var data = System.Text.Encoding.UTF8.GetBytes($"{networkId}:{tokenId}");
        var hash = SHA256.HashData(data);
        var value = BitConverter.ToUInt64(hash, 0);
        return modulo == 0 ? 0 : value % modulo;
    }

    private static uint PrefixMask(int prefixLength)
    {
        return prefixLength == 0 ? 0 : uint.MaxValue << (32 - prefixLength);
    }

    private static uint IPv4ToUInt32(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
    }

    private static IPAddress UInt32ToIPv4(uint value)
    {
        return new IPAddress(new[]
        {
            (byte)(value >> 24),
            (byte)(value >> 16),
            (byte)(value >> 8),
            (byte)value
        });
    }
}

public sealed record VirtualNetworkAccessGrant(
    VirtualNetworkRecord Definition,
    string TokenId,
    IPAddress ClientAddress);

public sealed record VirtualNetworkAddressAssignmentResult(
    bool Succeeded,
    string Error,
    string VirtualAddress);
