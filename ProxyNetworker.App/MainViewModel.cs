using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProxyNetworker.Client.Runtime;
using Avalonia.Threading;

namespace ProxyNetworker.App;

public partial class MainViewModel : ObservableObject
{
    private readonly IClientController _controller;
    private ClientConfigurationStore _store;
    private ProxyNetworkerClientConfig _configuration = new() { ApiServer = "http://127.0.0.1:13301", VirtualNetwork = new() { Enabled = true } };
    private ClientState _state = ClientState.Stopped;
    private DateTimeOffset _lastSample = DateTimeOffset.UtcNow;
    private long _lastSent, _lastReceived;
    private bool _connecting;
    public ObservableCollection<ClientPeerRow> Peers { get; } = [];
    public ObservableCollection<ClientLog> Logs { get; } = [];
    public ObservableCollection<PortTunnelEditor> PortTunnels { get; } = [];

    public string Server { get => field; set => SetProperty(ref field, value); } = "http://127.0.0.1:13301";
    public string Token { get => field; set => SetProperty(ref field, value); } = "";
    public string NodeName { get => field; set => SetProperty(ref field, value); } = "pn-client";
    public string NetworkServer { get => field; set => SetProperty(ref field, value); } = "";
    public string ServerError { get => field; set => SetProperty(ref field, value); } = "";
    public string TokenError { get => field; set => SetProperty(ref field, value); } = "";
    public string NodeNameError { get => field; set => SetProperty(ref field, value); } = "";
    public bool VirtualNetworkEnabled { get => field; set => SetProperty(ref field, value); } = true;
    public bool RelayOnly { get => field; set => SetProperty(ref field, value); }
    public bool StableDirectPathSelection { get => field; set => SetProperty(ref field, value); } = true;
    public bool ShowToken { get => field; set => SetProperty(ref field, value); }
    public bool Busy { get => field; set => SetProperty(ref field, value); }
    public bool CanEditConfiguration { get => field; set => SetProperty(ref field, value); } = true;
    public string Status { get => field; set => SetProperty(ref field, value); } = "未连接";
    public string Error { get => field; set => SetProperty(ref field, value); } = "";
    public string Notice { get => field; set => SetProperty(ref field, value); } = "";
    public string VirtualAddress { get => field; set => SetProperty(ref field, value); } = "尚未分配";
    public string InterfaceName { get => field; set => SetProperty(ref field, value); } = "未创建";
    public string Transport { get => field; set => SetProperty(ref field, value); } = "未运行";
    public string Encryption { get => field; set => SetProperty(ref field, value); } = "未运行";
    public string UploadRate { get => field; set => SetProperty(ref field, value); } = "0 B/s";
    public string DownloadRate { get => field; set => SetProperty(ref field, value); } = "0 B/s";
    public string Sent { get => field; set => SetProperty(ref field, value); } = "0 B";
    public string Received { get => field; set => SetProperty(ref field, value); } = "0 B";
    public string PortStatus { get => field; set => SetProperty(ref field, value); } = "0 / 0";
    public bool HasBackgroundSettings => _controller is IClientBackgroundSettings;
    public string BackgroundBatteryStatus { get => field; set => SetProperty(ref field, value); } = "";
    public string BackgroundNotificationStatus { get => field; set => SetProperty(ref field, value); } = "";
    public bool HasPeers { get => field; set => SetProperty(ref field, value); }
    public bool HasLogs { get => field; set => SetProperty(ref field, value); }
    public bool HasError => Error.Length != 0;
    public bool HasNotice => Notice.Length != 0;
    public int SelectedMobileView
    {
        get => field;
        set
        {
            if (!SetProperty(ref field, value)) return;
            OnPropertyChanged(nameof(IsConnectionView));
            OnPropertyChanged(nameof(IsPeersView));
            OnPropertyChanged(nameof(IsSettingsView));
            OnPropertyChanged(nameof(IsLogsView));
        }
    }
    public bool IsConnectionView => SelectedMobileView == 0;
    public bool IsPeersView => SelectedMobileView == 1;
    public bool IsSettingsView => SelectedMobileView == 2;
    public bool IsLogsView => SelectedMobileView == 3;
    public char TokenMask => ShowToken ? '\0' : '\u2022';
    public string ConfigurationName => Path.GetFileName(_store.Path);
    public bool CanConnect => !Busy && CanEditConfiguration;
    public bool CanDisconnect => _connecting || !Busy && _state.State != ClientConnectionState.Stopped;
    public IAsyncRelayCommand ConnectCommand { get; }
    public IAsyncRelayCommand DisconnectCommand { get; }
    public IAsyncRelayCommand SaveCommand { get; }
    public IRelayCommand AddPortCommand { get; }
    public IRelayCommand BatterySettingsCommand { get; }
    public IRelayCommand NotificationSettingsCommand { get; }

    public MainViewModel(IClientController controller, ClientConfigurationStore store)
    {
        _controller = controller; _store = store;
        ConnectCommand = new AsyncRelayCommand(ConnectAsync, () => CanConnect);
        DisconnectCommand = new AsyncRelayCommand(DisconnectAsync, () => CanDisconnect);
        SaveCommand = new AsyncRelayCommand(SaveAsync, () => !Busy && CanEditConfiguration);
        AddPortCommand = new RelayCommand(() => AddPort(new() { Enabled = true }));
        BatterySettingsCommand = new RelayCommand(() => OpenBackgroundSettings(settings => settings.OpenBatterySettings()), () => HasBackgroundSettings);
        NotificationSettingsCommand = new RelayCommand(() => OpenBackgroundSettings(settings => settings.OpenNotificationSettings()), () => HasBackgroundSettings);
        controller.StateChanged += OnState;
        controller.LogReceived += OnLog;
        PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(Busy) or nameof(CanEditConfiguration))
            {
                OnPropertyChanged(nameof(CanConnect)); OnPropertyChanged(nameof(CanDisconnect));
                ConnectCommand.NotifyCanExecuteChanged(); DisconnectCommand.NotifyCanExecuteChanged(); SaveCommand.NotifyCanExecuteChanged();
            }
            if (args.PropertyName == nameof(ShowToken)) OnPropertyChanged(nameof(TokenMask));
            if (args.PropertyName == nameof(Error)) OnPropertyChanged(nameof(HasError));
            if (args.PropertyName == nameof(Notice)) OnPropertyChanged(nameof(HasNotice));
            if (args.PropertyName == nameof(Server)) ServerError = "";
            if (args.PropertyName == nameof(Token)) TokenError = "";
            if (args.PropertyName == nameof(NodeName)) NodeNameError = "";
        };
    }

    public async Task InitializeAsync()
    {
        ApplyState(_controller.State);
        try
        {
            if (File.Exists(_store.Path)) Apply(await _store.LoadAsync());
        }
        catch (Exception exception) { Error = exception.Message; }
        RefreshBackgroundSettings();
    }

    public void RefreshBackgroundSettings()
    {
        if (_controller is not IClientBackgroundSettings settings) return;
        try
        {
            var state = settings.GetBackgroundState();
            BackgroundBatteryStatus = state.BatteryOptimizationExempt ? "不受限制" : "系统优化";
            BackgroundNotificationStatus = state.NotificationsEnabled ? "已开启" : "已关闭";
        }
        catch (Exception exception) { Error = exception.Message; }
    }

    private void OpenBackgroundSettings(Action<IClientBackgroundSettings> open)
    {
        if (_controller is not IClientBackgroundSettings settings) return;
        try { open(settings); }
        catch (Exception exception) { Error = exception.Message; }
    }

    private void Apply(ProxyNetworkerClientConfig configuration)
    {
        _configuration = configuration;
        Server = configuration.GetDefaultServer() ?? "http://127.0.0.1:13301";
        var network = configuration.VirtualNetwork ?? new();
        Token = network.Token ?? ""; NodeName = network.Name; VirtualNetworkEnabled = network.Enabled;
        NetworkServer = network.Server ?? "";
        RelayOnly = network.RelayOnly; StableDirectPathSelection = network.StableDirectPathSelection;
        PortTunnels.Clear();
        foreach (var port in configuration.PortTunnels) AddPort(port);
    }

    private void AddPort(RemotePortTunnelClientConfig configuration) =>
        PortTunnels.Add(new(configuration, editor => PortTunnels.Remove(editor)));

    private ProxyNetworkerClientConfig Capture()
    {
        _configuration.ApiServer = Server.Trim();
        _configuration.VirtualNetwork ??= new();
        var network = _configuration.VirtualNetwork;
        network.Enabled = VirtualNetworkEnabled; network.Name = NodeName.Trim(); network.Token = Token.Trim();
        network.Server = string.IsNullOrWhiteSpace(NetworkServer) ? null : NetworkServer.Trim();
        network.RelayOnly = RelayOnly; network.StableDirectPathSelection = StableDirectPathSelection;
        _configuration.PortTunnels = PortTunnels.Select(port => port.Capture()).ToList();
        ServerError = TokenError = NodeNameError = "";
        if (VirtualNetworkEnabled)
        {
            try { ClientConfigurationStore.ValidateServer(network.Server ?? Server); }
            catch (ArgumentException) { ServerError = "请填写有效的 HTTP(S) 管理服务端地址"; }
            if (string.IsNullOrWhiteSpace(NodeName)) NodeNameError = "请填写节点名称";
            if (network.AuthenticationKey.Length == 0 && (Token.Length == 0 || !Token.StartsWith("vnet_", StringComparison.OrdinalIgnoreCase)))
                TokenError = "请填写服务端签发的 vnet_ Token";
            if (ServerError.Length + TokenError.Length + NodeNameError.Length != 0)
                throw new ArgumentException("请检查标记的连接配置后重试");
        }
        return _configuration;
    }

    private async Task SaveAsync()
    {
        Busy = true; Error = ""; Notice = "";
        try { await _store.SaveAsync(Capture()); Notice = "配置已保存"; }
        catch (Exception exception) { Error = exception.Message; }
        finally { Busy = false; }
    }
    private async Task ConnectAsync(CancellationToken cancellationToken)
    {
        _connecting = true;
        Busy = true; Error = ""; Notice = "";
        try
        {
            var configuration = Capture();
            if (configuration.VirtualNetwork is not { Enabled: true } && !configuration.PortTunnels.Any(port => port.Enabled))
                throw new ArgumentException("请启用虚拟局域网或至少一个端口隧道");
            CanEditConfiguration = false;
            await _store.SaveAsync(configuration, cancellationToken);
            await _controller.ConnectAsync(configuration, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            try { await _controller.DisconnectAsync(CancellationToken.None); }
            catch (Exception exception) { Error = exception.Message; }
        }
        catch (Exception exception) { Error = exception.Message; }
        finally
        {
            _connecting = false;
            CanEditConfiguration = _controller.State.State is ClientConnectionState.Stopped or ClientConnectionState.Failed;
            Busy = false;
        }
    }
    private async Task DisconnectAsync()
    {
        if (ConnectCommand.IsRunning)
        {
            ConnectCommand.Cancel();
            if (ConnectCommand.ExecutionTask is { } pending) await pending;
            return;
        }
        Busy = true; Error = "";
        try { await _controller.DisconnectAsync(CancellationToken.None); }
        catch (Exception exception) { Error = exception.Message; }
        finally { Busy = false; }
    }

    private void OnState(ClientState state) => Dispatcher.UIThread.Post(() => ApplyState(state));

    private void ApplyState(ClientState state)
    {
        _state = state;
        CanEditConfiguration = state.State is ClientConnectionState.Stopped or ClientConnectionState.Failed;
        Status = state.State switch
        {
            ClientConnectionState.Starting => "正在启动", ClientConnectionState.Running => "运行中",
            ClientConnectionState.Reconnecting => "正在重连", ClientConnectionState.Stopping => "正在停止",
            ClientConnectionState.Failed => "连接失败", _ => "未连接"
        };
        VirtualAddress = state.VirtualAddress ?? "尚未分配"; InterfaceName = state.InterfaceName ?? "未创建";
        Transport = state.Transport?.ToUpperInvariant() ?? (state.PortTunnels.Length > 0 ? "端口隧道" : "未运行");
        Encryption = state.VirtualAddress is not null ? state.EncryptionEnabled ? "AES-GCM" : "仅认证"
            : state.PortTunnels.Length > 0 ? "不适用" : "未运行";
        Error = state.Error ?? "";
        var now = DateTimeOffset.UtcNow;
        var seconds = Math.Max((now - _lastSample).TotalSeconds, 0.1);
        UploadRate = FormatBytes((long)Math.Max(0, (state.BytesSent - _lastSent) / seconds)) + "/s";
        DownloadRate = FormatBytes((long)Math.Max(0, (state.BytesReceived - _lastReceived) / seconds)) + "/s";
        _lastSample = now; _lastSent = state.BytesSent; _lastReceived = state.BytesReceived;
        Sent = FormatBytes(state.BytesSent); Received = FormatBytes(state.BytesReceived);
        Peers.Clear();
        foreach (var peer in state.Peers) Peers.Add(new(peer));
        HasPeers = Peers.Count != 0;
        PortStatus = $"{state.PortTunnels.Count(port => port.Ready)} / {state.PortTunnels.Length}";
        OnPropertyChanged(nameof(CanDisconnect));
        DisconnectCommand.NotifyCanExecuteChanged();
    }

    private void OnLog(ClientLog log) => Dispatcher.UIThread.Post(() =>
    {
        while (Logs.Count >= 300) Logs.RemoveAt(0);
        Logs.Add(log); HasLogs = true;
    });

    public async Task ImportAsync(Stream stream, string? localPath)
    {
        if (!CanEditConfiguration) return;
        try
        {
            if (stream.CanSeek && stream.Length > 1024 * 1024) throw new InvalidDataException("Configuration exceeds 1 MiB.");
            var configuration = await ProxyNetworkerClientConfig.LoadAsync(stream, CancellationToken.None);
            Apply(configuration);
            if (!OperatingSystem.IsAndroid() && localPath is not null) _store = new(localPath);
            OnPropertyChanged(nameof(ConfigurationName));
            Error = ""; Notice = "配置已载入";
        }
        catch (Exception exception) { Error = exception.Message; }
    }

    public string ExportDiagnostics()
    {
        var builder = new StringBuilder();
        builder.AppendLine($"ProxyNetworker | {_state.State} | {_state.Transport} | encryption={_state.EncryptionEnabled}");
        builder.AppendLine($"sent={_state.BytesSent} received={_state.BytesReceived}");
        foreach (var log in Logs) builder.AppendLine($"{log.Timestamp:O} [{log.Level}] {log.Message}");
        var output = builder.ToString();
        foreach (var secret in new[] { Token }.Concat(PortTunnels.Select(port => port.Token)).Where(value => value.Length > 0))
            output = output.Replace(secret, "[redacted]", StringComparison.Ordinal);
        return System.Text.RegularExpressions.Regex.Replace(output,
            @"(?<!\d)(?:\d{1,3}\.){3}\d{1,3}(?!\d)|(?:https?|wss?)://\S+|(?:vnet_|ptun_)[A-Za-z0-9_-]+", "[redacted]");
    }

    public async Task ShutdownAsync()
    {
        ConnectCommand.Cancel();
        if (ConnectCommand.ExecutionTask is { } pending) await pending;
        await _controller.DisposeAsync();
        _controller.StateChanged -= OnState; _controller.LogReceived -= OnLog;
    }
    private static string FormatBytes(long value) => value switch
    {
        >= 1024 * 1024 * 1024 => $"{value / (1024d * 1024 * 1024):0.00} GB",
        >= 1024 * 1024 => $"{value / (1024d * 1024):0.00} MB",
        >= 1024 => $"{value / 1024d:0.0} KB", _ => $"{value} B"
    };
}

public sealed record ClientPeerRow(string Address, string Endpoint, string Path, string Rtt)
{
    public ClientPeerRow(ClientPeerState peer) : this(peer.Address, peer.Endpoint, peer.Path switch
    { "direct" => "直连", "mesh" => "Mesh", "relay" => "中继", _ => "探测中" },
        peer.RttMilliseconds is { } rtt ? $"{rtt:0.0} ms" : "未测量") { }
}

public partial class PortTunnelEditor : ObservableObject
{
    public string Name { get => field; set => SetProperty(ref field, value); } = "";
    public string Server { get => field; set => SetProperty(ref field, value); } = "";
    public string Token { get => field; set => SetProperty(ref field, value); } = "";
    public bool Enabled { get => field; set => SetProperty(ref field, value); }
    public IRelayCommand RemoveCommand { get; }
    public PortTunnelEditor(RemotePortTunnelClientConfig configuration, Action<PortTunnelEditor> remove)
    {
        Name = configuration.Name ?? ""; Server = configuration.Server ?? "";
        Token = configuration.Token; Enabled = configuration.Enabled;
        RemoveCommand = new RelayCommand(() => remove(this));
    }
    public RemotePortTunnelClientConfig Capture() => new()
    { Name = Name.Trim(), Server = string.IsNullOrWhiteSpace(Server) ? null : Server.Trim(), Token = Token.Trim(), Enabled = Enabled };
}
