# ProxyNetworker.Server 配置说明

`NetworkDefaults` 只提供新建虚拟网络时的默认参数。修改这些配置不会回写已有数据库记录。

| 配置项 | 示例 | 用途 |
| --- | --- | --- |
| `PublicListenAddress` | `0.0.0.0` | 端口穿透和虚拟网络公网端口的 IPv4 监听地址。与管理 API 的 `urls` 独立配置。 |
| `AdvertisedHost` | `www.example.com` | 下发给客户端的数据端口主机名或 IP。通过反向代理、SSH 隧道访问管理 API 时必须显式配置，避免客户端拿到 `127.0.0.1`。 |
| `VirtualNetworkGatewayAddress` | `10.66.0.1` | 新建虚拟网络未指定网关时使用的默认 IPv4 网关。已有网络保留数据库中的网关。 |
| `VirtualNetworkPrefixLength` | `24` | 新建虚拟网络的默认前缀长度，允许范围为 `1` 到 `30`。 |
| `VirtualNetworkMtu` | `1400` | 新建虚拟网络的默认 MTU，允许范围为 `576` 到 `65535`。 |

## 端口分配

管理面板创建的端口穿透和虚拟网络不再使用独立的 `PortTunnelListenPort` 或 `VirtualNetworkListenPort` 配置。它们的公网端口统一由以下系统设置管理：

- `SystemSettings.PublicPortRangeStart`
- `SystemSettings.PublicPortRangeEnd`

`SystemSettings` 会持久化到 SQLite。已有数据库不会因为修改 `appsettings.json` 自动改变系统端口范围，需要在管理页或 API 中更新。

## 监听地址

管理 API 的监听地址由服务端顶层 `urls` 控制，例如：

```json
{
  "urls": "http://127.0.0.1:13301",
  "NetworkDefaults": {
    "PublicListenAddress": "0.0.0.0"
  }
}
```

- `urls`：管理页面和管理 API 的监听地址。设为 `127.0.0.1` 时只能本机访问。
- `PublicListenAddress`：从 `SystemSettings.PublicPortRangeStart` 到 `PublicPortRangeEnd` 分配的端口穿透和虚拟网络端口的监听地址。
- `PublicListenAddress` 设为 `0.0.0.0` 表示监听所有 IPv4 网卡；设为 `127.0.0.1` 表示仅本机可连接；设为具体内网 IP 表示只绑定该网卡。
- 这两个配置互不影响。管理入口可以只开放本机，同时公网端口监听在指定内网网卡上。

## 虚拟网络传输

虚拟网络数据面使用 UDP，而不是 TCP。管理 API 和 Token 校验通过 HTTPS 完成；客户端获取配置时会收到该虚拟网络的临时 256 位会话密钥。之后所有 UDP 控制帧和 IP 包都使用 AES-GCM 加密和校验，旧版无认证数据帧会被拒绝。

客户端启动后会向服务端上报 UDP 候选地址，服务端补充公网映射并广播给同网络节点。节点通过加密探针测量 RTT、抖动和丢包，交换链路状态后计算最低成本路径。UDP 支持直接链路、多跳节点转发和服务端中继兜底；`run.sh status` 会显示 `Path: direct ...`、`Path: mesh ...` 或 `Path: relay`。

会话密钥在服务端进程重启后轮换。虚拟网络支持 UDP 和 QUIC；UDP 提供加密网状路由，同时保留中继作为兼容兜底。QUIC 当前提供可靠流式中继，P2P 直连仍受 NAT 打洞和 UDP socket 共享能力限制。

## 旧版运行时接口

旧版 `/api/tunnels/port`、`/api/tunnels/virtual-network` 和对应的停止接口已经移除。当前正式流程是在管理面板或 `/api/port-tunnels`、`/api/virtual-networks` 中创建资源，再使用 `ptun_` 或 `vnet_` Token 启动客户端。

已有数据库中的旧 `Tunnels` 表不会被自动删除，避免启动过程执行破坏性迁移；应用不再读取或写入该表。

## 示例

```json
{
  "NetworkDefaults": {
    "PublicListenAddress": "0.0.0.0",
    "AdvertisedHost": "www.example.com",
    "VirtualNetworkGatewayAddress": "10.66.0.1",
    "VirtualNetworkPrefixLength": 24,
    "VirtualNetworkMtu": 1400
  }
}
```

标准 JSON 不支持注释。因此配置说明放在本文档和 `NetworkDefaultsOptions` 的 XML 注释中，避免在 `appsettings.json` 内加入 `//` 或 `/* */` 导致配置加载失败。
