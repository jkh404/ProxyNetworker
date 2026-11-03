namespace ProxyNetworker.Core;

/// <summary>A read-only view of a known peer and its last selected forwarding path.</summary>
public sealed record VirtualNetworkClientPeerSnapshot(string Address, string Endpoint, string Path, double? RttMilliseconds);
