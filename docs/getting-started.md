---
layout: default
title: "开始使用"
description: "从 Docker 启动、首次安装到本地和远程访问的最短路径。"
permalink: /getting-started/
---

## 1. 启动 `SonnetDB`

从仓库根目录构建镜像：

```bash
docker build -f src/SonnetDB/Dockerfile -t sonnetdb .
```

运行容器：

```bash
docker run --rm -p 5080:5080 -v ./sonnetdb-data:/data sonnetdb
```

默认行为：

- 容器内监听 `http://0.0.0.0:5080`
- 数据根目录为 `/data`
- 帮助文档会随镜像一起发布到 `/help`

如果直接从源码运行服务端，也会默认监听 `5080`，并使用 `./sonnetdb-data` 作为数据根目录。

## 2. 打开管理入口

浏览器访问：

```text
http://127.0.0.1:5080/admin/
```

帮助文档入口：

```text
http://127.0.0.1:5080/help/
```

## 3. 完成首次安装

当 `<DataRoot>/.system` 为空时，`/admin/` 不会直接落到登录页，而是进入首次安装向导。

向导会预填：

- 基于主机名与本机硬件指纹生成的稳定服务器 ID
- 默认组织名称
- 管理员用户名 `admin`
- 自动生成的初始静态 Bearer Token

仅管理员密码必须手动输入；其余默认值均可在提交前修改。硬件原始标识只参与服务器 ID 的 SHA-256 短哈希计算，不会通过 API 或日志暴露。

初始化成功后，服务端会在 `<DataRoot>/.system/` 下生成：

- `installation.json`
- `users.json`
- `grants.json`

其中 Bearer Token 的元数据与哈希保存在 `users.json` 中，不会以明文再次落盘。

## 4. 创建数据库

完成首次安装并登录后，可以在管理界面中创建数据库，也可以直接调用 HTTP API：

```bash
curl -X POST "http://127.0.0.1:5080/v1/db" \
  -H "Authorization: Bearer <your-token>" \
  -H "Content-Type: application/json" \
  -d "{\"name\":\"metrics\"}"
```

## 5. 写入第一批数据

通过 SQL：

```bash
curl -X POST "http://127.0.0.1:5080/v1/db/metrics/sql" \
  -H "Authorization: Bearer <your-token>" \
  -H "Content-Type: application/json" \
  -d "{\"sql\":\"CREATE MEASUREMENT cpu (host TAG, usage FIELD FLOAT)\"}"
```

```bash
curl -X POST "http://127.0.0.1:5080/v1/db/metrics/sql" \
  -H "Authorization: Bearer <your-token>" \
  -H "Content-Type: application/json" \
  -d "{\"sql\":\"INSERT INTO cpu (time, host, usage) VALUES (1713676800000, 'server-01', 0.71)\"}"
```

## 6. 验证查询

```bash
curl -X POST "http://127.0.0.1:5080/v1/db/metrics/sql" \
  -H "Authorization: Bearer <your-token>" \
  -H "Content-Type: application/json" \
  -d "{\"sql\":\"SELECT time, host, usage FROM cpu WHERE host = 'server-01'\"}"
```

服务端会以 ndjson 方式返回 meta、rows 和结束标记。

## 7. 本地嵌入式访问

如果你不需要 HTTP 服务端，可以直接在进程内打开数据库目录：

```csharp
using SonnetDB.Engine;
using SonnetDB.Sql.Execution;

using var db = Tsdb.Open(new TsdbOptions
{
    RootDirectory = "./demo-data",
});

SqlExecutor.Execute(db, "CREATE MEASUREMENT cpu (host TAG, usage FIELD FLOAT)");
```

更完整的进程内示例见 [嵌入式与 in-proc API]({{ site.docs_baseurl | default: '/help' }}/embedded-api/)。

## 8. ADO.NET 与 CLI

本地 ADO.NET 连接：

```text
Data Source=./demo-data
```

远程 ADO.NET 连接：

```text
Data Source=sonnetdb+http://127.0.0.1:5080/metrics;Token=<your-token>
```

CLI 示例：

```bash
sndb sql --connection "Data Source=./demo-data" --command "SELECT count(*) FROM cpu"
```

详细用法分别见：

- [ADO.NET 参考]({{ site.docs_baseurl | default: '/help' }}/ado-net/)
- [CLI 参考]({{ site.docs_baseurl | default: '/help' }}/cli-reference/)

## 常用端点

| 地址 | 说明 |
| --- | --- |
| `/admin/` | 产品首页、首次安装、登录和后台入口 |
| `/help/` | 帮助文档站点 |
| `/healthz` | 健康检查 |
| `/metrics` | Prometheus 指标 |
| `/v1/setup/status` | 查询是否需要首次安装 |
| `/v1/setup/initialize` | 完成首次安装 |
| `/v1/db` | 数据库列表、创建、删除 |
| `/v1/db/{db}/sql` | 数据面 SQL 入口 |
| `/v1/sql` | 控制面 SQL 入口，仅 admin |
| `/v1/events` | SSE 事件流 |
