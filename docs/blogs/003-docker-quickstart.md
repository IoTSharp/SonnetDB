## Docker 快速上手：5 分钟运行 SonnetDB

如果您想快速体验 SonnetDB 的功能而不想处理编译和配置的细节，Docker 无疑是最便捷的途径。本文将引导您在 5 分钟内完成 SonnetDB 的 Docker 部署，并访问其管理界面。

### 第一步：拉取镜像

SonnetDB 的 Docker 镜像托管在 Docker Hub 上，您可以直接使用 `docker pull` 命令获取最新版本。打开终端，执行以下命令：

```bash
docker pull iotsharp/sonnetdb:latest
```

镜像包含 SonnetDB 服务端、Web 管理界面和帮助站点，基于 .NET 10 构建。可用镜像与标签以 [Docker 镜像说明](../releases/docker-image.md) 和对应发布产物为准。

### 第二步：启动容器

拉取完成后，使用以下命令启动 SonnetDB 容器：

```bash
docker run -d \
  --name sonnetdb \
  -p 127.0.0.1:5080:5080 \
  -v sonnetdb-data:/data \
  iotsharp/sonnetdb:latest
```

`-p 127.0.0.1:5080:5080` 将 HTTP API 和 Web 管理界面映射到主机 loopback，首次初始化仅通过本机访问；`-v sonnetdb-data:/data` 用数据卷持久化数据库，容器删除后数据卷仍保留。

如果您希望在宿主机特定目录存储数据，也可以使用 bind mount 方式：

```bash
docker run -d \
  --name sonnetdb \
  -p 127.0.0.1:5080:5080 \
  -v /opt/sonnetdb/data:/data \
  iotsharp/sonnetdb:latest
```

### 第三步：访问管理界面

容器启动后，打开浏览器访问 `http://127.0.0.1:5080/admin/`。首次访问时进入首次设置向导，创建管理员用户并获取访问令牌。

管理界面提供了一系列便捷的功能：SQL 查询控制台、数据可视化、测量管理、用户和令牌管理、系统监控仪表盘等。对于不熟悉命令行操作的开发者来说，图形化界面大大降低了使用门槛。

### 第四步：通过 API 验证连接

除了 Web 界面，您也可以通过命令行验证 SonnetDB 是否正常运行：

```bash
# 检查健康状态
curl --fail http://127.0.0.1:5080/healthz
```

### Docker Compose 方式

对于生产环境，推荐使用 Docker Compose 进行更精细的配置管理。创建一个 `docker-compose.yml` 文件：

```yaml
services:
  sonnetdb:
    image: iotsharp/sonnetdb:latest
    container_name: sonnetdb
    ports:
      - "127.0.0.1:5080:5080"
    volumes:
      - ./data:/data
    restart: unless-stopped
    environment:
      SONNETDB_USER: ${SONNETDB_USER:-}
      SONNETDB_PASSWORD: ${SONNETDB_PASSWORD:-}
      SONNETDB_DB: ${SONNETDB_DB:-}
```

然后执行 `docker compose up -d` 即可一键启动。

远程部署先通过 `SONNETDB_USER` / `SONNETDB_PASSWORD` 完成管理员引导，在本机确认 `needsSetup=false` 和管理员认证成功后，再显式开放所需接口。见 [Docker 首次初始化与远程部署](../releases/docker-image.md#首次初始化与远程部署)。

至此，您的 SonnetDB 实例已经成功运行。接下来，您可以尝试使用 `CREATE MEASUREMENT` 创建第一个时序表，或者通过 SQL 控制台执行查询，亲身体验 SonnetDB 的功能与性能。
