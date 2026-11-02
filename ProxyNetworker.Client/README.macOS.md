# ProxyNetworker.Client for macOS

这是 macOS 命令行客户端的开发预览，提供 `osx-arm64` 和 `osx-x64` 原生构建。当前没有用户 Mac 真机的互通、性能、睡眠唤醒或人工交互验收；CI 构建和回环测试不代表这些项目已通过。

## 组成

请保留完整发布目录，不要只移动可执行文件：

- `ProxyNetworker.Client`：Native AOT 程序，不需要另装 .NET。
- `libproxynetworker-utun.dylib`：Darwin 原生桥接库，处理 Apple Silicon/Intel 的变参 ABI 差异。
- `appsettings.json`：不含真实凭据的示例配置。
- `run.sh`：可选后台启动脚本。
- `PREVIEW.json`：CI 产物附带的架构、源提交、预览与签名状态。
- `LICENSE`：许可证。

程序使用系统自带的 utun，不安装额外内核扩展，也不要求关闭 SIP。创建接口和配置虚拟网段需要管理员权限。

## 启动

1. 根据机器架构选择完整安装包，在 `SHA256SUMS.txt` 所在目录运行 `shasum -a 256 -c SHA256SUMS.txt`。校验值验证完整性，不取代可信发行者签名。
2. 将现有客户端配置放入发布目录的 `appsettings.json`，保留原服务端地址和 Access Token。不要用示例文件覆盖现网配置。
3. 从该目录执行：

```sh
./ProxyNetworker.Client crypto self-test
sudo ./ProxyNetworker.Client
```

默认启动仍读取当前目录的 `appsettings.json`，不需要额外的 Token 命令行参数。虚拟地址仍由服务端配置决定。

初期以 IPv4、UDP、AES-GCM、MTU 1400 为使用范围；服务端资源应选择 UDP。QUIC 的 macOS 依赖不随本预览打包，不保证可用，也不会静默切换协议。

接口由内核动态命名为 `utunN`，配置中的节点名称不是固定接口名。客户端只添加虚拟网段路由，不替换已有路由，不修改系统默认路由或全局 DNS。自动配置拒绝 `/0` 默认路由。关闭最后一个 utun 句柄会撤销该接口及其所属路由。

## 可选后台运行

```sh
sudo bash run.sh start
sudo bash run.sh status
sudo bash run.sh logs
sudo bash run.sh stop
```

启动脚本先进入发布目录再无参数运行程序。PID、启动时间和终端日志保存在运行账号的 `~/Library/Application Support/ProxyNetworker/`；停止时核对 PID 与启动时间，避免误终止已复用的 PID。

程序滚动日志位于运行账号的 Application Support 下的 `ProxyNetworker/logs/`。使用 `sudo` 和普通账号时，配置权限及状态目录可能不同，应以相同身份操作启动/停止。没有默认安装或启用 `launchd` 服务。

配置包含凭据，应限制为可信账号可读，不加入 Git；诊断或反馈前先脱敏。

## 签名与验证边界

CI 产物为 ad-hoc 签名，不是 Developer ID 签名，也未公证。`codesign --verify --strict` 仅检查本地签名完整性，不能证明受信任的发行者身份；系统可能阻止从互联网下载的预览程序。本阶段不要求用户关闭 Gatekeeper 或其他系统保护。

当前验证计划包括：

- 两种架构的原生构建、核心回归、AOT 发布及加密自检。
- CI 环境中的隔离 utun 创建、原生地址族头、配置、ICMP 回环与空闲关闭。
- 路由冲突时的启动失败清理及原有路由保留。
- 已发布 AOT 程序无参数读取临时配置，与仅监听本机的测试节点进行 AES-GCM UDP 回环；不使用真实 Token。

实际执行情况以对应 CI run 和测试报告为准，不将计划项目自动记为通过。物理网络变化、跨节点性能、睡眠恢复和正式分发仍待后续验收。

## 本地原生构建

需要对应架构的 Mac、.NET 10 SDK 和 Xcode Command Line Tools；原生桥接库由项目使用 `xcrun clang` 自动构建。

```sh
dotnet publish ProxyNetworker.Client/ProxyNetworker.Client.csproj \
  -c Release -r osx-arm64 --self-contained true -p:PublishAot=true \
  -o publish/client-osx-arm64
```

Intel 原生构建使用 `osx-x64`。本阶段在对应架构原生验证，不使用 Windows 交叉 AOT 作为 Mac 原生验收证据。运行环境以实际 CI 记录为准，不推导对所有 macOS 版本的兼容承诺。
