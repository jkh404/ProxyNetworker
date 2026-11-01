# ProxyNetworker

[![Release](https://github.com/jkh404/ProxyNetworker/actions/workflows/release.yml/badge.svg)](https://github.com/jkh404/ProxyNetworker/actions/workflows/release.yml)

ProxyNetworker 是一个面向自托管场景的网络工具，提供公网端口穿透和加密虚拟局域网能力。项目由 ASP.NET Core 服务端、跨平台客户端和共享网络核心组成。

## 项目亮点

ProxyNetworker 的目标不是简单复制一个端口转发工具，而是把端口穿透、加密虚拟局域网、智能直连/中继和可视化管理整合到一套可自托管方案中。

- **一个服务端覆盖两类场景**：同时提供 TCP/UDP 端口穿透和虚拟局域网，不需要分别部署端口转发工具、VPN 和独立管理面板。
- **优先直连，失败自动中继**：UDP 节点交换局域网和公网候选地址，通过加密探针选择可用的低延迟直连路径；无法直连时自动回退到服务端中继。
- **UDP/QUIC 双协议**：UDP 作为默认协议，兼顾直连和低延迟；QUIC 为需要可靠流式中继的场景提供备选。KCP 经过实际测试后因尾部延迟过高被移除。
- **跨平台 Native AOT 客户端**：已实测 Windows 11 x64、Ubuntu 24.04 x64 和 Debian 13 Raspberry Pi arm64，发布后不需要目标机器预装 .NET。
- **完整的数据面安全**：Token 按资源发放，虚拟网络使用临时 256 位会话密钥，UDP 数据帧通过 AES-GCM 加密和完整性校验。
- **面向运维的管理能力**：支持用户与资源配额、Token 管理、网络启停、节点封禁、在线设备、连接历史和协议选择。
- **部署门槛低**：服务端使用 SQLite，无外部数据库依赖；支持 Docker Compose；Linux 客户端提供基于 `nohup` 的后台启动脚本。
- **可验证的工程选择**：协议与平台结论来自实际构建、跨节点测试和延迟数据，而不是只依据理论特性。

## 主要功能

- 端口穿透：将 TCP 或 UDP 内网服务映射到服务端分配的稳定公网端口。
- 虚拟局域网：为不同网络中的设备分配虚拟 IPv4 地址，支持节点直接通信。
- UDP 智能路由：优先建立加密点对点直连，直连不可用时自动回退到服务端中继。
- QUIC 传输：为需要可靠流式中继的场景提供 QUIC 支持。
- Token 接入：使用 `ptun_` 和 `vnet_` Token 分别接入端口穿透和虚拟网络。
- 管理面板：管理用户、配额、Token、在线节点、连接历史、网络状态和节点封禁。
- 跨平台客户端：支持 Windows x64、Linux x64 和 Linux ARM64，可发布为 Native AOT 自包含程序。
- SQLite 持久化：服务端配置、用户、资源和会话历史保存在本地 SQLite 数据库中。

## 平台支持

### 已实测平台

| 组件 | 操作系统 | 架构 | 验证范围 |
| --- | --- | --- | --- |
| Server | Windows 11 专业版 | x64 | 本地启动、管理 API、SQLite 数据库迁移 |
| Server | Ubuntu 24.04.5 LTS | x64 | 管理面板、端口分配、UDP/QUIC 虚拟网络和中继服务 |
| Server | Ubuntu 24.04.5 LTS + Docker | x64 | Docker Compose 构建、健康检查和线上部署 |
| Client | Windows 11 专业版 | x64 | 端口穿透、虚拟网络、TUN 设备和多节点互通 |
| Client | Ubuntu 24.04.5 LTS | x64 | 端口穿透、虚拟网络、UDP 直连与中继回退 |
| Client | Debian GNU/Linux 13（Raspberry Pi） | arm64 | 虚拟网络、TUN 设备和跨平台节点互通 |

“已实测”表示上述环境和架构中已经完成实际运行验证。其他 Linux 发行版只要满足 .NET 10、TUN 和 `iproute2` 等依赖，通常也可以运行，但尚未逐一列入验证矩阵。

### 运行要求

| 组件 | 要求 |
| --- | --- |
| Server | .NET 10；Linux 推荐 Docker Compose 或 systemd 部署 |
| Windows Client | 创建 TUN 设备时需要管理员权限 |
| Linux Client | 虚拟网络模式需要 `/dev/net/tun`、`iproute2`、root 或 `CAP_NET_ADMIN` |

服务端基于 .NET 10。客户端可发布为自包含程序，目标机器不需要预装 .NET 运行时。

## 项目结构

```text
ProxyNetworker.Client/   跨平台客户端
ProxyNetworker.Core/     隧道、虚拟网络、加密和共享协议实现
ProxyNetworker.Server/   ASP.NET Core 服务端与管理面板
TestConsole/             开发测试工具
ProxyNetworker.slnx      .NET 解决方案
```

## 快速开始

### 环境要求

- .NET 10 SDK
- Windows 或 Linux
- Docker 与 Docker Compose，可选，用于部署服务端

恢复依赖并构建完整解决方案：

```powershell
dotnet restore .\ProxyNetworker.slnx
dotnet build .\ProxyNetworker.slnx -c Release
```

### 启动服务端

```powershell
dotnet run --project .\ProxyNetworker.Server\ProxyNetworker.Server.csproj
```

默认管理地址为：

```text
http://127.0.0.1:13301
```

健康检查：

```text
http://127.0.0.1:13301/health
```

首次启动且数据库中没有用户时，服务端会创建管理员 `sky`，并生成 12 位随机强密码。初始密码只会在首次创建管理员时写入启动日志，登录后应立即修改。

### Docker 部署

```bash
cd ProxyNetworker.Server
mkdir -p data logs data-protection-keys
docker compose up -d --build
docker compose ps
curl http://127.0.0.1:13301/health
```

Docker 部署、动态端口范围和反向代理要求见 [服务端 Docker 文档](ProxyNetworker.Server/README.Docker.md)。

## 客户端用法

在管理面板中创建端口穿透或虚拟网络，并生成对应 Token。客户端会根据 Token 前缀自动选择模式。

端口穿透：

```bash
./ProxyNetworker.Client connect --server http://SERVER:13301 --token ptun_xxx
```

虚拟局域网：

```bash
sudo ./ProxyNetworker.Client virtual-network --server SERVER:13301 --token vnet_xxx --name pn-client
```

也可以通过配置文件运行：

```bash
./ProxyNetworker.Client init-config --path proxynetworker.client.json
./ProxyNetworker.Client run --config proxynetworker.client.json
```

Windows 上请从管理员终端启动。Linux 虚拟网络模式需要 `/dev/net/tun`、`iproute2` 以及 root 或 `CAP_NET_ADMIN` 权限。

Linux 发布目录包含后台启动脚本：

```bash
bash run.sh start
bash run.sh status
bash run.sh logs
bash run.sh stop
```

完整客户端参数和发布说明见 [客户端文档](ProxyNetworker.Client/README.md)。

## 虚拟网络传输

| 协议 | 特点 | 适用场景 |
| --- | --- | --- |
| `udp` | 加密数据报、直连探测、智能路由、自动中继回退 | 默认选择，优先获得更低延迟 |
| `quic` | 加密可靠流、通过服务端中继 | 网络丢包较多或需要可靠流式传输 |

UDP 控制帧和虚拟网络 IP 包使用临时会话密钥进行 AES-GCM 加密和完整性校验。协议选择和测试数据见 [虚拟网络协议文档](ProxyNetworker.Server/README.Protocols.md)。

## Native AOT 发布

仓库根目录提供统一发布脚本。首次使用时安装 `dotnet-script`：

```powershell
dotnet tool install --global dotnet-script
dotnet script ".\通用dotnet编译发布脚本.csx" -- ".\ProxyNetworker.Client\ProxyNetworker.Client.csproj"
```

在脚本中选择目标平台和 `Native AOT`，发布产物默认位于 `publish/` 下。跨平台 Native AOT、后台运行和 Linux 依赖说明见 [客户端文档](ProxyNetworker.Client/README.md)。

## 配置

服务端的主要配置位于 `ProxyNetworker.Server/appsettings.json`：

- `urls`：管理页面和管理 API 的监听地址。
- `SystemSettings.PublicPortRangeStart` 和 `PublicPortRangeEnd`：动态公网端口范围。
- `NetworkDefaults.PublicListenAddress`：端口穿透和虚拟网络数据端口监听地址。
- `NetworkDefaults.VirtualNetworkGatewayAddress`：新虚拟网络默认网关。
- `NetworkDefaults.VirtualNetworkPrefixLength`：新虚拟网络默认地址前缀。
- `NetworkDefaults.VirtualNetworkMtu`：新虚拟网络默认 MTU。

详细说明见 [服务端配置文档](ProxyNetworker.Server/README.Configuration.md)。

## 安全建议

- 不要把管理端口直接暴露到公网，建议通过 HTTPS 反向代理发布。
- 限制 `data`、`logs` 和 `data-protection-keys` 目录的访问权限。
- 首次登录后立即修改管理员密码。
- Token 应按最小权限发放，不再使用时及时删除。
- 客户端配置文件包含 Token，不应提交到 Git 仓库。
- 公网数据端口范围应遵循防火墙和最小暴露原则。

## 文档

- [客户端使用与 AOT 发布](ProxyNetworker.Client/README.md)
- [服务端 Docker 部署](ProxyNetworker.Server/README.Docker.md)
- [服务端配置说明](ProxyNetworker.Server/README.Configuration.md)
- [虚拟网络协议与测试结果](ProxyNetworker.Server/README.Protocols.md)
- [发布新版本](RELEASING.md)

## 发布版本

推送 `v1.2.3` 形式的语义化版本标签后，GitHub Actions 会自动构建 Windows x64、Linux x64、Linux ARM64 客户端和 Linux x64 服务端，并创建 GitHub Release。

```bash
git tag -a v1.2.3 -m "ProxyNetworker v1.2.3"
git push origin v1.2.3
```

完整流程见 [发布说明](RELEASING.md)。

## 许可证

本项目采用 [MIT License](LICENSE) 开源。
