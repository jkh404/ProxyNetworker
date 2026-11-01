using System.Security.Cryptography;
using System.Text;

namespace ProxyNetworker.Core.Platforms.Linux;

public static class LinuxInterfaceName
{
    public const int MaxBytes = 15;

    public static string Normalize(string? requestedName)
    {
        var requested = string.IsNullOrWhiteSpace(requestedName) ? "pn-client" : requestedName.Trim();
        var sanitized = new string(requested.Select(static character =>
            character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-' or '.'
                ? character
                : '-').ToArray()).Trim('-', '.', '_');

        if (string.IsNullOrEmpty(sanitized) || sanitized is "." or "..")
        {
            sanitized = "pn-client";
        }

        if (Encoding.UTF8.GetByteCount(sanitized) <= MaxBytes)
        {
            return sanitized;
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(requested))).ToLowerInvariant();
        var prefix = sanitized[..8].TrimEnd('-', '.', '_');
        if (prefix.Length == 0)
        {
            prefix = "pn";
        }

        return $"{prefix}-{hash[..(MaxBytes - prefix.Length - 1)]}";
    }
}
