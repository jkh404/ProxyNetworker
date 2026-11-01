using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

internal sealed class ManagementApiClient : IDisposable
{
    private readonly HttpClientHandler _handler;
    private readonly HttpClient _httpClient;

    public ManagementApiClient(string server)
    {
        _handler = new HttpClientHandler
        {
            CookieContainer = new CookieContainer()
        };
        _httpClient = new HttpClient(_handler)
        {
            BaseAddress = NormalizeBaseUri(server)
        };
    }

    public async Task<AccountResponse> LoginAsync(string username, string password, CancellationToken cancellationToken)
    {
        var response = await _httpClient.PostAsJsonAsync(
            "api/auth/login",
            new LoginRequest(username, password),
            ClientJsonContext.Default.LoginRequest,
            cancellationToken).ConfigureAwait(false);

        return await ReadRequiredAsync(response, ClientJsonContext.Default.AccountResponse, cancellationToken).ConfigureAwait(false);
    }

    public async Task ChangePasswordAsync(string currentPassword, string newPassword, CancellationToken cancellationToken)
    {
        var response = await _httpClient.PostAsJsonAsync(
            "api/auth/change-password",
            new ChangePasswordRequest(currentPassword, newPassword),
            ClientJsonContext.Default.ChangePasswordRequest,
            cancellationToken).ConfigureAwait(false);

        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PortTunnelDefinitionResponse> CreatePortTunnelAsync(
        string name,
        string protocol,
        string privateHost,
        int privatePort,
        CancellationToken cancellationToken)
    {
        var response = await _httpClient.PostAsJsonAsync(
            "api/port-tunnels",
            new CreatePortTunnelDefinitionRequest(name, protocol, privateHost, privatePort),
            ClientJsonContext.Default.CreatePortTunnelDefinitionRequest,
            cancellationToken).ConfigureAwait(false);

        return await ReadRequiredAsync(response, ClientJsonContext.Default.PortTunnelDefinitionResponse, cancellationToken).ConfigureAwait(false);
    }

    public async Task<VirtualNetworkDefinitionResponse> CreateVirtualNetworkAsync(
        string name,
        string gatewayAddress,
        int prefixLength,
        int listenPort,
        int mtu,
        string transportProtocol,
        bool encryptionEnabled,
        CancellationToken cancellationToken)
    {
        var response = await _httpClient.PostAsJsonAsync(
            "api/virtual-networks",
            new CreateVirtualNetworkDefinitionRequest(name, gatewayAddress, prefixLength, listenPort, mtu, transportProtocol, encryptionEnabled),
            ClientJsonContext.Default.CreateVirtualNetworkDefinitionRequest,
            cancellationToken).ConfigureAwait(false);

        return await ReadRequiredAsync(response, ClientJsonContext.Default.VirtualNetworkDefinitionResponse, cancellationToken).ConfigureAwait(false);
    }

    public async Task<CreatedAccessTokenResponse> CreatePortTunnelTokenAsync(
        string tunnelId,
        string tokenType,
        DateTimeOffset? validFrom,
        DateTimeOffset? validUntil,
        CancellationToken cancellationToken)
    {
        var response = await _httpClient.PostAsJsonAsync(
            $"api/port-tunnels/{Uri.EscapeDataString(tunnelId)}/tokens",
            new CreateAccessTokenRequest(tokenType, validFrom, validUntil),
            ClientJsonContext.Default.CreateAccessTokenRequest,
            cancellationToken).ConfigureAwait(false);

        return await ReadRequiredAsync(response, ClientJsonContext.Default.CreatedAccessTokenResponse, cancellationToken).ConfigureAwait(false);
    }

    public async Task<CreatedAccessTokenResponse> CreateVirtualNetworkTokenAsync(
        string networkId,
        string tokenType,
        DateTimeOffset? validFrom,
        DateTimeOffset? validUntil,
        CancellationToken cancellationToken)
    {
        var response = await _httpClient.PostAsJsonAsync(
            $"api/virtual-networks/{Uri.EscapeDataString(networkId)}/tokens",
            new CreateAccessTokenRequest(tokenType, validFrom, validUntil),
            ClientJsonContext.Default.CreateAccessTokenRequest,
            cancellationToken).ConfigureAwait(false);

        return await ReadRequiredAsync(response, ClientJsonContext.Default.CreatedAccessTokenResponse, cancellationToken).ConfigureAwait(false);
    }

    public async Task<VirtualNetworkClientConfigResponse> GetVirtualNetworkConfigAsync(
        string token,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "api/client/virtual-networks/config");
        request.Headers.TryAddWithoutValidation("X-ProxyNetworker-Token", token);
        var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new InvalidOperationException(
                "Virtual Network token was rejected by the server. " +
                "Check that the configured vnet_ token was created on this server and is not expired or already consumed.");
        }

        return await ReadRequiredAsync(response, ClientJsonContext.Default.VirtualNetworkClientConfigResponse, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        _httpClient.Dispose();
        _handler.Dispose();
    }

    private static Uri NormalizeBaseUri(string server)
    {
        var value = server.Contains("://", StringComparison.Ordinal)
            ? server
            : $"http://{server}";

        return new Uri(value.EndsWith("/", StringComparison.Ordinal) ? value : $"{value}/");
    }

    private static async Task<T> ReadRequiredAsync<T>(HttpResponseMessage response, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken)
    {
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        var result = await response.Content.ReadFromJsonAsync(typeInfo, cancellationToken).ConfigureAwait(false);
        return result ?? throw new InvalidOperationException("Server returned an empty response.");
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var message = TryReadErrorMessage(body) ?? body;
        throw new InvalidOperationException($"Server returned {(int)response.StatusCode} {response.ReasonPhrase}. {message}".Trim());
    }

    private static string? TryReadErrorMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(body, ClientJsonContext.Default.ErrorResponse)?.Message;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

internal sealed record LoginRequest(string Username, string Password);

internal sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

internal sealed record ErrorResponse(string Message);

internal sealed record AccountResponse(
    string Id,
    string Username,
    string Role,
    bool IsDisabled,
    int MaxVirtualNetworks,
    int MaxPortTunnels,
    int PortRangeStart,
    int PortRangeEnd,
    long BandwidthLimitBytes,
    long MaxTrafficSpeedBytesPerSecond,
    DateTimeOffset CreatedAt,
    DateTimeOffset PasswordChangedAt);

internal sealed record CreatePortTunnelDefinitionRequest(
    string Name,
    string Protocol,
    string PrivateHost,
    int PrivatePort);

internal sealed record CreateVirtualNetworkDefinitionRequest(
    string Name,
    string GatewayAddress,
    int PrefixLength,
    int ListenPort,
    int Mtu,
    string TransportProtocol,
    bool EncryptionEnabled);

internal sealed record VirtualNetworkDefinitionResponse(
    string Id,
    string OwnerUserId,
    string Name,
    string GatewayAddress,
    int PrefixLength,
    int ListenPort,
    int Mtu,
    string TransportProtocol,
    bool? EncryptionEnabled,
    bool IsEnabled,
    DateTimeOffset CreatedAt);

internal sealed record PortTunnelDefinitionResponse(
    string Id,
    string OwnerUserId,
    string Name,
    string Protocol,
    string PrivateHost,
    int PrivatePort,
    int PublicPort,
    long BandwidthLimitBytes,
    long MaxTrafficSpeedBytesPerSecond,
    DateTimeOffset CreatedAt);

internal sealed record CreateAccessTokenRequest(
    string TokenType,
    DateTimeOffset? ValidFrom,
    DateTimeOffset? ValidUntil);

internal sealed record AccessTokenResponse(
    string Id,
    string ScopeKind,
    string ResourceId,
    string TokenPreview,
    string TokenType,
    DateTimeOffset? ValidFrom,
    DateTimeOffset? ValidUntil,
    bool IsConsumed,
    DateTimeOffset CreatedAt);

internal sealed record CreatedAccessTokenResponse(
    AccessTokenResponse Token,
    string PlainTextToken);

internal sealed record VirtualNetworkClientConfigResponse(
    string NetworkId,
    string Name,
    string ServerHost,
    int ServerPort,
    string GatewayAddress,
    string ClientAddress,
    int PrefixLength,
    int Mtu,
    string TransportProtocol,
    bool? EncryptionEnabled,
    string AuthenticationKey);
