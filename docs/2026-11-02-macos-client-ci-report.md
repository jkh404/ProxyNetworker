# macOS 命令行开发预览验证

复核日期：2026-11-02，Asia/Shanghai。

结论：**G1-CI 通过，macOS CLI 开发预览完成。未做用户 Mac 真机验收，不宣称正式支持或真实性能达标。**

## 程序与工作流身份

- 基线：`v1.1.0` / `c5df9bfdd3879bed73b877f0dcb9f15c03cf9ab3`。
- 隔离验证分支：`codex/mac-client-ci-20261102-124549`。
- 实际受测提交：`89171a2711fbcab075761114244a3b0aa01a0f35`。
- [最终 CI run 41453509337](https://github.com/jkh404/ProxyNetworker/actions/runs/41453509337)：四项 job 全部成功。
- 工作流：[macos-client.yml](../.github/workflows/macos-client.yml)；仅构建预览和上传 CI artifact，不发布 Release。
- 用户工作分支、普通暂存区及原有未提交数据面/管理页修改未纳入验证提交，也未被回退。

## 交付范围

- 原生 utun 后端、动态 `utunN` 名称、网络序 4 字节 IPv4 地址族头。
- Darwin 非阻塞 socket、取消、句柄归属、`writev` 报文边界与失败清理。
- C 桥接库处理 Darwin `ioctl/fcntl` 变参，避免 Apple Silicon ABI 错配。
- 配置前拒绝接口网段重叠；仅添加虚拟子网路由，不接管默认路由或全局 DNS。
- 严格检查 Darwin `route` 命令结果；失败只关闭新 utun，不删除其他接口或路由。
- 既有 `appsettings.json` 无参数启动、Token 锁与共享协议保留；Mac 日志使用 Application Support。
- 可选后台脚本、停止和回退说明；无默认安装的 `launchd` 服务。
- 双架构 Native AOT tar 包、SHA256、源提交与预览元数据。

## 实际环境与结果

| 项目 | Apple Silicon | Intel |
| --- | --- | --- |
| RID / runner | `osx-arm64` / `macos-14` | `osx-x64` / `macos-15-intel` |
| 实际系统 | macOS 14.8.9 / 23J631 | macOS 15.7.9 / 24G830 |
| Xcode | 16.2 / 16C5032a，显式选择 | 16.4 / 16F6 |
| .NET SDK / Runtime | 10.0.403 / 10.0.14 | 10.0.403 / 10.0.14 |
| 核心测试 | 189 通过，6 个 Linux 专用测试跳过 | 189 通过，6 个 Linux 专用测试跳过 |
| 客户端与冒烟工具 AOT | 通过 | 通过 |
| 解压后的包内 AES-GCM 自检 | PASS，系统 AesGcm 后端 | PASS，系统 AesGcm 后端 |
| utun / MTU 1400 配置 / ICMP 本机协议栈 | 通过 | 通过 |
| 相同网段冲突拒绝、原路由保留 | 通过 | 通过 |
| `/32` 本机地址启动 | 通过 | 通过 |
| 空闲读退出、接口与所属路由清理 | 通过，关闭 89 ms | 通过，关闭 76 ms |
| 包内 AOT 客户端无参数读取临时配置 | 通过 | 通过 |
| 包内客户端 AES-GCM UDP 回环与 SIGINT 退出 | 3 次 ping 通过 | 3 次 ping 通过 |

测试使用专用基准地址、本机 UDP 测试节点和临时随机密钥，不接入现网服务，不使用真实 Token。测试配置在结束时删除；CI 回环不是跨物理节点互通。

同一受测提交的 Windows 回归为 189 通过、6 跳过；Linux 为 204 全部通过。Linux 原生理论用例展开后计数不同，不能把跳过的 6 个方法简单加到其他平台的通过数上。

本地 Windows 工作树完整回归为 193 通过、6 跳过，其中包含未纳入隔离提交的 4 个用户用例；不能把本地计数冒充 CI 结果。Mac 专项便携测试 46/46；本地完整 Release 和冒烟工具构建均为 0 警告、0 错误。

## 预览产物

成功 run 的 artifact：

- `macos-evidence-osx-arm64`，ID `13145602188`。
- `macos-evidence-osx-x64`，ID `13146302032`。

包在 artifact 的 `packages/` 下，保留期 14 天；不是现有正式 Release 的资产。包内为程序、原生桥接库、示例配置、`run.sh`、README、LICENSE 和 `PREVIEW.json`。两份包均已下载复核文件完整性、Mach-O CPU 类型、JSON 配置和受测提交。

```text
3ded7789c1ab6d8c17b9479e8a403820440e239746ad17b8ccc97637fcd8103d  ProxyNetworker.Client_osx-arm64_preview.tar.gz
644f8c20727da20001750c6c5596ed03fd4e7eda7a5cdb4086798c506c7e1f01  ProxyNetworker.Client_osx-x64_preview.tar.gz
```

源码快照和两份解压包均通过 Gitleaks 扫描。公开报告不包含现网身份、Token、密钥或原始部署日志。完整使用、权限与回退步骤见 [Mac 说明](../ProxyNetworker.Client/README.macOS.md)。

签名为 ad-hoc，`codesign --verify --strict` 通过；不是 Developer ID 签名，也未公证。`PREVIEW.json` 明确记录 `physicalMacValidation:false`、`developerIdSigned:false`；未要求关闭 SIP 或 Gatekeeper。

## 本次发现与修复

1. Darwin `route` 写入失败可以返回退出码 0。现在同时检查标准输出、标准错误和明确成功格式；网段重叠在配置前拒绝，失败不删除原路由。
2. `route` 的成功输出不保留 CIDR 后缀。按实际原生输出校验，保留“退出零但失败”回归用例；`/32` 不再重复添加本机路由。
3. ARM 镜像默认 Xcode 15.4 无法链接当前 .NET AOT 加密库所需的 Swift 符号。显式选择镜像已安装的 Xcode 16.2，最终 ARM 构建、系统加密和数据面测试通过。
4. 既有 Mesh 测试“取空闲 UDP 端口后立即释放”的方式可能重复选中端口。测试改为分别保留端口到节点绑定前，修复本次 ARM 回归中暴露的占用冲突；不修改生产数据面行为。
5. 冒烟测试直接运行最终 tar 包解压的程序，避免只验证发布目录而遗漏打包依赖。

失败 run 仍保留为排查证据：`41447625425`、`41450414568`、`41451555824`、`41452446180`。它们不计入通过结果，也不能据这些旧 run 推断最终 ARM job 失败。

## 未验证与后续边界

- MAC-04 / G1-REAL 暂缓：无用户 Mac 真机，不做跨 Windows/Linux 的真实业务、实际 Token 接入、TCP/UDP Port Tunnel 业务、Direct/Mesh/Relay 故障恢复、满 MTU 物理路径、吞吐、睡眠唤醒或人工交互验收。
- 当前只实现普通 IPv4 utun 数据路径；不把 Linux 的 GSO/GRO 或批量 syscall 能力搬到 Darwin。
- QUIC 依赖不随本预览打包，不承诺 Mac QUIC 可用，也不静默切换协议。
- 实际 CI OS/架构不等同于正式最低兼容版本；后续正式 Mac 发行仍需真机、签名和公证决策。
- ARM 的 `macos-15` / `macos-26` 尝试未及时分配 runner，本次使用 `macos-14` 原生完成验证。该镜像已被上游标记 deprecated，后续需迁移并重新运行原生检查，不能假设 runner 永久可用。
- G1-CI 通过后可启动 Avalonia；本轮不创建 Avalonia 或 Android 工程，不修改正式 Release 流程，不打新版本标签。
