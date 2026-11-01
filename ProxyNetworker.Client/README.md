# ProxyNetworker.Client

支持以下客户端平台：

- Windows x64
- Linux x64
- Linux ARM64

发布产物是自包含部署，不要求目标机器预装 .NET 运行时。

同一个 `ptun_` 或 `vnet_` Token 在同一台电脑上只能启动一个客户端进程。重复启动会直接退出，避免同一节点注册多个 UDP 端点并造成路径抖动。

Token 模式的虚拟网络会定期刷新运行时配置。服务端重启、后台停用后重新启用，或者会话密钥变化时，客户端会使用原 Token 自动获取新配置并重建 TUN，不需要手工重启。

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

## AEAD 自检

客户端启动加密数据面之前会执行一次 AES-GCM 已知答案检查。平台实现通过时使用硬件加速；如果发现标签不能与其他实现互操作，会自动切换到托管 GCM。可随时手动验证当前发布物：

```bash
./ProxyNetworker.Client crypto self-test
```

通过时会输出 `AES-GCM self-test: PASS` 和实际使用的后端。发布工作流会在 Windows x64、Linux x64、Linux ARM64 的 AOT 打包前运行同一检查。

## Windows x64

Windows 客户端需要管理员权限运行。远程端口穿透和虚拟局域网命令都需要从“以管理员身份运行”的 PowerShell 或终端启动。

`wintun.dll` 会随发布产物自动复制。

虚拟 IP 由服务端按 Token 分配并持久化。客户端不提供手动指定虚拟 IP 的参数；服务端管理员修改地址后，客户端会在下一次配置刷新时自动重建 TUN 并切换到新地址。

## Linux x64 / ARM64

Linux 发布产物不需要 .NET 运行时，但虚拟局域网模式还需要：

- `/dev/net/tun`，例如通过 `sudo modprobe tun` 提供
- `ip` 命令，通常由 `iproute2` 包提供
- root 权限，或具备 `CAP_NET_ADMIN` 能力

Linux x64/ARM64 虚拟网络会优先启用批量 UDP 收发和 TUN GSO：UDP 使用 `recvmmsg`/`sendmmsg`，TUN 使用 `vnet_hdr` 和 TSO 元数据。内核、libc 或设备不支持某个能力时会自动回退，不影响 Token 和虚拟 IP。高吞吐部署建议在服务端和客户端执行：

```bash
printf '%s\n' \
  'net.core.rmem_max = 16777216' \
  'net.core.wmem_max = 16777216' \
  | sudo tee /etc/sysctl.d/99-proxynetworker-net.conf
sudo sysctl -p /etc/sysctl.d/99-proxynetworker-net.conf
```

如需在排障时只保留 `vnet_hdr` 读取和普通 TUN 写入，可设置 `PN_DISABLE_GSO_TX=1` 后启动客户端。该开关不影响 AES-GCM/HMAC-only 选择。

Ubuntu/Debian 示例：

```bash
sudo apt install iproute2
sudo modprobe tun
chmod +x ./ProxyNetworker.Client
sudo ./ProxyNetworker.Client virtual-network --server SERVER:13301 --token vnet_xxx --name pn-client
```

Linux x64 的 QUIC 客户端依赖随包附带的 `libmsquic.so.2` 和兼容 OpenSSL 1.1 的 `libcrypto.so.1.1`。使用发布目录中的 `run.sh` 启动时会自动从程序目录加载这两个运行库；手工启动时应先设置：

```bash
export LD_LIBRARY_PATH="$PWD${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"
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

虚拟网络默认启用 AES-GCM。管理员在服务端关闭加密后，客户端仍使用会话密钥执行 HMAC-SHA256 完整性校验，但报文内容不再保密。手工直连模式也可通过 `--no-encryption` 明确选择该模式。

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
