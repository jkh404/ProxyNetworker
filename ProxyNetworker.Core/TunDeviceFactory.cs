using System.Runtime.InteropServices;
using ProxyNetworker.Core.Platforms.Linux;
using ProxyNetworker.Core.Platforms.MacOS;
using ProxyNetworker.Core.Platforms.Windows;

namespace ProxyNetworker.Core;

public static class TunDeviceFactory
{
    public static ITunDevice Create(TunDeviceOptions options, Action<ProxyNetworkerLogEntry>? logger = null)
    {
        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        options.Validate();

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return new WintunTunDevice(options, logger);
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return new LinuxTunDevice(options, logger);
        }

        if (OperatingSystem.IsMacOS())
        {
            return new MacOsTunDevice(options, logger);
        }

        throw new PlatformNotSupportedException("ProxyNetworker.Core supports TUN devices on Windows, Linux and macOS.");
    }
}
