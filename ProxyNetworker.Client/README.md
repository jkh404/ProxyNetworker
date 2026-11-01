# ProxyNetworker.Client

支持以下客户端平台：

- Windows x64
- Linux x64
- Linux ARM64

发布产物是自包含部署，不要求目标机器预装 .NET 运行时。

## 发布

统一使用仓库根目录的交互式发布脚本。第一次使用前安装 `dotnet-script`：

```powershell
dotnet tool install --global dotnet-script
```

在仓库根目录运行：

```powershell
dotnet script ".\通用dotnet编译发布脚本.csx" -- ".\ProxyNetworker.Client\ProxyNetworker.Client.csproj"
```

脚本第一步选择平台，第二步选择“Native AOT（无需 .NET Runtime）”。输出目录可以按提示修改，默认输出到仓库根目录的 `publish\ProxyNetworker.Client_<rid>_aot`。

## Native AOT

项目自身不包含跨平台 Native AOT 链接器。从 Windows 交叉发布 Linux Native AOT 时，通用发布脚本会临时注入 `PublishAotClang 1.0.5` 完成 Zig 链接，不需要把交叉编译包写进 Client 项目。

Native AOT 产物不包含 IL 程序集，也不要求目标机器安装 .NET 运行时。JSON 使用源生成序列化；Serilog 动态配置绑定已移除以满足裁剪和 AOT 约束。

## Windows x64

Windows 客户端需要管理员权限运行。远程端口穿透和虚拟局域网命令都需要从“以管理员身份运行”的 PowerShell 或终端启动。

`wintun.dll` 会随发布产物自动复制。

## Linux x64 / ARM64

Linux 发布产物不需要 .NET 运行时，但虚拟局域网模式还需要：

- `/dev/net/tun`，例如通过 `sudo modprobe tun` 提供
- `ip` 命令，通常由 `iproute2` 包提供
- root 权限，或具备 `CAP_NET_ADMIN` 能力

Ubuntu/Debian 示例：

```bash
sudo apt install iproute2
sudo modprobe tun
chmod +x ./ProxyNetworker.Client
sudo ./ProxyNetworker.Client virtual-network --server SERVER:13301 --token vnet_xxx --name pn-client
```

Linux 上只运行端口穿透时，通常不需要 root，因为该模式不创建 TUN 设备。

## Linux 后台运行

Linux 发布目录会包含 `run.sh`。脚本使用 `nohup` 后台启动客户端，并把终端输出写入 `logs/client-console-<时间>.log`，PID 保存在 `run/client.pid`。

```bash
bash run.sh start
bash run.sh status
bash run.sh logs
bash run.sh stop
```

`start` 和 `status` 会在隧道准备好后直接显示分配到的公网端口，例如：

```text
Tunnel: web (tcp)
Assigned public port: <published-port>
Public endpoint: server.example.com:<published-port>
Private target: 127.0.0.1:8080
```

虚拟网络会显示分配地址、服务器端点以及当前路径：

```text
Virtual Network: test
Assigned address: <assigned-vnet-ip>/24
Server endpoint: server.example.com:<data-port>
Path: direct <local-vnet-ip> -> <peer-public-ip>:<public-port>
```

无法建立直连时会显示 `Path: relay`，数据自动继续通过服务端中继。

当节点 A 不能直接连接节点 B，但两者都能连接中间节点 C 时，客户端会选择最低成本的多跳路径：

```text
Path: mesh 10.66.0.2 -> 10.66.0.3 -> 10.66.0.4 (6.0 ms; route 10.66.0.2 -> 10.66.0.3 -> 10.66.0.4)
```

UDP 路由同时考虑 RTT、抖动、丢包率和跳数，并限制最大 8 跳。QUIC 当前仍使用服务端可靠中继，不宣称已支持 P2P mesh。

如果客户端与服务端数据端口运行在同一台机器上，客户端会自动使用 relay-only，避免公网/NAT hairpin 导致的不稳定直连。

启动参数会原样传给客户端：

```bash
bash run.sh start connect --server http://SERVER:13301 --token ptun_xxx
sudo bash run.sh start virtual-network --server SERVER:13301 --token vnet_xxx
```

`restart` 会先停止旧进程再启动：

```bash
bash run.sh restart
```

## 使用 Token 运行

```bash
./ProxyNetworker.Client connect --server http://SERVER:13301 --token ptun_xxx
./ProxyNetworker.Client connect --server http://SERVER:13301 --token vnet_xxx
```

客户端会根据 `ptun_` 或 `vnet_` 前缀自动选择模式。

`PN_SERVER` 和 `PN_TOKEN` 环境变量同时适用于 `connect` 和 `virtual-network` 子命令。Linux 虚拟网络接口名超过 15 字节时，客户端会显示原名称并自动生成稳定短名称，例如 `node37-linux-x64` 会缩短为合法接口名。

## 配置文件

```bash
./ProxyNetworker.Client init-config --path proxynetworker.client.json
./ProxyNetworker.Client run --config proxynetworker.client.json
```

完整命令行参数可运行：

```bash
./ProxyNetworker.Client help
```
