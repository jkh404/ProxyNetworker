using System.Net;

namespace ProxyNetworker.Core;

public sealed record VirtualNetworkPeerSnapshot(
    IPAddress VirtualAddress,
    string NodeName,
    string MachineName,
    string OsDescription,
    string OsArchitecture,
    string ProcessArchitecture,
    string ClientVersion,
    IPEndPoint PublicEndPoint,
    IReadOnlyList<IPEndPoint> LocalEndPoints,
    long BytesSent,
    long BytesReceived,
    DateTimeOffset ConnectedAt,
    DateTimeOffset LastSeenAt);
