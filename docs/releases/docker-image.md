---
layout: default
title: Docker 镜像
description: SonnetDB 的容器镜像发布、标签策略与启动方式。
permalink: /releases/docker-image/
---

`PR #39` 为仓库补齐了 `SonnetDB` 的 Docker 镜像自动发布流水线，目标仓库为：

- Docker Hub：`iotsharp/sonnetdb`
- GHCR：`ghcr.io/<owner>/sonnetdb`

镜像内包含：

- `SonnetDB`
- 管理后台前端
- `/help` 静态帮助站点
- 默认的 `/data` 数据目录挂载点

## 标签策略

- `latest`：`main` 分支最新成功构建
- `edge`：`main` 分支滚动标签
- `vX.Y.Z`：与 Git tag 对齐的版本标签
- `X.Y`：同次版本滚动标签
- `sha-<commit>`：便于回溯具体提交

## 启动方式

```bash
docker run --rm \
  -p 127.0.0.1:5080:5080 \
  -v ./sonnetdb-data:/data \
  iotsharp/sonnetdb:latest
```

或者使用 GHCR：

```bash
docker run --rm \
  -p 127.0.0.1:5080:5080 \
  -v ./sonnetdb-data:/data \
  ghcr.io/<owner>/sonnetdb:latest
```

启动后默认可访问：

- `http://127.0.0.1:5080/admin/`
- `http://127.0.0.1:5080/help/`
- `http://127.0.0.1:5080/healthz`
- `http://127.0.0.1:5080/metrics`

## 首次初始化与远程部署

空数据目录的 `/v1/setup/initialize` 允许创建第一个管理员，因此首次启动应保持主机端口仅对本机开放。仓库 Compose 默认将 HTTP `5080`、Frame `5081`、MQTT `1883` 以及可选观测栈端口全部发布到 `127.0.0.1`。开发用 `docker-compose.override.yml` 只增加镜像构建配置，仍沿用这些端口边界。容器内部继续监听全部容器接口，容器网络内的客户端应处于同一受信任部署边界。

本机体验可以直接访问 `/admin/` 完成安装向导。准备远程部署时，先用现有环境变量引导创建管理员，并在仍保持 loopback 映射时验证初始化。Compose 已透传 `SONNETDB_USER`、`SONNETDB_PASSWORD` 和可选的 `SONNETDB_DB`；例如在 Bash 中输入本次部署的独立密码：

```bash
export SONNETDB_USER=operator
read -rsp 'Initial administrator password: ' SONNETDB_PASSWORD
printf '\n'
export SONNETDB_PASSWORD
export SONNETDB_DB=metrics
docker compose -f docker-compose.yml up -d

# 等待启动完成后，确认 needsSetup 为 false。
curl --fail http://127.0.0.1:5080/v1/setup/status
# curl 提示输入刚才设置的密码；此请求应返回已授权的数据库列表。
curl --fail --user "$SONNETDB_USER" http://127.0.0.1:5080/v1/db
```

直接使用 `docker run` 时，保留 `-p 127.0.0.1:5080:5080` 并增加 `-e SONNETDB_USER -e SONNETDB_PASSWORD -e SONNETDB_DB` 即可传入相同引导配置。也可以使用不提交到仓库的私有 env 文件；Compose 的 `--env-file` 和 `docker run --env-file` 均可承载这三个变量。

只有在 `needsSetup=false`、管理员认证成功且匿名数据请求被拒绝后，再按实际客户端需求将对应 `ports` 条目中的 `127.0.0.1` 改为明确的目标接口地址，重新创建容器。HTTP 远程访问应通过已配置 TLS 的入口。MQTT、Frame 和观测端口分别按需开放，初始化并不自动为这些端口增加 TLS。

引导只在未初始化时执行；修改环境变量不会重置已有管理员密码。引导失败可能只记日志，因此以实际 setup 状态和认证结果为准。单独配置静态 `SonnetDBServer:Tokens` 不会完成首次安装，不能替代上述初始化验证。

## 自动发布工作流

仓库中的 `.github/workflows/docker-publish.yml` 会在以下场景触发：

- `main` 分支上与服务端镜像相关的文件变更
- 推送 `v*` 版本标签
- 手动触发 `workflow_dispatch`

工作流会：

1. 构建 `src/SonnetDB/Dockerfile`
2. 推送到 GHCR
3. 在 Docker Hub Secrets 配置完成后同步推送到 `iotsharp/sonnetdb`
4. 生成 OCI labels 与版本标签
5. 通过 GitHub Actions Cache 复用 Docker 层缓存

## 仓库 Secrets

若要把镜像推送到 Docker Hub，需要在仓库中配置：

- `DOCKERHUB_USERNAME`
- `DOCKERHUB_TOKEN`

GHCR 默认使用 `GITHUB_TOKEN` 登录，无需额外密码，但需要仓库允许 Actions 写入 packages。
