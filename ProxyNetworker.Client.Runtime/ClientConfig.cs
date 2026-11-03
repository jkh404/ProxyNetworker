using System.Text.Json;

namespace ProxyNetworker.Client.Runtime;

/// <summary>Configuration shared by the console, desktop and Android clients.</summary>
public sealed class ProxyNetworkerClientConfig
{
    public const string AppSettingsSectionName = "ProxyNetworkerClient";

    public string? ApiServer { get; set; }

    public string? Server { get; set; }

    public List<RemotePortTunnelClientConfig> PortTunnels { get; set; } = [];

    public DirectVirtualNetworkClientConfig? VirtualNetwork { get; set; }

    public static async Task<ProxyNetworkerClientConfig> LoadAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return await LoadAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<ProxyNetworkerClientConfig> LoadAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;

        if (root.ValueKind != JsonValueKind.Object)
        {
            return new ProxyNetworkerClientConfig();
        }

        if (TryGetPropertyIgnoreCase(root, AppSettingsSectionName, out var appSettingsSection))
        {
            return DeserializeConfig(appSettingsSection);
        }

        return DeserializeConfig(root);
    }

    public static async Task WriteSampleAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.Create(path);
        await using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        writer.WriteStartObject();
        if (IsAppSettingsPath(path))
        {
            writer.WritePropertyName(AppSettingsSectionName);
            WriteClientConfigSection(writer);
            writer.WritePropertyName("Serilog");
            WriteSerilogSection(writer);
        }
        else
        {
            WriteClientConfigProperties(writer);
            writer.WritePropertyName("Serilog");
            WriteSerilogSection(writer);
        }

        writer.WriteEndObject();
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync("\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
    }

    public string? GetDefaultServer()
    {
        return ApiServer ?? Server;
    }

    private static ProxyNetworkerClientConfig DeserializeConfig(JsonElement element)
    {
        return element.Deserialize(ClientJsonContext.Default.ProxyNetworkerClientConfig) ?? new ProxyNetworkerClientConfig();
    }

    private static void WriteClientConfigSection(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        WriteClientConfigProperties(writer);
        writer.WriteEndObject();
    }

    private static void WriteClientConfigProperties(Utf8JsonWriter writer)
    {
        writer.WriteString("apiServer", "http://127.0.0.1:13301");
        writer.WritePropertyName("portTunnels");
        writer.WriteStartArray();
        writer.WriteStartObject();
        writer.WriteBoolean("enabled", false);
        writer.WriteString("name", "web");
        writer.WriteString("token", "ptun_replace_this_token");
        writer.WriteEndObject();
        writer.WriteEndArray();
        writer.WritePropertyName("virtualNetwork");
        writer.WriteStartObject();
        writer.WriteBoolean("enabled", false);
        writer.WriteString("name", "pn-client");
        writer.WriteString("token", "vnet_replace_this_token");
        writer.WriteBoolean("stableDirectPathSelection", true);
        writer.WriteEndObject();
    }

    private static void WriteSerilogSection(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("MinimumLevel");
        writer.WriteStartObject();
        writer.WriteString("Default", "Information");
        writer.WritePropertyName("Override");
        writer.WriteStartObject();
        writer.WriteString("Microsoft", "Warning");
        writer.WriteString("System", "Warning");
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static bool IsAppSettingsPath(string path)
    {
        var fileName = Path.GetFileName(path);
        return fileName.Equals("appsettings.json", StringComparison.OrdinalIgnoreCase) ||
            (fileName.StartsWith("appsettings.", StringComparison.OrdinalIgnoreCase) &&
             fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement element, string propertyName, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (property.NameEquals(propertyName) || string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }
}

/// <summary>A token-authenticated remote port tunnel.</summary>
public sealed class RemotePortTunnelClientConfig
{
    public bool Enabled { get; set; } = true;

    public string? Name { get; set; }

    public string? Server { get; set; }

    public string Token { get; set; } = string.Empty;
}

/// <summary>A token-authenticated or explicitly configured virtual network.</summary>
public sealed class DirectVirtualNetworkClientConfig
{
    public bool Enabled { get; set; }

    public string? Server { get; set; }

    public string? Token { get; set; }

    public string Name { get; set; } = "pn-client";

    public string? NodeDisplayName { get; set; }

    public string ServerHost { get; set; } = "127.0.0.1";

    public int ServerPort { get; set; } = 51820;

    public int BindPort { get; set; }

    public string TunAddress { get; set; } = "10.66.0.2";

    public int PrefixLength { get; set; } = 24;

    public int Mtu { get; set; } = 1400;

    public string TransportProtocol { get; set; } = "udp";

    public bool EncryptionEnabled { get; set; } = true;

    public byte[] AuthenticationKey { get; set; } = Array.Empty<byte>();

    public bool RelayOnly { get; set; }

    public bool StableDirectPathSelection { get; set; } = true;
}
