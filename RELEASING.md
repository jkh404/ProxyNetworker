# 发布版本

项目通过 `.github/workflows/release.yml` 自动构建并发布 GitHub Release。工作流不需要额外配置 Secret，使用仓库自带的 `GITHUB_TOKEN` 上传 Release 资产。

发布前会先运行核心路由、UDP 多跳、共享运行时、UI 和发布包校验工具测试；任一必需构建或测试失败时不会生成或覆盖 Release。

## 发布步骤

1. 确认 `master` 已同步并且构建通过。
2. 创建语义化版本标签，例如 `v1.0.0`。
3. 推送标签，GitHub Actions 会自动构建并创建 Release。

发布前检查所有待推送提交及暂存文件，不要提交现网配置、Token、密钥、数据库、回滚状态或原始部署日志。公开报告中的节点身份、地址和目录应脱敏。发布包从标签的干净检出构建，不使用本地现网 `publish/` 目录。

版本说明放在 `docs/releases/<tag>.md`，例如 `docs/releases/v1.1.0.md`。工作流优先使用该文件作为 Release 说明；不存在时才使用 GitHub 自动生成的说明。

```bash
git switch master
git pull origin master
git tag -a v1.0.0 -m "ProxyNetworker v1.0.0"
git push origin v1.0.0
```

带连字符的标签会发布为预发行版本，例如：

```bash
git tag -a v1.1.0-rc.1 -m "ProxyNetworker v1.1.0-rc.1"
git push origin v1.1.0-rc.1
```

## 自动生成的资产

| 资产 | 内容 |
| --- | --- |
| `ProxyNetworker.Client_win-x64_<tag>.zip` | Windows x64 Native AOT 客户端 |
| `ProxyNetworker.Client_linux-x64_<tag>.tar.gz` | Linux x64 Native AOT 客户端及 `run.sh` |
| `ProxyNetworker.Client_linux-arm64_<tag>.tar.gz` | Linux ARM64 Native AOT 客户端及 `run.sh` |
| `ProxyNetworker.Server_linux-x64_<tag>.tar.gz` | Linux x64 自包含服务端、配置文档和 MsQuic 运行库 |
| `ProxyNetworker.App_win-x64_preview_<tag>.zip` | Windows x64 托管自包含 GUI 与独立 Worker，开发预览 |
| `ProxyNetworker.App_android-arm64_development-signed-preview_<tag>.apk` | Android ARM64 开发签名预览，未做手机真机验收 |
| `ProxyNetworker.App_android-x64_development-signed-preview_<tag>.apk` | Android x64 开发签名预览，模拟器测试范围 |
| `SHA256SUMS.txt` | 所有发布资产的 SHA256 校验值 |

GitHub 还会自动附带对应标签的源码 zip 和 tar.gz。

## macOS 开发预览

macOS 当前使用独立的 `.github/workflows/macos-client.yml`，不加入上述正式发布资产，也不因预览 CI 成功自动发布新版本。工作流分别在 Apple Silicon 和 Intel runner 原生构建 AOT 客户端，生成完整 tar 包及 SHA256，并对解压后的程序执行加密自检和隔离 utun 回环测试。

成功 run 的 `macos-evidence-osx-arm64` / `macos-evidence-osx-x64` artifact 中，`packages/` 包含预览包和 `SHA256SUMS.txt`，其余文件记录环境、源提交与自动测试结果；保留期为 14 天。使用范围、权限、签名状态和回退方式见 [macOS 客户端说明](ProxyNetworker.Client/README.macOS.md)。

预览仅为 ad-hoc 签名，未做 Developer ID 签名、公证或用户 Mac 真机验收。后续正式 Mac 发行需要另行决定验收范围、签名与公证方案，不直接修改正式发布流程的资产数量来冒充平台验收。

## GUI 开发预览

日常 Avalonia 桌面与 Android 构建使用 `.github/workflows/client-apps.yml`；从 `v1.2.0` 起，Release 工作流也从标签的干净检出构建上述三份**预览**资产。完整 Release 共有七份安装包及一份校验文件；生成校验值后再次执行校验，任一缺失时不发布。Windows 产物为托管自包含 GUI 和独立 Worker，不是 GUI Native AOT；Android 按 x64/ARM64 分别生成开发签名 APK，不能作为正式签名或手机真机验收声明。

APK 校验覆盖单一 ABI、非 Debug、版本、内嵌程序集、服务标识和已知禁止内容；双服务的权限和前台类型在合并清单阶段另行检查。Android `ApplicationVersion` 是单调递增的整数版本码，当前 `v1.2.0` 为 `10200`，后续发布时必须同步递增。临时 runner 的开发签名不保证跨构建升级，保留原始导入配置后再处理签名不一致，卸载会清除应用私有数据。正式签名和商店分发另行安排，不上传签名私钥。构建、已实测范围及后台限制见 [GUI 说明](ProxyNetworker.App/README.md)。

## 重新构建已有标签

在 GitHub 页面依次进入 **Actions** > **Release** > **Run workflow**，输入已经存在的标签，例如 `v1.0.0`，然后运行工作流。

如果 Release 已存在，工作流会覆盖同名资产；不会重复创建 Release。手动运行工作流不会自动创建标签，因此输入标签必须已经推送到仓库。

## 权限设置

工作流声明了 `contents: write` 权限。如果发布步骤返回 `403`，请检查仓库的 **Settings** > **Actions** > **General** > **Workflow permissions**，允许 GitHub Actions 使用读写权限。

## 构建环境

- Windows x64：`windows-latest`
- Linux x64：`ubuntu-24.04`
- Linux ARM64：`ubuntu-24.04-arm`
- CLI 使用 .NET 10 Native AOT，服务端和 Windows GUI 使用托管自包含发布；Linux ARM64 CLI 在 ARM runner 上原生构建。
- Android 使用 .NET 10 Android workload、SDK 36 与 JDK 21，最低 API 26，分别构建两个 ABI。
