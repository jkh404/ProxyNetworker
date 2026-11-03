using System.Text.Json;
using System.Text.Json.Nodes;
using System.Net;
using System.Net.Sockets;

namespace ProxyNetworker.Client.Runtime;

/// <summary>Validates and atomically updates the existing configuration without dropping other sections.</summary>
public sealed class ClientConfigurationStore(string path)
{
    public string Path { get; } = System.IO.Path.GetFullPath(path);

    public Task<ProxyNetworkerClientConfig> LoadAsync(CancellationToken cancellationToken = default) =>
        ProxyNetworkerClientConfig.LoadAsync(Path, cancellationToken);

    public async Task SaveAsync(ProxyNetworkerClientConfig configuration, CancellationToken cancellationToken = default)
    {
        Validate(configuration);
        JsonObject root;
        if (File.Exists(Path))
        {
            await using var original = File.OpenRead(Path);
            root = await JsonNode.ParseAsync(original, cancellationToken: cancellationToken).ConfigureAwait(false) as JsonObject
                ?? throw new InvalidDataException("Configuration must be a JSON object.");
        }
        else
        {
            root = new JsonObject { [ProxyNetworkerClientConfig.AppSettingsSectionName] = new JsonObject() };
        }

        var sectionName = root.Select(pair => pair.Key).FirstOrDefault(key =>
            key.Equals(ProxyNetworkerClientConfig.AppSettingsSectionName, StringComparison.OrdinalIgnoreCase));
        var section = sectionName is null ? root : root[sectionName] as JsonObject
            ?? throw new InvalidDataException("Client configuration section must be an object.");
        var update = JsonSerializer.SerializeToNode(configuration, ClientJsonContext.Default.ProxyNetworkerClientConfig)!.AsObject();
        Merge(section, update);

        var directory = System.IO.Path.GetDirectoryName(Path)!;
        Directory.CreateDirectory(directory);
        var temporary = System.IO.Path.Combine(directory, "." + System.IO.Path.GetFileName(Path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows())
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var stream = new FileStream(temporary, options))
            {
                await using var writer = new Utf8JsonWriter(stream, new() { Indented = true });
                root.WriteTo(writer);
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, Path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private static void Merge(JsonObject target, JsonObject update)
    {
        foreach (var pair in update)
        {
            var oldName = target.Select(item => item.Key).FirstOrDefault(key => key.Equals(pair.Key, StringComparison.OrdinalIgnoreCase));
            var name = oldName ?? pair.Key;
            if (target[name] is JsonObject original && pair.Value is JsonObject incoming) Merge(original, incoming);
            else target[name] = pair.Value?.DeepClone();
        }
    }

    public static void Validate(ProxyNetworkerClientConfig configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        foreach (var tunnel in configuration.PortTunnels.Where(item => item.Enabled))
        {
            ValidateServer(tunnel.Server ?? configuration.GetDefaultServer());
            ValidateToken(tunnel.Token, "ptun_");
        }
        if (configuration.VirtualNetwork is not { Enabled: true } network)
            return;
        if (!string.IsNullOrWhiteSpace(network.Token))
        {
            ValidateServer(network.Server ?? configuration.GetDefaultServer());
            ValidateToken(network.Token, "vnet_");
        }
        else
        {
            if (string.IsNullOrWhiteSpace(network.ServerHost) || network.ServerPort is < 1 or > 65535 ||
                network.AuthenticationKey.Length != 32 ||
                !IPAddress.TryParse(network.TunAddress, out var address) || address.AddressFamily != AddressFamily.InterNetwork)
                throw new ArgumentException("Enter a valid Access Token, or a complete direct-mode configuration.");
        }
        if (network.PrefixLength is < 1 or > 32 || network.Mtu is < 576 or > 65535 ||
            network.BindPort is < 0 or > 65535 || string.IsNullOrWhiteSpace(network.Name))
            throw new ArgumentException("The virtual network name, prefix, MTU or bind port is invalid.");
        if (network.TransportProtocol is not ("udp" or "quic"))
            throw new ArgumentException("Virtual network transport must be udp or quic.");
    }

    public static Uri ValidateServer(string? server)
    {
        if (string.IsNullOrWhiteSpace(server))
            throw new ArgumentException("Management server is required.");
        var value = server.Contains("://", StringComparison.Ordinal) ? server : "http://" + server;
        if (!Uri.TryCreate(value.TrimEnd('/') + "/", UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("Enter an HTTP(S) management server without embedded credentials.");
        return uri;
    }

    private static void ValidateToken(string? token, string prefix)
    {
        if (string.IsNullOrWhiteSpace(token) || !token.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            token.Contains("replace_this_token", StringComparison.OrdinalIgnoreCase) || token.Any(char.IsWhiteSpace))
            throw new ArgumentException($"A valid {prefix} Access Token is required.");
    }
}
