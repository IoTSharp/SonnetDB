## SonnetDB 生产部署指南：Docker Compose、安装包与环境变量

SonnetDB 可以通过 Docker、完整 Server bundle 或原生安装包部署。部署选择之外，还要明确初始化身份、数据目录、版本回滚和实际开放的监听口；单个容器或安装包不自动构成高可用集群。

本文依据当前部署文档整理。正式 4.0.0 发布页与 main 滚动镜像是不同来源：`latest` 跟随 main 的成功构建，生产应使用实际验证的版本标签或 digest，并保存对应配置和恢复证据。

### Docker Compose 最小示例

```yaml
services:
  sonnetdb:
    image: iotsharp/sonnetdb:latest
    ports:
      - "127.0.0.1:5080:5080"
    volumes:
      - sonnetdb-data:/data
    environment:
      SONNETDB_SonnetDBServer__DataRoot: /data
      SONNETDB_SonnetDBServer__AutoLoadExistingDatabases: "true"
      SONNETDB_USER: ${SONNETDB_USER:-}
      SONNETDB_PASSWORD: ${SONNETDB_PASSWORD:-}
      SONNETDB_DB: ${SONNETDB_DB:-}
    restart: unless-stopped
volumes:
  sonnetdb-data:
```

示例沿用滚动镜像便于本机体验，生产使用前换成已确认存在且测试通过的 digest。仓库 Compose 还提供 Frame、MQTT 与观测栈选项，分别检查配置后按需开放。

启动前通过私有 env 文件或部署环境提供独立管理员凭据。以下是 Bash 示例：

```bash
export SONNETDB_USER=operator
read -rsp 'Initial administrator password: ' SONNETDB_PASSWORD
export SONNETDB_PASSWORD
export SONNETDB_DB=metrics
docker compose up -d
curl --fail --max-time 10 http://127.0.0.1:5080/v1/setup/status
curl --fail --max-time 10 --user "$SONNETDB_USER" http://127.0.0.1:5080/v1/db
```

确认 `needsSetup=false` 和管理员认证成功，再配置自己的 HTTPS 和网络入口。访问 `/admin/` 也可以完成安装向导。引导变量只用于未初始化状态，改变它们不会重置已有管理员密码；仅设置静态 token 也不会完成首次安装。

### Bundle 与原生安装包

完整 Server bundle 包含可执行文件、管理前端、帮助站点、CLI 和默认配置。Windows 启动脚本为 `start-sonnetdb.cmd`，Linux 为 `start-sonnetdb.sh`。下载时按正式 release 的实际文件名与平台选择，不沿用历史稿中虚构的 `0.1.0` 文件名。

当前新生成的 bundle 和 MSI/DEB/RPM 内置 Server 默认将 HTTP/Frame 绑定本机，其他协议显式关闭。既有安装和 Docker/源码配置需要分别核对。打包合同已实现，并不代表每个目标系统的安装、升级、卸载与恢复都已实机验收。

Server full bundle 的公开初始化账号和静态 token 必须按部署文档更换/移除。改管理员密码会撤销该用户颁发的 token，但不会自动删掉 `appsettings.json` 或环境变量中的静态 token；重启后验证旧凭据失效再开放远程。

### 环境变量的层级

双下划线对应 .NET 配置层级，例如：

```text
SONNETDB_SonnetDBServer__DataRoot=/data
SONNETDB_SonnetDBServer__AutoLoadExistingDatabases=true
SONNETDB_SonnetDBServer__Observability__Prometheus__Enabled=true
```

Copilot 是否启用与聊天、embedding provider 的配置需要根据当前 [provider 文档](https://github.com/IoTSharp/SonnetDB/blob/main/docs/copilot-providers.md) 设置。不要只填一个 OpenAI URL 就宣称模型已经就绪；实际模型、维度、凭据、网络与成本分别验证。

### 验证、备份和升级

`/healthz`、`/healthz/live`、`/healthz/ready` 服务于不同健康检查；健康成功不替代数据写读和恢复验收。`/metrics` 默认最小集合，启用 exporter 后可观察更完整的写入、WAL、flush、查询和积压指标。

CLI 安装使用实际包版本：

```bash
dotnet tool install --global SonnetDB.Cli
sndb version
sndb remote --url https://db.example.com --database metrics --token <token> --save-profile production
sndb connect production --repl
```

数据持久化要保留完整目录与相关配置。卷存在并不防止误删、磁盘损坏或不兼容升级。协调停止写入后使用数据库备份工具，并在新目录校验和恢复；单库备份不要宣称覆盖 Server 实例的 MQ、用户和其他独立持久化状态。

升级前保存镜像 digest/包版本、配置和备份，使用副本验证 schema、查询、认证与恢复边界。若格式升级不能被旧版本读取，回滚必须依赖升级前备份，不能只换回旧容器就假定成功。

参考：[Docker 发布与初始化](https://github.com/IoTSharp/SonnetDB/blob/main/docs/releases/docker-image.md)、[Server bundle](https://github.com/IoTSharp/SonnetDB/blob/main/docs/releases/server-bundle.md)、[备份恢复](https://github.com/IoTSharp/SonnetDB/blob/main/docs/backup-restore.md)、[正式 4.0.0](https://github.com/IoTSharp/SonnetDB/releases/tag/v4.0.0)。
