# ProxyNetworker 四节点虚拟局域网测试与待改进报告

日期：2026-11-01

## 0. 当前状态

本报告记录四节点实测后发现的问题。后续代码状态如下：

- `v1.0.1` 已包含 UDP 网状路由、多跳转发、链路状态通告、CI 测试和跨平台 Release 工作流。
- `v1.0.2` 纳入 Windows `WSAECONNRESET` 修复、直连故障冷却、`RelayRequired`、同机自动 relay-only 和 `NetworkDefaults.AdvertisedHost`。
- Windows x64 AOT、Linux 服务端发布、完整解决方案构建和 5 项自动测试均已通过。
- QUIC 仍为服务端可靠中继，P2P mesh 未实现。

## 1. 测试目标

使用 4 个节点组建虚拟局域网，并由 `<server-public-ip>` 作为服务端、虚拟网络节点和公网访问入口：

| 节点 | 公网地址 | 内网地址 | 虚拟地址 | 客户端位置与模式 |
|---|---|---|---|---|
| Server + Node | `<server-public-ip>` | `<server-lan-ip>` | `<vn-ip-server-node>` | `<client-install-path>`，AOT，smart |
| Node 37 | `<node37-public-ip>` | `<node37-lan-ip>` | `<vn-ip-node37>` | `<client-install-path>`，AOT，smart |
| 树莓派 | 家庭公网出口 | `<pi-home-lan-ip>` | `<vn-ip-pi>` | `<client-install-path>`，AOT |
| Windows 本机 | 家庭公网出口 | `<windows-home-lan-ip>` | `<vn-ip-windows>` | `<client-install-path>`，AOT，relay-only |

网络名称：sky-mesh-20261101
网段：`<virtual-subnet>/24`
网关：`<virtual-gateway>`
协议：UDP（智能直连/中继）
服务端口：`<data-port>/UDP`

## 2. 已验证能力

1. 4 个节点均成功创建 TUN/WinTun 接口并加入同一虚拟网络。
2. 节点间虚拟 IP 路由和双向数据面基本可用。
3. 智能路径能够发现候选地址并选择 direct 路径，例如：
   - Windows -> <node37> direct
   - Windows -> 树莓派 direct
   - <node37> -> 树莓派 direct
   - <node37> -> <server-node> direct
4. 路径探测失败后具备回退 relay 的设计；Windows 日志中曾出现 direct 超时后重新选择 relay。
5. <server-node> 作为公网入口的端口映射验证成功：
   - `<published-port-1>/TCP` -> `<vn-ip-windows>:<http-port>`（Windows HTTP）
   - `<published-port-2>/TCP` -> `<vn-ip-node37>:<http-port>`（Node 37 HTTP）
   - `<published-port-3>/TCP` -> `<vn-ip-pi>:22`（树莓派 SSH Banner）
6. Windows AOT 客户端修复后，其他节点可通过 relay 稳定访问 Windows 服务。
7. 后续新增的 UDP mesh 路由支持基于 RTT、抖动、丢包率和跳数计算最低成本路径，并支持 `A -> C -> B` 多跳转发、8 跳上限、防环和服务端中继兜底。实现和边界见 `docs/MESH_ROUTING.md`。

## 3. 关键缺陷与改进项

### P0：Windows UDP 接收循环遇到 WSAECONNRESET 会永久退出

现象：Windows 收到 direct path 探测产生的 ICMP port-unreachable 后，UdpClient.ReceiveAsync 抛出 SocketException(10054)，原实现记录错误并退出 receive loop。进程仍存在，但数据面已停止。

影响：Windows 节点表面在线，实际无法继续收发虚拟网流量。

状态：已修复并纳入 `v1.0.2`。VirtualNetworkTunnel.cs 对 ConnectionReset/ConnectionRefused 继续接收，并将相关直连路径进入冷却期，期间优先使用服务端中继。Windows x64 AOT 发布、完整 Release 构建和三节点 UDP 集成测试通过。

建议：增加 Windows 平台定向回归测试，并把该异常作为可观测的路径质量事件，而不是仅忽略。

### P0：智能路由无法识别“探测成功但数据面失败”的非对称故障

现象：<node37> 和 <server-node> 都曾选中到 Windows 的 direct 路径，PathProbe 也成功，但实际 TCP 连接超时。改为 relay-only 后立即恢复。

根因：当前智能路由以 UDP PathProbe/ACK 判断链路健康，探测由客户端内部处理，不经过主机防火墙和最终 TCP 服务。因此它能证明 UDP 隧道存在，但不能证明 TUN 数据面、主机防火墙和业务端口可用。

影响：会出现“系统显示路径 healthy，但真正业务不可达”的误判。

建议：
- 增加 RelayRequired 状态广播，让节点可主动通知其他节点“不要向我建立 direct”。
- 在连续 direct 数据发送失败、WSAECONNRESET、TUN 投递失败后自动进入 relay-only 冷却期。
- 增加业务级 PathHealthAck，或对实际虚拟包进行概率性端到端确认。
- UI 显示 direct/relay、RTT、丢包、最近失败原因和自动降级次数。

当前进展：WSAECONNRESET 和探测发送失败已触发单节点或全局 direct 冷却，并自动回退 relay。客户端会通过 `RelayRequired` 通知服务端停止向其他节点广播其直连候选。防火墙或业务端口导致的“PathProbe 成功但 TCP 业务失败”仍需业务级健康确认。

### P0：防火墙开箱配置不足

现象：<node37> 的 UFW 和 Windows Firewall 默认拦截虚拟网入站。表现与路由故障完全一致。

已验证规则：
- Linux：`ufw allow from <virtual-subnet>/24`
- Windows：入站允许 `RemoteAddress <virtual-subnet>/24`

建议：
- 客户端加入网络后检测本机防火墙状态，并给出精确的待执行命令。
- 不默认开放所有网络，只允许指定虚拟网段。
- 增加“本机入站自检”，从客户端内部模拟一次 TUN 入站和回包。

### P1：Server + Node 同机节点的 direct 路径不稳定

现象：<server-node> 既运行 relay server 又运行 node，向其他公网节点 direct 时会经过本机公网/NAT 回环，容易出现不对称路径或 malformed datagram。

建议：默认把“同机 Server + Node”节点标记为 relay-only，仅由本机 relay 收发，避免 NAT hairpin。

当前修复：客户端检测到数据服务端地址属于本机接口时会自动使用 relay-only，通过 `RelayRequired` 让服务端不再广播其直连候选，并记录原因。已纳入 `v1.0.2`。

### P1：旧 AOT 发布物与新协议版本不一致

现象：树莓派旧 AOT 客户端把 Base64 authentication key 当作原始字符串，报 “must be 32 bytes”；protocol 版客户端正常。

影响：同样是 AOT 发布物，不同构建时间可能导致协议行为不同。

建议：
- 发布物内加入 ProtocolVersion 和 BuildSha。
- Server 与 Client 在握手时检查版本兼容性。
- CI 为三平台同时发布 AOT 产物，禁止手工混用旧包。
- 增加 direct-mode auth-key 的 Base64 回归测试。

### P1：管理端“新建内网穿透”会一直停留在创建中

现象：表单提交后按钮持续“创建中…”，数据库没有写入记录，也没有错误提示。

测试绕过：备份数据库后，使用事务直接写入 3 条临时 PortTunnelDefinitions 和对应 Token，完成端到端验证。

建议：
- API 请求增加 AbortController 超时。
- 后端异常必须返回 request id，前端恢复按钮并展示可操作错误。
- 增加端口隧道创建的集成测试和数据库锁检测。

### P1：Token 模式的服务端地址从请求 Host 推导

现象：客户端通过 SSH 隧道访问 `<loopback>:<management-port>` 时，服务端配置返回 `serverHost=<loopback>`，导致客户端把它自己的 loopback 当成 `<data-port>/UDP` 服务端。

临时解决：在树莓派增加 Host 重写代理，把 Host 改为 `<server-host>:<management-port>`。

当前修复：新增 `NetworkDefaults.AdvertisedHost`。配置后服务端优先向客户端下发该主机，不再依赖请求 Host 或 `X-Forwarded-Host`。已纳入 `v1.0.2`。

建议：
- 配置中显式提供 PublicEndpoint/AdvertisedHost。
- 客户端增加 --server-host 覆盖参数。
- 服务端不要依赖反代/隧道后的 Request.Host。

### P1：节点生命周期缺少正式服务管理

现状：<server-node> 和 <node37> 使用 nohup 后台运行；Windows 使用隐藏进程；树莓派仍依赖 VelaShell 监控任务和临时代理。

影响：
- 重启后不会自动恢复。
- 日志、PID、健康检查分散。
- VelaShell 的长任务会占用同一 SSH 执行通道。

建议：
- Linux 提供 systemd unit：启动、停止、重启、状态、日志、自动重连。
- Windows 提供服务模式或托盘控制器。
- 客户端内置 pid/log/health 子命令，统一 run.sh 和 PowerShell 的行为。

### P2：缺少面板级路径可观测性

建议在虚拟网络详情页展示：
- 在线节点和真实虚拟地址
- direct/relay 当前路径
- RTT、抖动、丢包
- 最近一次成功和失败时间
- relay-only 原因
- 防火墙自检结果
- 客户端版本、AOT 标识、构建 SHA

### P2：临时测试数据和密钥清理流程需要自动化

建议提供资源标签（例如 test=true）、到期时间、自动清理任务。当前测试 Token 和端口隧道已人工清理，但流程应产品化。

## 4. 建议优先级

1. Windows WSAECONNRESET 修复、`AdvertisedHost` 和三平台 AOT 已完成并纳入 `v1.0.2`。
2. RelayRequired、同机自动 relay-only 和直连故障冷却已完成并纳入 `v1.0.2`。
3. 增加 Linux/Windows 防火墙自检与精确引导。
4. 修复端口隧道创建接口挂起和前端超时。
5. `AdvertisedHost` 已完成；后续可增加客户端 `ServerHostOverride` 作为额外覆盖入口。
6. 提供 Linux systemd 与 Windows 服务化部署。
7. 增加路径质量 UI 和四节点 CI/集成测试。

## 5. 本次清理状态

- 已删除 3 条临时端口隧道定义和 Token。
- 已停止 3 个临时端口隧道客户端。
- 已停止 <node37>、Windows 临时 HTTP 服务。
- 已保留虚拟网络 sky-mesh-20261101 和 4 个节点。
- 已保留数据库测试前备份：`<database-backup-path>`
