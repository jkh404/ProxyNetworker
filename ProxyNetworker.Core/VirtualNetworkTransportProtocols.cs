namespace ProxyNetworker.Core;

public static class VirtualNetworkTransportProtocols
{
    public const string Udp = "udp";
    public const string Quic = "quic";

    public static string Normalize(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            Quic => Quic,
            _ => Udp
        };
    }

    public static bool IsSupported(string? value)
    {
        return value is not null && value.Trim().ToLowerInvariant() is Udp or Quic;
    }

    public static bool UsesReliableStream(string value)
    {
        return Normalize(value) is Quic;
    }
}
