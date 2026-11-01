# ProxyNetworker.Server Docker 部署端口说明

`NetworkDefaults` 和 `SystemSettings` 的说明见 [README.Configuration.md](README.Configuration.md)。

## Docker Compose（当前推荐）

仓库内提供了 [compose.yaml](compose.yaml)。Linux 服务器使用 host network，让动态分配的 TCP/UDP 端口直接监听宿主机网卡。

```bash
cd ProxyNetworker.Server
mkdir -p data logs data-protection-keys
docker compose up -d --build
docker compose ps
curl http://127.0.0.1:13301/health
```

当前拓扑：

- 管理页面和管理 API：`127.0.0.1:13301`，不直接对公网开放。
- 外部 HTTPS 入口：由 FastGateway 等反向代理监听，例如 `https://server.example.com:<management-port>`，转发到 `http://127.0.0.1:13301`。
- 端口穿透和虚拟网络数据端口：`40000-42999/tcp` 与 `40000-42999/udp`。
- SQLite、日志和 ASP.NET Core Data Protection 密钥分别持久化到 `data`、`logs` 和 `data-protection-keys`。

反向代理必须保留原始 Host，并允许 WebSocket Upgrade。管理 API 的 `urls` 与数据端口的 `NetworkDefaults.PublicListenAddress` 是两个独立配置。

## 端口类型

服务端有两类对外端口：

- 管理/API 端口：由 `urls` 配置控制，例如 `http://*:13301`，需要发布 TCP。
- 隧道/虚拟局域网端口：由资源创建时的 `ListenPort` 或系统端口范围分配，端口会在运行时绑定，需要提前发布到宿主机。

Docker bridge 网络不会因为容器内进程运行后动态绑定新端口，就自动开放宿主机端口。因此动态分配端口必须限制到固定范围，并在启动容器时预发布。

## 推荐配置

`appsettings.Docker.json` 和当前 [compose.yaml](compose.yaml) 使用的动态端口范围都是 `40000-42999`：

```json
{
  "SystemSettings": {
    "PublicPortRangeStart": 40000,
    "PublicPortRangeEnd": 42999
  }
}
```

如果数据库已经存在，`SystemSettings` 会以数据库值为准，需要到管理页面或 API 更新端口范围。

## docker run 示例

```powershell
docker run -d --name proxynetworker-server `
  -e ASPNETCORE_ENVIRONMENT=Docker `
  -e DOTNET_ENVIRONMENT=Docker `
  -p 13301:13301/tcp `
  -p 30010-30100:30010-30100/tcp `
  -p 30010-30100:30010-30100/udp `
  your-image
```

如果只使用虚拟局域网，至少需要发布：

```powershell
-p 13301:13301/tcp `
-p 30010-30100:30010-30100/udp
```

## 已有数据库注意事项

`SystemSettings` 会持久化到 SQLite。已有数据库不会因为换了 `appsettings.Docker.json` 自动改端口范围。部署到 Docker 前，需要在管理页或 API 中把系统外网端口范围改到已经发布的范围，例如 `30010-30100`。

Linux 服务器也可以使用 `--network host` 避免端口发布范围问题，但这会让容器直接共享宿主网络命名空间，隔离性更弱。

## 首次启动管理员

当数据库中没有用户时，服务会创建管理员账号 `sky`，并生成一个包含大小写字母、数字和符号的 12 位随机密码。首次密码只在创建管理员时写入一次服务启动日志，请登录后立即修改，并妥善限制日志访问权限。
