using System.Text;
using System.Text.Json.Nodes;
using ProxyNetworker.Client.Runtime;

namespace ProxyNetworker.Client.Runtime.Tests;

public sealed class ClientConfigurationTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ConfigurationsRetainSectionsAndUnknownNetworkFields(bool wrapped)
    {
        var directory = Directory.CreateTempSubdirectory("pn-config-test-");
        var path = Path.Combine(directory.FullName, "appsettings.json");
        try
        {
            var client = """{"apiServer":"http://127.0.0.1:13301","virtualNetwork":{"enabled":false,"name":"old","futureOption":42}}""";
            var text = wrapped ? """{"Serilog":{"MinimumLevel":"Warning"},"ProxyNetworkerClient":""" + client + "}" : client;
            await File.WriteAllTextAsync(path, text);
            var store = new ClientConfigurationStore(path);
            var configuration = await store.LoadAsync();
            configuration.VirtualNetwork!.Name = "new";
            await store.SaveAsync(configuration);
            var saved = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
            var section = wrapped ? saved["ProxyNetworkerClient"]! : saved;
            Assert.Equal(42, section["virtualNetwork"]!["futureOption"]!.GetValue<int>());
            Assert.Equal("new", section["virtualNetwork"]!["name"]!.GetValue<string>());
            if (wrapped) Assert.Equal("Warning", saved["Serilog"]!["MinimumLevel"]!.GetValue<string>());
            Assert.Empty(Directory.GetFiles(directory.FullName, "*.tmp"));
        }
        finally { directory.Delete(recursive: true); }
    }

    [Fact]
    public async Task InvalidAndCancelledSavesCannotReplaceOriginalFile()
    {
        var directory = Directory.CreateTempSubdirectory("pn-config-test-");
        var path = Path.Combine(directory.FullName, "appsettings.json");
        try
        {
            const string original = """{"ProxyNetworkerClient":{"apiServer":"http://127.0.0.1:13301"}}""";
            await File.WriteAllTextAsync(path, original);
            var store = new ClientConfigurationStore(path);
            var invalid = new ProxyNetworkerClientConfig { VirtualNetwork = new() { Enabled = true } };
            await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(invalid));
            Assert.Equal(original, await File.ReadAllTextAsync(path));
            using var cancel = new CancellationTokenSource();
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveAsync(new(), cancel.Token));
            Assert.Equal(original, await File.ReadAllTextAsync(path));
        }
        finally { directory.Delete(recursive: true); }
    }

    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("https://user:password@example.invalid")]
    [InlineData("https://example.invalid/?token=value")]
    [InlineData("")]
    public void UnsafeManagementEndpointsAreRejected(string endpoint) =>
        Assert.Throws<ArgumentException>(() => ClientConfigurationStore.ValidateServer(endpoint));

    [Fact]
    public async Task ImportUsesExistingCaseInsensitiveAppsettingsContract()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(
            """{"proxyNetworkerClient":{"APISERVER":"http://127.0.0.1:13301","VirtualNetwork":{"Enabled":false,"Name":"existing"}}}"""));
        var configuration = await ProxyNetworkerClientConfig.LoadAsync(stream, CancellationToken.None);
        Assert.Equal("existing", configuration.VirtualNetwork!.Name);
        Assert.Equal("http://127.0.0.1:13301", configuration.GetDefaultServer());
    }
}
