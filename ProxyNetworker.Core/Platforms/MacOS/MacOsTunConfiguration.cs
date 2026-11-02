using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Net;
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

        return
        [
            new("/sbin/ifconfig",
                [name, "inet", local, local, "netmask", netmask, "mtu",
                 options.Mtu.ToString(CultureInfo.InvariantCulture), "up"]),
            new("/sbin/route", ["-n", "add", "-inet", "-net", destination, "-interface", name])
        ];
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

        await stdout.ConfigureAwait(false);
        var error = await stderr.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new IOException(
                $"{command.FileName} failed with exit code {process.ExitCode}. {error.Trim()} " +
                "The macOS virtual-network client needs administrator privileges; existing routes are never replaced.");
        }
    }
}
