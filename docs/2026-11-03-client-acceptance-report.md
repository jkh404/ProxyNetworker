# Avalonia 客户端实测记录

日期：2026-11-03，Asia/Shanghai。状态：**Windows x64 与 Android x64 模拟器的已列用例通过，包括最终包后台恢复；手机及其他桌面平台不计入验收**。

本轮保留独立 CLI，验证新建的 Avalonia 桌面及 Android 客户端。本报告记录 `v1.2.0` 发布前的本地验证，远程构建与发布以对应标签的 Actions 记录为准。

## 环境与边界

- PC：Windows x64 本机，真实 Wintun 接口，普通权限 UI 与独立网络 Worker。
- Android：Android 16 / API 36、x64 模拟器，真实系统 VPN 授权、`VpnService` 和内核 TUN；不是手机真机测试。
- 使用项目实际管理服务、中继、临时数据库及临时 Token，未连接生产管理服务。
- Virtual Network 使用 IPv4、UDP、AES-GCM、MTU 1400，只路由隔离测试网段。
- TCP/UDP Port Tunnel 通过隔离的本机 WebSocket 管理服务转发；这部分测试不宣称测试了公网 TLS。
- 对端是测试工具的用户态 ICMP 回显节点。客户端侧使用真实 OS 网络栈和虚拟接口，不是内存 TUN 模拟。
- 不将本机回环或模拟器结果外推为公网性能、Android ARM64 真机、Linux GUI 或 macOS 真机验收。

## 已有实际证据

| 项目 | 结果 | 本地原始证据 |
| --- | --- | --- |
| PC 虚拟网普通 ICMP | 10/10，0% 丢包 | 前序工具输出 |
| PC 1400 字节 IPv4 DF 包 | 10/10，0% 丢包 | `pc-mtu.txt` |
| PC TCP/UDP 端口转发 | 256 KiB SHA-256 一致；20 × 1200 字节 UDP 一致 | `pc-ports.json` |
| PC 正常断开 | 测试地址、路由均清除 | `pc-cleanup.json` |
| PC 管理员授权拒绝 | 显示明确错误，没有静默提权 | `pc-auth-denied.json` |
| PC 普通权限复用原管理员锁 | 修复后 TCP/UDP 实际转发通过 | `pc-port-only-fixed.json` |
| PC 界面被强制结束 | Worker 退出，TCP/UDP 监听均释放 | `pc-crash-cleanup.json` |
| Android VPN 授权拒绝 | 显示错误，不创建 TUN | `android-consent-denied.json` |
| Android 创建 VPN | `tun0`，UDP、AES-GCM、MTU 1400，两个端口隧道就绪 | `android-started-ui.json` |
| Android 普通 ICMP | 10/10，0% 丢包 | `android-ping.txt` |
| Android 1400 字节 IPv4 DF 包 | 10/10，0% 丢包 | `android-mtu.txt` |
| Android TCP/UDP 端口转发 | 256 KiB SHA-256 一致；20 × 1200 字节 UDP 一致 | `android-ports.json` |
| Android 退到后台 | 5/5 ICMP；TCP/UDP 完整性校验通过 | `android-background-*` |
| Android 横屏期间收发 | 3/3 ICMP | `android-landscape-ping.txt` |
| Android 系统设置断开 | 内核确认 `tun0` 不存在，UI 回到未连接 | `android-system-disconnect.json` |
| Android 忘记 VPN、撤销授权 | 接口释放，下次连接重新显示系统授权 | `android-permission-revoked.json`、`android-consent-required-again.json` |
| Android 应用内断开 | 内核确认 `tun0` 不存在 | `android-app-disconnect.json` |
| Android Release 安装 | 非 Debug APK，保留原测试配置，真实 VPN/TUN 启动 | `android-release-started-ui.json`、`android-release-tun.json` |
| Android Release 数据面 | 普通 ICMP 10/10、满 MTU 10/10，TCP/UDP 完整性通过 | `android-release-ping.txt`、`android-release-mtu.txt`、`android-release-ports.json` |
| PC Release 端口转发 | 256 KiB SHA-256 一致；20 × 1200 字节 UDP 一致 | `pc-release-ports.json` |
| PC Release 关闭到后台 | 窗口隐藏、Worker 保留，后台 TCP/UDP 仍通过 | `pc-release-background.json`、`pc-release-background-ports.json` |
| PC Release 再次启动 | 第二进程返回 0，原窗口恢复，GUI 仍只有一个 | `pc-release-single-instance.json` |
| Android 真正划掉任务 | Activity 任务不存在，前台服务/TUN 保留，ICMP 5/5 及 TCP/UDP 通过 | `android-task-dismissed-service.json`、`android-task-dismissed-ping.txt`、`android-task-dismissed-ports.json` |
| Android 锁屏及强制 Doze | `mWakefulness=Dozing`、deep state `IDLE`，ICMP 10/10 及 TCP/UDP 通过 | `android-final-screen-off-doze.json` |
| Android 系统回收恢复实验 | 受控 SIGKILL 后 90 秒未自动恢复，**未通过**；随后手动重连及 TCP/UDP 通过 | `android-sticky-recovery.json`、`android-manual-recovery-*` |
| Android 新后台策略下的系统断开 | TUN 清除，持久化连接意图为 false | `android-background-system-disconnect.json` |
| Android 通知策略入口 | 原生通知通道关闭/开启后，返回应用正确显示状态，已恢复原开启值 | `android-native-notification-settings.json`、`android-notification-disabled-ui.json`、`android-notification-restored-ui.json` |
| Android 最终包通知更新 | 4 秒观察内，状态不变时 `mUpdateTimeMs` 不变，未继续按 500 毫秒重复更新 | `android-notification-throttle.json` |
| Android 通知栏断开 | 点击真实通知动作，TUN 与前台服务均移除，连接意图为 false | `android-notification-stop.json` |
| Android 系统 Force Stop | 10 秒逐秒确认进程与 TUN 均不复活；重新打开应用仍为未连接 | `android-force-stop.json`、`android-after-force-stop-reopen-*` |
| Android 最后清理检查 | TUN 不存在、连接意图 false，原测试配置语义完全保留 | `android-final-background-cleanup.json`、`android-final-private-state.json` |
| PC 最终托盘包 | 隐藏窗口时 TCP/UDP 仍通过，第二实例唤回原窗口 | `pc-tray-fixed-background-ports.json`、`pc-tray-fixed-activation.json` |
| PC 原生托盘退出 | 实际菜单有完整条目，点击退出后 GUI 返回 0，所属 Worker 退出 | `pc-final-tray-exit.json` |
| Android 最终设置页滚动 | 加入后台设置后仍可滚动到最后一个端口 Token，页头和底部导航保持固定，无内容穿透 | `android-final-background-settings.png`、`android-final-settings-scrolled*` |
| Android 独立会话宿主原型 | 首次进程回收 3.13 秒恢复，随后无 Activity 三轮为 3.75/6.96/18.27 秒；新 PID/TUN、满 MTU 与 TCP/UDP 均通过 | `android-session-host-recovery-1*`、`android-session-host-background-*` |
| Android v1.2.0 最终包回收恢复 | 三轮无 Activity 恢复耗时 3.33/6.12/18.16 秒；每轮新 PID/TUN、满 MTU 10/10、TCP/UDP 完整性通过 | `android-v120-final-recovery-*` |
| Android v1.2.0 锁屏 Doze | 确认 deep IDLE 与 Dozing，满 MTU 10/10、TCP/UDP 通过 | `android-v120-final-doze.json` |
| Android v1.2.0 通知停止与撤销授权 | TUN 与两个服务均清除；撤销后连接意图为 false，再次连接弹出授权 | `android-v120-notification-stop.json`、`android-v120-revoke.json` |
| Android v1.2.0 重连与 Force Stop | 连续三轮连接/断开通过；强制停止后逐秒检查 10 秒不复活，重开显示未连接 | `android-v120-rapid-reconnect.json`、`android-v120-force-stop*` |
| Android v1.2.0 最后清理 | TUN、两个服务均不存在，连接意图 false，原配置语义完全保留 | `android-v120-final-private-cleanup.json` |
| PC v1.2.0 最终包 | TCP/UDP、后台、第二实例唤回、断开后 Worker 退出及窗口关闭均通过 | `pc-v120-final-*` |

原始证据、测试配置及凭据只保留在被 Git 忽略的 `artifacts/acceptance-20261103-01/`。
公开说明不包含临时 Token、会话密钥、数据库或未脱敏配置。

再授权后立即发起的一次 3 包冷启动探测为 2/3，保留于
`android-reauthorized-ping.txt`，不改写或混入稳态结果。
界面的“运行中”表示本地工作进程及接口运行，不保证某个远端节点已经可达。

## 实测发现与修复

1. Debug APK 原先依赖 IDE Fast Deployment，单独安装缺少程序集。现改为嵌入程序集。
2. 手写清单覆盖了 C# 服务属性，导致前台服务类型及 VPN 绑定权限丢失。声明统一放入清单，并在合并后执行构建校验。
3. Android 活动宿主已经处理安全区，移动 Page 再处理一次会产生重复留白。Android 页面改为使用宿主安全区。
4. 系统字体回退在当前镜像上存在中文缺字。随应用提供固定版本的 Noto Sans CJK SC，并附原始 SIL OFL 许可证。
5. 连接按钮自定义内容的文字颜色与 Arc 按钮默认配色不匹配。文字改为继承按钮前景色。
6. Android 前台服务增加通知授权流程，启动异常改为失败状态并清理资源，不直接导致进程崩溃。
7. 管理员创建的 Token 锁可能只允许普通用户读取。普通用户改用同一个文件的只读独占句柄；不绕开全局 Token 互斥。
8. 端口隧道增加实际有效载荷计数，控制帧不计入流量；会话汇总包括虚拟网络与端口隧道。
9. 前台 VPN 与 Activity 生命周期分离，明确 `stopWithTask=false`；保存用户连接意图，系统投递 sticky 重启时只恢复仍被请求且授权仍有效的会话。主动断开/撤销授权先清除意图。
10. 增加真实通知、电池策略及系统设置入口；从系统设置返回时刷新状态，界面重建时恢复已有服务状态。不自动修改电池豁免、不注册开机启动、不启用始终在线 VPN，也不持有无限期唤醒锁。
11. 前台通知从每 500 毫秒重复更新改为只在连接状态文字变化时更新，并使用 `OnlyAlertOnce`。最终包已确认通知更新时间在稳定状态下不变，通知断开动作也通过。
12. UI 验收工具发现 `uiautomator` 可能报错但返回 0。现使用每次独立的 XML 路径并验证成功消息；4 个脚本用例覆盖空闲失败、错误路径、无效 XML 和新鲜成功结果，不允许误读旧快照。
13. PC 托盘实测发现空菜单：Arc 基础主题没有为托盘使用的 Avalonia 内置菜单提供模板。桌面宿主单独加载 Avalonia Fluent 基础主题，Arc 业务组件保持不变，Android 不引入 Fluent。新增菜单行尺寸/文字回归，并用最终发布包实际操作托盘退出，确认 GUI 与 Worker 均正常退出。
14. 原 `VpnService` 同时承载会话并由系统绑定，受控回收后只留下无进程的服务记录。改为同进程的私有前台 `NetworkClientService` 持有会话并绑定系统 VPN 设备，最后构建完成三轮无 Activity 自动恢复及数据面复测；没有增加看门进程。旧服务的延迟回调不会覆盖新宿主状态，释放绑定放在清理的 `finally` 中。

## 当前源码回归与产物

- .NET 测试：Core 194 通过、6 项 Linux 原生调用测试因本机是 Windows 而跳过；共享运行时 22 通过；UI/单实例/菜单 18 通过。合计 **234 通过、6 跳过**。
- 当前解决方案 Release 构建：0 警告、0 错误。
- Android UI 工具契约测试 4/4、回收工具安全契约 6/6、APK 校验工具测试 8/8；模拟工具结果不计入真实 Android 数据面通过数。Python offload 工具测试 18 通过、3 个 Linux 专用测试跳过。
- Windows x64 托管自包含 GUI/Worker 发布通过；Release 文件与依赖中未发现 Spyglass、本地配置或凭据。最终实际托盘测试见上表。
- Android x64、ARM64 分别完成 Release APK 发布与包检查，均为非 Debug、内嵌程序集、单一对应 ABI；归档及程序集存储名称检查未发现 Spyglass 或配置/数据库。ARM64 仅构建，不表示手机实测。
- 本地 v1.2.0 x64 最终 APK SHA-256：`07FCF2B440B4DA33EC93D44D99B45FE341EC4B440BE9E8C0A68E8DB868891F63`。
- 本地 v1.2.0 ARM64 最终 APK SHA-256：`DB86AEAF6A21D2B87EE62DE6CEDFB3BC3E0E84C27949D90BF9D90F5E52BAA1A2`。
- 最终 x64 APK 的三轮恢复、满 MTU、TCP/UDP 校验以及锁屏/Doze 收发再次通过；回收工具先核对安装包与本地哈希、模拟器身份、目标进程身份和新 TUN 索引。以上为本地受测包哈希，不冒充 CI 开发签名发布包哈希。
- CLI Windows x64 Native AOT 发布通过；本轮未重新取得该 AOT 包在管理员上下文中的加密自检 PASS，不将构建成功等同于此项运行验收。
- 本地检查不代替远程 CI。发布工作流从标签干净检出构建 CLI、服务端、Windows GUI 和双架构 Android，并生成统一校验文件；GUI/Android 仍为开发预览范围。

## 后台恢复限制

退到桌面、Activity 任务被划掉、锁屏与模拟器强制 Doze 的网络测试已经通过，不能据此扩展为“任意系统回收后必然保活”。
早期单服务实现通过核对包名后对测试进程执行 SIGKILL；90 秒内没有新进程或 TUN，系统服务记录仍显示 sticky。保留该失败样本，不将其改写为成功。
随后拆分同进程的前台会话宿主与系统 VPN 设备绑定，原型四轮及最终包三轮自动恢复通过。恢复期间并非无缝连接，系统退避会影响耗时，不能承诺任意系统固定时限或永不被杀。
不使用额外常驻看门进程、不强行修改电池策略、不绕过 Force Stop。手机厂商策略、Wi-Fi/蜂窝切换和真实耗电仍待设备测试。

## 收尾清单

- [x] 完成不支持 QUIC（不创建 TUN）、错误密钥（0/3 ICMP）及原配置恢复的实际测试。
- [x] 在最终构建上重新执行正向收发和清理检查。
- [x] 完成当前源码的单元回归、桌面发布包及 Android 双架构构建；平台跳过项如上单列。
- [x] 检查 Release 文件、依赖/程序集存储名称，不包含已发现的 Spyglass、生产配置或测试凭据；不作为无限范围的安全审计声明。
- [x] 同步路线图、构建入口及 CI；远程 CI 仍未因本轮未提交源码而执行。
- [x] 完成最终后台版本的通知停止、Force Stop 不复活、设置返回及通知更新频率检查。
- [x] 排查受控 SIGKILL 后的自动恢复缺口，以最终包三轮实际恢复和收发记录验证；不外推到其他系统。
- [x] 清理本轮后台进程、模拟器与临时监听，确认现有配置未被修改。

GUI 和 Worker 已停止，Android TUN 和两个服务已清除，电池/Doze 状态已恢复。隔离测试宿主、服务端、模拟器及管理监听已停止，见 `v120-final-lab-cleanup.json`。原 Android 测试配置语义未变，原有现网 CLI 进程保留，未修改其配置。原始测试证据继续保留在忽略目录。

最后的停止检查按异步生命周期等待服务清理完成，而不是只凭 TUN 消失断言两个服务同步消失。一次 Force Stop 后过早调用 UI 自动化出现空根节点，工具按失败处理，没有读取旧快照；随后重新执行完整的十秒检查及新鲜 UI 状态验证通过。

Mac 真机验收继续按用户约定暂缓，不用本轮 Windows/Android 结果代替。
