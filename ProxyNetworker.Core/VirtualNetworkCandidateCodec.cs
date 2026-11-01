using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace ProxyNetworker.Core;

internal static class VirtualNetworkCandidateCodec
{
    private const int MaxEndpoints = 16;
    private const int EndpointSize = 6;

    public static byte[] Encode(IEnumerable<IPEndPoint> endpoints)
    {
        var normalized = endpoints
            .Where(static endpoint => endpoint.AddressFamily == AddressFamily.InterNetwork && endpoint.Port is > 0 and <= ushort.MaxValue)
            .DistinctBy(static endpoint => endpoint.ToString(), StringComparer.OrdinalIgnoreCase)
            .Take(MaxEndpoints)
            .ToArray();

        var payload = new byte[1 + normalized.Length * EndpointSize];
        payload[0] = (byte)normalized.Length;
        var offset = 1;
        foreach (var endpoint in normalized)
        {
            endpoint.Address.GetAddressBytes().CopyTo(payload.AsSpan(offset, 4));
            BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(offset + 4, 2), (ushort)endpoint.Port);
            offset += EndpointSize;
        }

        return payload;
    }

    public static bool TryDecode(ReadOnlySpan<byte> payload, out IPEndPoint[] endpoints)
    {
        endpoints = Array.Empty<IPEndPoint>();
        if (payload.Length == 0 || payload[0] > MaxEndpoints || payload.Length != 1 + payload[0] * EndpointSize)
        {
            return false;
        }

        var decoded = new IPEndPoint[payload[0]];
        var offset = 1;
        for (var index = 0; index < decoded.Length; index++)
        {
            var address = new IPAddress(payload.Slice(offset, 4));
            var port = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(offset + 4, 2));
            if (port == 0)
            {
                return false;
            }

            decoded[index] = new IPEndPoint(address, port);
            offset += EndpointSize;
        }

        endpoints = decoded;
        return true;
    }
}
