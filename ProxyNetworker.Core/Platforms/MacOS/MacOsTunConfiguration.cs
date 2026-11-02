using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace ProxyNetworker.Core.Platforms.MacOS;

internal sealed record MacOsTunCommand(string FileName, string[] Arguments);

internal static class MacOsTunConfiguration
{
    internal static void Validate(TunDeviceOptions options)
    {
        options.Validate();
        if (options.Address.AddressFamily != AddressFamily.InterNetwork)
        {
            throw new PlatformNotSupportedException("The macOS utun backend supports IPv4 only.");
        }

        if (options.ConfigureInterface && options.PrefixLength == 0)
        {
            throw new ArgumentException("The macOS backend does not install a default route. Use a virtual-network prefix between 1 and 32.", nameof(options));
        }
    }

    internal static bool IsInterfaceName(string name) =>
        name.Length is > 4 and < 16 && name.StartsWith("utun", StringComparison.Ordinal) &&
        name.AsSpan(4).IndexOfAnyExceptInRange('0', '9') < 0;

    internal static void RejectConflictingInterfaces(string name, TunDeviceOptions options)
    {
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.Name == name)
            {
                continue;
            }

            foreach (var address in adapter.GetIPProperties().UnicastAddresses)
            {
                if (address.Address.AddressFamily == AddressFamily.InterNetwork &&
                    NetworksOverlap(options.Address, options.PrefixLength, address.Address, address.PrefixLength))
                {
                    throw new IOException(
                        $"The virtual subnet conflicts with interface '{adapter.Name}' ({address.Address}/{address.PrefixLength}). " +
                        "Existing interfaces and routes have not been changed.");
                }
            }
        }
    }

    internal static bool NetworksOverlap(IPAddress first, int firstPrefix, IPAddress second, int secondPrefix)
    {
        Span<byte> bytes = stackalloc byte[4];
        first.TryWriteBytes(bytes, out _);
        var firstAddress = BinaryPrimitives.ReadUInt32BigEndian(bytes);
        second.TryWriteBytes(bytes, out _);
        var secondAddress = BinaryPrimitives.ReadUInt32BigEndian(bytes);
        var prefix = Math.Min(firstPrefix, secondPrefix);
        var mask = prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);
        return (firstAddress & mask) == (secondAddress & mask);
    }

    internal static MacOsTunCommand[] CreateCommands(string name, TunDeviceOptions options)
    {
        Validate(options);
        if (!IsInterfaceName(name))
        {
            throw new ArgumentException("The kernel must return a valid utun interface name.", nameof(name));
        }

        Span<byte> address = stackalloc byte[4];
        options.Address.TryWriteBytes(address, out _);
        var mask = options.PrefixLength == 0 ? 0u : uint.MaxValue << (32 - options.PrefixLength);
        var network = BinaryPrimitives.ReadUInt32BigEndian(address) & mask;
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, mask);
        var netmask = new IPAddress(bytes).ToString();
        BinaryPrimitives.WriteUInt32BigEndian(bytes, network);
        var destination = $"{new IPAddress(bytes)}/{options.PrefixLength}";
        var local = options.Address.ToString();

        var configure = new MacOsTunCommand("/sbin/ifconfig",
            [name, "inet", local, local, "netmask", netmask, "mtu",
             options.Mtu.ToString(CultureInfo.InvariantCulture), "up"]);
        // ifconfig already owns the local host route; /32 has no additional subnet.
        return options.PrefixLength == 32
            ? [configure]
            : [configure, new("/sbin/route", ["-n", "add", "-inet", "-net", destination, "-interface", name])];
    }

    internal static async Task RunCommandAsync(MacOsTunCommand command, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(command.FileName)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        foreach (var argument in command.Arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }
        process.StartInfo.Environment["LC_ALL"] = "C";

        cancellationToken.ThrowIfCancellationRequested();
        if (!process.Start())
        {
            throw new IOException($"Could not start {command.FileName}.");
        }

        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
            }

            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException($"Timed out configuring the macOS interface with {command.FileName}.");
        }

        var output = await stdout.ConfigureAwait(false);
        var error = await stderr.ConfigureAwait(false);
        EnsureSucceeded(command, process.ExitCode, output, error);
    }

    internal static void EnsureSucceeded(MacOsTunCommand command, int exitCode, string output, string error)
    {
        // Darwin route can print a routing-socket error and still exit zero.
        var routeSucceeded = command.FileName != "/sbin/route" ||
            (command.Arguments is ["-n", "add", "-inet", "-net", var destination, "-interface", var name] &&
             output.Trim().Equals($"add net {destination.Split('/')[0]}: gateway {name}", StringComparison.Ordinal));
        if (exitCode != 0 || !string.IsNullOrWhiteSpace(error) || !routeSucceeded)
        {
            throw new IOException(
                $"{command.FileName} failed with exit code {exitCode}. {output.Trim()} {error.Trim()} " +
                "The macOS virtual-network client needs administrator privileges; existing routes are never replaced.");
        }
    }
}
