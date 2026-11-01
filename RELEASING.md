# 发布版本

项目通过 `.github/workflows/release.yml` 自动构建并发布 GitHub Release。工作流不需要额外配置 Secret，使用仓库自带的 `GITHUB_TOKEN` 上传 Release 资产。

发布前会先运行核心路由和 UDP 多跳集成测试；测试失败时不会生成或覆盖 Release。

## 发布步骤

1. 确认 `master` 已同步并且构建通过。
2. 创建语义化版本标签，例如 `v1.0.0`。
3. 推送标签，GitHub Actions 会自动构建并创建 Release。

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
| `SHA256SUMS.txt` | 所有发布资产的 SHA256 校验值 |

GitHub 还会自动附带对应标签的源码 zip 和 tar.gz。

## 重新构建已有标签

在 GitHub 页面依次进入 **Actions** > **Release** > **Run workflow**，输入已经存在的标签，例如 `v1.0.0`，然后运行工作流。

如果 Release 已存在，工作流会覆盖同名资产；不会重复创建 Release。手动运行工作流不会自动创建标签，因此输入标签必须已经推送到仓库。

## 权限设置

工作流声明了 `contents: write` 权限。如果发布步骤返回 `403`，请检查仓库的 **Settings** > **Actions** > **General** > **Workflow permissions**，允许 GitHub Actions 使用读写权限。

## 构建环境

- Windows x64：`windows-latest`
- Linux x64：`ubuntu-24.04`
- Linux ARM64：`ubuntu-24.04-arm`
- 所有平台均使用 .NET 10 原生发布，不通过 Windows 交叉编译 Linux ARM64。
