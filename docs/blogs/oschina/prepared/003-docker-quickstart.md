---
title: Docker 快速上手：5 分钟运行 SonnetDB
categories: SonnetDB,Docker,部署
draft: false
---

Docker 可以帮助我们快速体验 SonnetDB 的 HTTP API、管理界面和随镜像附带的帮助站点。本文的目标是完成首次启动和连接验证；下载耗时、机器配置和初始化操作都会影响实际时间，“5 分钟”不是性能保证。

SonnetDB 已有正式的 [4.0.0 Release](https://github.com/IoTSharp/SonnetDB/releases/tag/v4.0.0)。以下使用 `latest` 演示仓库文档中的滚动镜像流程：它跟随 `main` 的成功构建，不能与固定的 4.0.0 发行物等同。需要固定版本时，先从镜像仓库确认对应标签或 digest，再替换示例中的 `latest`。

## 第一步：拉取和启动

```bash
docker pull iotsharp/sonnetdb:latest

docker run -d \
  --name sonnetdb \
  -p 127.0.0.1:5080:5080 \
  -v sonnetdb-data:/data \
  iotsharp/sonnetdb:latest
```

HTTP API 与管理界面共用 `5080`。主机映射只接受本机连接，首次初始化在这个范围内完成。命名数据卷挂载到 `/data`，删除容器不会自动删除这个卷。

如果选择 bind mount，可以把卷参数换成 `-v /opt/sonnetdb/data:/data`。先确认宿主目录存在且权限适合容器，避免因为挂载失败实际使用了错误的数据目录。

## 第二步：完成首次安装

浏览器打开 `http://127.0.0.1:5080/admin/`。空数据目录会进入首次设置向导，设置组织、管理员用户名、密码和初始 Token。Token 应保存在自己的凭据存储中；它不会以明文从服务端再次读回。

检查初始化状态与健康状态：

```bash
curl --fail http://127.0.0.1:5080/v1/setup/status
curl --fail http://127.0.0.1:5080/healthz
```

`needsSetup=false` 才说明首次初始化已经完成。健康检查成功本身不证明管理员认证、数据库权限或全部模型已经可用。

## 第三步：创建一个数据库

在管理界面中创建 `metrics`，或使用刚才取得的管理员 Token 调用 API：

```bash
curl --fail -X POST http://127.0.0.1:5080/v1/db \
  -H 'Authorization: Bearer <your-token>' \
  -H 'Content-Type: application/json' \
  --data '{"name":"metrics"}'
```

替换 `<your-token>` 后才能执行。数据库中的 SQL 入口是 `/v1/db/metrics/sql`，不是旧的 `/api/v1` 路由。

## 使用 Docker Compose

```yaml
services:
  sonnetdb:
    image: iotsharp/sonnetdb:latest
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

保存后运行 `docker compose up -d`。准备远程部署时，先用每次部署独立的 `SONNETDB_USER` / `SONNETDB_PASSWORD` 引导管理员，在 loopback 范围内确认 `needsSetup=false`、认证成功和匿名数据请求被拒绝，再按需要开放明确的接口地址，并为远程 HTTP 配置 TLS。

已有实例不会因为修改引导环境变量而重置管理员密码。单独配置静态 Token 也不能替代首次初始化。Frame、MQTT 和其它协议端口分别配置，本例未开放它们。

后续步骤见 [Docker 镜像说明](https://github.com/IoTSharp/SonnetDB/blob/main/docs/releases/docker-image.md)、[开始使用](https://github.com/IoTSharp/SonnetDB/blob/main/docs/getting-started.md)和[备份恢复](https://github.com/IoTSharp/SonnetDB/blob/main/docs/backup-restore.md)。数据库卷备份也须按恢复合同验证；实例级 SonnetMQ 状态不能由单库备份覆盖。
