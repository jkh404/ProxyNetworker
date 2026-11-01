using System.Buffers.Binary;
using System.Net;

namespace ProxyNetworker.Core;

internal sealed record VirtualNetworkMeshLink(
    IPAddress VirtualAddress,
    int CostMilliseconds,
    int RttMilliseconds,
    int JitterMilliseconds,
    int LossBasisPoints)
{
    public static VirtualNetworkMeshLink Create(
        IPAddress virtualAddress,
        int rttMilliseconds,
        int jitterMilliseconds,
        int lossBasisPoints)
    {
        var normalizedRtt = Math.Clamp(rttMilliseconds, 1, 60_000);
        var normalizedJitter = Math.Clamp(jitterMilliseconds, 0, 10_000);
        var normalizedLoss = Math.Clamp(lossBasisPoints, 0, 10_000);
        return new VirtualNetworkMeshLink(
            virtualAddress,
            CalculateCost(normalizedRtt, normalizedJitter, normalizedLoss),
            normalizedRtt,
            normalizedJitter,
            normalizedLoss);
    }

    public static int CalculateCost(int rttMilliseconds, int jitterMilliseconds, int lossBasisPoints)
    {
        var lossPenalty = (int)Math.Round(lossBasisPoints / 50d, MidpointRounding.AwayFromZero);
        return Math.Clamp(rttMilliseconds + (jitterMilliseconds * 2) + lossPenalty + 2, 1, 120_000);
    }
}

internal sealed class VirtualNetworkMeshAdvertisement
{
    private const byte CurrentVersion = 1;
    private const int MaxLinks = 64;
    private const int HeaderSize = 10;
    private const int LinkSize = 16;

    public VirtualNetworkMeshAdvertisement(
        long sequence,
        IReadOnlyCollection<VirtualNetworkMeshLink> links,
        DateTimeOffset receivedAt)
    {
        Sequence = sequence;
        Links = links.ToArray();
        ReceivedAt = receivedAt;
    }

    public long Sequence { get; }

    public IReadOnlyList<VirtualNetworkMeshLink> Links { get; }

    public DateTimeOffset ReceivedAt { get; }

    public byte[] Encode()
    {
        var normalized = Links
            .Where(static link => link.VirtualAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            .DistinctBy(static link => link.VirtualAddress.ToString(), StringComparer.OrdinalIgnoreCase)
            .Take(MaxLinks)
            .ToArray();

        var payload = new byte[HeaderSize + normalized.Length * LinkSize];
        payload[0] = CurrentVersion;
        BinaryPrimitives.WriteInt64BigEndian(payload.AsSpan(1, 8), Sequence);
        payload[9] = (byte)normalized.Length;

        var offset = HeaderSize;
        foreach (var link in normalized)
        {
            link.VirtualAddress.GetAddressBytes().CopyTo(payload.AsSpan(offset, 4));
            BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(offset + 4, 4), link.RttMilliseconds);
            BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(offset + 8, 4), link.JitterMilliseconds);
            BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(offset + 12, 2), checked((ushort)link.LossBasisPoints));
            offset += LinkSize;
        }

        return payload;
    }

    public static bool TryDecode(
        ReadOnlySpan<byte> payload,
        out VirtualNetworkMeshAdvertisement advertisement)
    {
        advertisement = null!;
        if (payload.Length < HeaderSize || payload[0] != CurrentVersion)
        {
            return false;
        }

        var sequence = BinaryPrimitives.ReadInt64BigEndian(payload.Slice(1, 8));
        var count = payload[9];
        if (count > MaxLinks || payload.Length != HeaderSize + count * LinkSize)
        {
            return false;
        }

        var links = new VirtualNetworkMeshLink[count];
        var offset = HeaderSize;
        for (var index = 0; index < count; index++)
        {
            var address = new IPAddress(payload.Slice(offset, 4));
            var rtt = BinaryPrimitives.ReadInt32BigEndian(payload.Slice(offset + 4, 4));
            var jitter = BinaryPrimitives.ReadInt32BigEndian(payload.Slice(offset + 8, 4));
            var loss = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(offset + 12, 2));
            links[index] = VirtualNetworkMeshLink.Create(address, rtt, jitter, loss);
            offset += LinkSize;
        }

        advertisement = new VirtualNetworkMeshAdvertisement(sequence, links, DateTimeOffset.UtcNow);
        return true;
    }
}

internal sealed record VirtualNetworkMeshTraffic(
    IPAddress VirtualAddress,
    long BytesSent,
    long BytesReceived);

internal sealed class VirtualNetworkMeshTrafficReport
{
    private const byte CurrentVersion = 1;
    private const int MaxEntries = 64;
    private const int HeaderSize = 10;
    private const int EntrySize = 20;

    public VirtualNetworkMeshTrafficReport(long sequence, IReadOnlyCollection<VirtualNetworkMeshTraffic> links)
    {
        Sequence = sequence;
        Links = links.ToArray();
    }

    public long Sequence { get; }

    public IReadOnlyList<VirtualNetworkMeshTraffic> Links { get; }

    public byte[] Encode()
    {
        var normalized = Links
            .Where(static link => link.VirtualAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            .DistinctBy(static link => link.VirtualAddress.ToString(), StringComparer.OrdinalIgnoreCase)
            .Take(MaxEntries)
            .ToArray();

        var payload = new byte[HeaderSize + normalized.Length * EntrySize];
        payload[0] = CurrentVersion;
        BinaryPrimitives.WriteInt64BigEndian(payload.AsSpan(1, 8), Sequence);
        payload[9] = (byte)normalized.Length;

        var offset = HeaderSize;
        foreach (var link in normalized)
        {
            link.VirtualAddress.GetAddressBytes().CopyTo(payload.AsSpan(offset, 4));
            BinaryPrimitives.WriteInt64BigEndian(payload.AsSpan(offset + 4, 8), Math.Max(0, link.BytesSent));
            BinaryPrimitives.WriteInt64BigEndian(payload.AsSpan(offset + 12, 8), Math.Max(0, link.BytesReceived));
            offset += EntrySize;
        }

        return payload;
    }

    public static bool TryDecode(
        ReadOnlySpan<byte> payload,
        out VirtualNetworkMeshTrafficReport report)
    {
        report = null!;
        if (payload.Length < HeaderSize || payload[0] != CurrentVersion)
        {
            return false;
        }

        var sequence = BinaryPrimitives.ReadInt64BigEndian(payload.Slice(1, 8));
        var count = payload[9];
        if (count > MaxEntries || payload.Length != HeaderSize + count * EntrySize)
        {
            return false;
        }

        var links = new VirtualNetworkMeshTraffic[count];
        var offset = HeaderSize;
        for (var index = 0; index < count; index++)
        {
            var address = new IPAddress(payload.Slice(offset, 4));
            var bytesSent = BinaryPrimitives.ReadInt64BigEndian(payload.Slice(offset + 4, 8));
            var bytesReceived = BinaryPrimitives.ReadInt64BigEndian(payload.Slice(offset + 12, 8));
            links[index] = new VirtualNetworkMeshTraffic(
                address,
                Math.Max(0, bytesSent),
                Math.Max(0, bytesReceived));
            offset += EntrySize;
        }

        report = new VirtualNetworkMeshTrafficReport(sequence, links);
        return true;
    }
}

internal readonly struct VirtualNetworkMeshPacket
{
    private const byte CurrentVersion = 1;
    private const int HeaderSize = 8;
    public const byte DefaultHopLimit = 8;
    public const byte MaximumHopLimit = 16;

    private VirtualNetworkMeshPacket(
        byte hopCount,
        byte hopLimit,
        IPAddress destination,
        ReadOnlyMemory<byte> payload)
    {
        HopCount = hopCount;
        HopLimit = hopLimit;
        Destination = destination;
        Payload = payload;
    }

    public byte HopCount { get; }

    public byte HopLimit { get; }

    public IPAddress Destination { get; }

    public ReadOnlyMemory<byte> Payload { get; }

    public static byte[] Create(IPAddress destination, ReadOnlySpan<byte> packet, byte hopLimit = DefaultHopLimit)
    {
        var addressBytes = destination.GetAddressBytes();
        if (addressBytes.Length != 4)
        {
            throw new PlatformNotSupportedException("Virtual Network mesh packets currently support IPv4 only.");
        }

        if (hopLimit is 0 or > MaximumHopLimit)
        {
            throw new ArgumentOutOfRangeException(nameof(hopLimit));
        }

        var payload = new byte[HeaderSize + packet.Length];
        payload[0] = CurrentVersion;
        payload[1] = 0;
        payload[2] = hopLimit;
        addressBytes.CopyTo(payload.AsSpan(4, 4));
        packet.CopyTo(payload.AsSpan(HeaderSize));
        return payload;
    }

    public static bool TryParse(ReadOnlySpan<byte> payload, out VirtualNetworkMeshPacket packet)
    {
        packet = default;
        if (payload.Length <= HeaderSize || payload[0] != CurrentVersion)
        {
            return false;
        }

        var hopCount = payload[1];
        var hopLimit = payload[2];
        if (hopLimit is 0 or > MaximumHopLimit || hopCount >= hopLimit)
        {
            return false;
        }

        var destination = new IPAddress(payload.Slice(4, 4));
        packet = new VirtualNetworkMeshPacket(
            hopCount,
            hopLimit,
            destination,
            payload.Slice(HeaderSize).ToArray());
        return true;
    }

    public bool TryCreateForwarded(out byte[] forwardedPayload)
    {
        forwardedPayload = Array.Empty<byte>();
        if (HopCount + 1 >= HopLimit)
        {
            return false;
        }

        forwardedPayload = new byte[HeaderSize + Payload.Length];
        forwardedPayload[0] = CurrentVersion;
        forwardedPayload[1] = (byte)(HopCount + 1);
        forwardedPayload[2] = HopLimit;
        Destination.GetAddressBytes().CopyTo(forwardedPayload.AsSpan(4, 4));
        Payload.Span.CopyTo(forwardedPayload.AsSpan(HeaderSize));
        return true;
    }
}

internal sealed record VirtualNetworkMeshRoute(
    IPAddress Destination,
    IPAddress NextHop,
    int CostMilliseconds,
    int HopCount,
    string Path);

internal sealed class VirtualNetworkMeshRouteTable
{
    private const byte DefaultMaxHops = 8;
    private readonly Dictionary<string, VirtualNetworkMeshRoute> _routes;

    private VirtualNetworkMeshRouteTable(Dictionary<string, VirtualNetworkMeshRoute> routes)
    {
        _routes = routes;
    }

    public static VirtualNetworkMeshRouteTable Empty { get; } = new(new Dictionary<string, VirtualNetworkMeshRoute>(StringComparer.OrdinalIgnoreCase));

    public bool TryGetRoute(IPAddress destination, out VirtualNetworkMeshRoute route)
    {
        return _routes.TryGetValue(destination.ToString(), out route!);
    }

    public static VirtualNetworkMeshRouteTable Build(
        IPAddress localAddress,
        IReadOnlyCollection<VirtualNetworkMeshLink> localLinks,
        IReadOnlyDictionary<string, (IPAddress Origin, VirtualNetworkMeshAdvertisement Advertisement)> advertisements,
        DateTimeOffset now,
        TimeSpan maximumAdvertisementAge,
        byte maxHops = DefaultMaxHops)
    {
        var localKey = localAddress.ToString();
        var graph = new Dictionary<string, List<GraphEdge>>(StringComparer.OrdinalIgnoreCase);

        foreach (var link in localLinks)
        {
            AddEdge(graph, localAddress, link);
        }

        foreach (var item in advertisements.Values)
        {
            if (now - item.Advertisement.ReceivedAt > maximumAdvertisementAge)
            {
                continue;
            }

            foreach (var link in item.Advertisement.Links)
            {
                AddEdge(graph, item.Origin, link);
            }
        }

        var distances = new Dictionary<string, RouteState>(StringComparer.OrdinalIgnoreCase)
        {
            [localKey] = new RouteState(0, 0, null, null)
        };
        var queue = new PriorityQueue<string, (int Cost, int Hops)>();
        queue.Enqueue(localKey, (0, 0));

        while (queue.TryDequeue(out var current, out var priority))
        {
            if (!distances.TryGetValue(current, out var currentState) ||
                currentState.CostMilliseconds != priority.Cost ||
                currentState.HopCount != priority.Hops)
            {
                continue;
            }

            if (!graph.TryGetValue(current, out var edges))
            {
                continue;
            }

            foreach (var edge in edges)
            {
                var hops = currentState.HopCount + 1;
                if (hops > maxHops)
                {
                    continue;
                }

                var cost = currentState.CostMilliseconds + edge.Link.CostMilliseconds;
                if (distances.TryGetValue(edge.ToKey, out var existing) &&
                    (existing.CostMilliseconds < cost || (existing.CostMilliseconds == cost && existing.HopCount <= hops)))
                {
                    continue;
                }

                distances[edge.ToKey] = new RouteState(cost, hops, current, edge.Link);
                queue.Enqueue(edge.ToKey, (cost, hops));
            }
        }

        var routes = new Dictionary<string, VirtualNetworkMeshRoute>(StringComparer.OrdinalIgnoreCase);
        foreach (var destination in distances.Keys)
        {
            if (string.Equals(destination, localKey, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var path = new List<string>();
            var cursor = destination;
            while (!string.Equals(cursor, localKey, StringComparison.OrdinalIgnoreCase))
            {
                path.Add(cursor);
                if (!distances.TryGetValue(cursor, out var state) || state.PreviousKey is null)
                {
                    path.Clear();
                    break;
                }
                cursor = state.PreviousKey;
            }

            if (path.Count == 0)
            {
                continue;
            }

            path.Add(localKey);
            path.Reverse();
            var destinationAddress = IPAddress.Parse(destination);
            var nextHop = IPAddress.Parse(path[1]);
            var finalState = distances[destination];
            routes[destination] = new VirtualNetworkMeshRoute(
                destinationAddress,
                nextHop,
                finalState.CostMilliseconds,
                finalState.HopCount,
                string.Join(" -> ", path));
        }

        return new VirtualNetworkMeshRouteTable(routes);
    }

    private static void AddEdge(
        Dictionary<string, List<GraphEdge>> graph,
        IPAddress from,
        VirtualNetworkMeshLink link)
    {
        if (from.Equals(link.VirtualAddress))
        {
            return;
        }

        var fromKey = from.ToString();
        if (!graph.TryGetValue(fromKey, out var edges))
        {
            edges = new List<GraphEdge>();
            graph[fromKey] = edges;
        }

        edges.Add(new GraphEdge(link.VirtualAddress.ToString(), link));
    }

    private sealed record GraphEdge(string ToKey, VirtualNetworkMeshLink Link);

    private sealed record RouteState(
        int CostMilliseconds,
        int HopCount,
        string? PreviousKey,
        VirtualNetworkMeshLink? PreviousLink);
}
