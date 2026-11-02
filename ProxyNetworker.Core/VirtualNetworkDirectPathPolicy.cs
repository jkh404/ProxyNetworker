using System.Net;
using System.Net.Sockets;

namespace ProxyNetworker.Core;

internal static class VirtualNetworkDirectPathPolicy
{
    internal const double PrivatePreferenceMilliseconds = 5;
    internal const double MinimumSwitchImprovementMilliseconds = 2;
    internal const double MinimumSwitchImprovementRatio = 0.20;

    internal static bool IsPrivateAddress(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        Span<byte> bytes = stackalloc byte[4];
        return address.TryWriteBytes(bytes, out _) &&
            (bytes[0] == 10 ||
             (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
             (bytes[0] == 192 && bytes[1] == 168));
    }

    internal static double SelectionScore(double scoreMilliseconds, bool isPrivate) =>
        scoreMilliseconds - (isPrivate ? PrivatePreferenceMilliseconds : 0);

    internal static bool ShouldSwitch(
        double currentScore,
        bool currentIsPrivate,
        double candidateScore,
        bool candidateIsPrivate)
    {
        var current = SelectionScore(currentScore, currentIsPrivate);
        var candidate = SelectionScore(candidateScore, candidateIsPrivate);
        if (candidate > current)
        {
            return false;
        }

        // A newly proven private path may replace public startup fallback immediately.
        if (candidateIsPrivate && !currentIsPrivate)
        {
            return true;
        }

        var improvement = Math.Max(
            MinimumSwitchImprovementMilliseconds, currentScore * MinimumSwitchImprovementRatio);
        return current - candidate >= improvement;
    }
}
