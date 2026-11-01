using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace ProxyNetworker.Core;

public sealed record VirtualNetworkNodeInfo(
    string NodeName,
    string MachineName,
    string OsDescription,
    string OsArchitecture,
    string ProcessArchitecture,
    string ClientVersion)
{
    public static VirtualNetworkNodeInfo FromEnvironment(string nodeName)
    {
        return new VirtualNetworkNodeInfo(
            Clean(nodeName),
            Clean(Environment.MachineName),
            Clean(RuntimeInformation.OSDescription),
            RuntimeInformation.OSArchitecture.ToString(),
            RuntimeInformation.ProcessArchitecture.ToString(),
            Clean(typeof(VirtualNetworkNodeInfo).Assembly.GetName().Version?.ToString() ?? "unknown"));
    }

    public byte[] Encode()
    {
        return Encoding.UTF8.GetBytes(string.Join('\t',
            Clean(NodeName),
            Clean(MachineName),
            Clean(OsDescription),
            Clean(OsArchitecture),
            Clean(ProcessArchitecture),
            Clean(ClientVersion)));
    }

    public static bool TryParse(ReadOnlySpan<byte> payload, out VirtualNetworkNodeInfo nodeInfo)
    {
        nodeInfo = default!;
        if (payload.IsEmpty || payload.Length > 2048)
        {
            return false;
        }

        var parts = Encoding.UTF8.GetString(payload).Split('\t');
        if (parts.Length != 6)
        {
            return false;
        }

        nodeInfo = new VirtualNetworkNodeInfo(
            Clean(parts[0]),
            Clean(parts[1]),
            Clean(parts[2]),
            Clean(parts[3]),
            Clean(parts[4]),
            Clean(parts[5]));
        return true;
    }

    private static string Clean(string? value)
    {
        var cleaned = (value ?? string.Empty).Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ').Trim();
        return cleaned.Length <= 256 ? cleaned : cleaned[..256];
    }
}
