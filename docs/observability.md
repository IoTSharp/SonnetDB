---
layout: default
title: "可观测性"
description: "SonnetDB 指标、追踪、健康检查、Prometheus、OTLP、Grafana 与 Aspire Dashboard 联调指南。"
permalink: /observability/
---

# 可观测性

SonnetDB Core 只使用 BCL `Meter` 与 `ActivitySource` 插桩，OpenTelemetry SDK 和导出器位于 Server。未配置导出目标时不会向外发送遥测；Prometheus 完整端点也默认关闭。

## 指标

Core Meter 名为 `SonnetDB.Core`，Server SQL Meter 名为 `SonnetDB.Server`，Copilot Meter 名为 `SonnetDB.Copilot`。下表不包含 ASP.NET Core 和 `HttpClient` instrumentation 自动产生的框架指标。

| 指标 | 类型 | 单位 | 含义与主要标签 |
| --- | --- | --- | --- |
| `sonnetdb.write.points` | Counter | point | 写入路径接受的数据点数 |
| `sonnetdb.write.duration` | Histogram | ms | 单次写入端到端耗时，含 WAL durability 与背压等待 |
| `sonnetdb.wal.fsync.duration` | Histogram | ms | WAL fsync 耗时 |
| `sonnetdb.flush.duration` | Histogram | ms | Flush 总耗时；`outcome=ok|error` |
| `sonnetdb.flush.points` | Counter | point | Flush 落盘的数据点数 |
| `sonnetdb.flush.bytes` | Counter | byte | Flush 生成的 Segment 字节数 |
| `sonnetdb.compaction.duration` | Histogram | ms | Compaction plan 执行耗时；`outcome=ok|error` |
| `sonnetdb.segment.block.reads` | Counter | block | 解码缓存未命中后的物理 Block 读取数 |
| `sonnetdb.segment.block.read.bytes` | Counter | byte | 物理读取的 Block payload 字节数 |
| `sonnetdb.query.duration` | Histogram | ms | Core 查询从枚举开始到结束的耗时；`db.operation=points|aggregate` |
| `sonnetdb.memtable.bytes` | Gauge | byte | 活跃 MemTable 估算内存；`sonnetdb.database` |
| `sonnetdb.memtable.points` | Gauge | point | 活跃 MemTable 点数；`sonnetdb.database` |
| `sonnetdb.segments.count` | Gauge | segment | 活跃 Segment 数；`sonnetdb.database` |
| `sonnetdb.flush.pending` | Gauge | request | 排队或执行中的 Flush 数；`sonnetdb.database` |
| `sonnetdb.sql.query.count` | Counter | query | 完成的 SQL 语句数 |
| `sonnetdb.sql.query.duration` | Histogram | ms | SQL 端到端耗时 |
| `sonnetdb.sql.queue.wait.duration` | Histogram | ms | REST/Frame SQL permit 队列等待 |
| `sonnetdb.sql.candidate.rows` | Histogram | row | 访问路径产生的候选行数 |
| `sonnetdb.sql.examined.rows` | Histogram | row | 完整残余谓词检查的候选行数 |
| `sonnetdb.sql.returned.rows` | Histogram | row | 返回行数 |
| `sonnetdb.sql.allocated.bytes` | Histogram | byte | 同步 SQL 执行线程的托管分配；跨线程未知样本不记录 |
| `sonnetdb.sql.lock.wait.duration` | Histogram | ms | 归属于当前语句的关系表与 KV 关键锁等待 |
| `sonnetdb.sql.logical.reads` | Histogram | read | 按候选行解码口径统计的逻辑读取 |
| `sonnetdb.sql.logical.writes` | Histogram | write | 按受影响行口径统计的逻辑写入 |
| `sonnetdb.sql.physical.reads` | Histogram | read | Segment block 等物理读取 |
| `sonnetdb.sql.physical.read.bytes` | Histogram | byte | Segment block 等物理读取 payload 字节数 |
| `sonnetdb.sql.physical.writes` | Histogram | write | WAL record 等物理写入 |
| `sonnetdb.sql.physical.write.bytes` | Histogram | byte | WAL record 等物理写入字节数 |
| `sonnetdb.sql.execution.duration` | Histogram | ms | 不含 HTTP 响应编码的 Core SQL 执行耗时 |
| `sonnetdb.sql.wal.fsync.duration` | Histogram | ms | 归属于当前语句的 WAL fsync 等待 |
| `sonnetdb.sql.wal.fsync.count` | Histogram | fsync | 归属于当前语句的 WAL fsync 次数 |
| `sonnetdb.sql.gc.gen0.collections` | Histogram | collection | 语句执行窗口内观测到的 Gen0 GC 次数 |
| `sonnetdb.sql.gc.gen1.collections` | Histogram | collection | 语句执行窗口内观测到的 Gen1 GC 次数 |
| `sonnetdb.sql.gc.gen2.collections` | Histogram | collection | 语句执行窗口内观测到的 Gen2 GC 次数 |
| `copilot.chat.requests` | Counter | request | Copilot 请求数；`model`、`mode`、`succeeded` |
| `copilot.chat.duration` | Histogram | ms | Copilot 请求耗时；标签同上 |
| `copilot.chat.tokens` | Counter | token | 输入/输出 token；`direction`、`model` |
| `copilot.tool.calls` | Counter | call | 本地工具调用数；`tool.name` |
| `copilot.knowledge.recall.hits` | Counter | recall | 文档知识召回命中次数 |
| `copilot.knowledge.recall.misses` | Counter | recall | 文档知识召回未命中次数 |

SQL 指标只允许 `outcome`、`access.path` 和 `fallback.reason` 三个有限标签；fingerprint、SQL、数据库、索引名、参数值和行内容均不进入 metric label。其他热路径 Counter/Histogram 也不携带数据库名，逐数据库状态只由四个 Gauge 暴露。不要把 measurement、SQL 原文或用户数据添加为 metric label。

## SQL 诊断聚合

启用慢查询诊断后，每条完成的 REST/Frame SQL 都进入有界 fingerprint 聚合器；只有达到 `ThresholdMs` 的语句才进入最近慢查询样本环、结构化日志、Activity 事件和 SSE `slow_query`。因此 `/v1/diagnostics/top-queries` 的生命周期计数不会因最近样本环覆盖而清零，`/v1/diagnostics/slow-queries` 仍只表示最近的阈值样本。

```json
{
  "Observability": {
    "SlowQueryLog": {
      "Enabled": true,
      "ThresholdMs": 10000,
      "WarningThresholdMs": 30000,
      "CriticalThresholdMs": 60000,
      "Capacity": 256,
      "AggregateCapacity": 1024
    }
  }
}
```

`Capacity` 有效范围为 16～4096，控制最近慢查询样本数；`AggregateCapacity` 有效范围为 16～16384，控制独立 fingerprint 分组数。聚合容量耗尽后不会继续增长字典，无法归属到新 fingerprint 的调用累计在 `unattributedSampleCount`；该全局值只向 Server Admin 返回。Top-N 条目包含访问路径、索引名、fallback、候选/检查/返回行、逻辑/物理 I/O、SQL permit/锁等待与分配量；数据库权限过滤继续适用。

指标适合低基数告警和趋势图，fingerprint Top-N 适合进程内诊断，两者都不是持久审计。进程重启会清空样本和聚合计数；需要跨重启留存时应由受控采集器周期拉取并在外部系统保留。

## Trace 与 span 树

典型 Copilot 查询链路如下。`query_sql` 同步执行时，所有节点共享同一 `TraceId`，父子关系由当前 `Activity` 自动传播。

```text
HTTP POST /v1/copilot/chat                    ASP.NET Core server span
└── copilot.chat                              Copilot 会话
    └── copilot.agent.run_tool                本地工具，tool.name=query_sql
        └── sonnetdb.query.points             Core 原始点查询
            └── sonnetdb.segment.read         Segment 物理 Block 读取
```

其他主要 span：

| span | 用途 | 关键 metadata |
| --- | --- | --- |
| `sonnetdb.query.aggregate` | Core 聚合查询 | `db.system`、`db.operation` |
| `sonnetdb.flush` | MemTable 到 Segment | `sonnetdb.segment.id` |
| `sonnetdb.compaction` | Segment 合并与切换 | 输入数量、输出 Segment ID |
| `sonnetdb.segment.read` | 物理 Block 读取 | Segment ID、Block index、点数、字节数、cache hit；不含路径和字段名 |
| `copilot.agent.plan_tools` | Agent 工具规划 | 模型与工具数量 metadata |
| `copilot.agent.run_tool` | Agent 或云端桥接工具执行 | 工具名、参数长度、成功状态；不含参数正文 |
| `copilot.agent.generate_answer` | Agent 生成最终回答 | 模型与 token metadata |

## 健康检查

| 端点或检查 | 含义 | 运维判定 |
| --- | --- | --- |
| `/healthz` | 轻量兼容摘要，不执行依赖探测 | 用于简单状态页，不作为完整 readiness 依据 |
| `/healthz/live` | 仅证明进程与 HTTP 管线存活 | 失败时重启实例；成功不表示存储和 provider 已就绪 |
| `/healthz/ready` | 聚合下列四项 readiness 检查 | `Unhealthy` 返回 503；provider `Degraded` 不阻断基本数据库流量 |
| `segment_store_writable` | 对 Segment 目录执行真实 write-through 探测 | `Unhealthy` 表示数据目录只读、权限或磁盘故障 |
| `wal_writable` | 对 WAL 目录执行真实 write-through 探测 | `Unhealthy` 时停止接收写流量并检查磁盘 |
| `copilot_provider_reachable` | 检查 Chat provider 配置和 `/models` 可达性 | Copilot 禁用时为 Healthy；配置或网络问题为 Degraded |
| `copilot_embedding_provider_reachable` | 检查 embedding provider | builtin/local 就绪时不发网络请求；远程失败为 Degraded |

远程 provider 的结果缓存 30 秒，探测超时被限制在 1 至 5 秒。存储检查失败会使 readiness 为 `Unhealthy`；Copilot provider 降级只表示 AI 能力不可用。

## Prometheus

启用 Server 自带的完整 Prometheus exporter：

```text
SONNETDB_SonnetDBServer__Observability__Prometheus__Enabled=true
```

Prometheus scrape 示例：

```yaml
scrape_configs:
  - job_name: sonnetdb
    scrape_interval: 15s
    static_configs:
      - targets: ["sonnetdb:5080"]
    metrics_path: /metrics
```

未启用该配置时，`/metrics` 保留兼容用的最小文本指标集，不包含本文列出的完整 OTel histogram 和 Copilot 指标。

## OTLP 导出

设置标准环境变量后，Server 会同时通过 OTLP 导出 metrics 与 traces：

```text
OTEL_EXPORTER_OTLP_ENDPOINT=http://otel-collector:4317
```

端点为空或未设置时不注册 OTLP exporter。生产环境还应按采集器要求配置 TLS、认证 header 和采样策略；不要把 collector 凭据写入仓库中的 Compose env 文件。

## 本地 Compose 观测栈

普通启动只运行 SonnetDB：

```powershell
docker compose up -d
```

显式启用 `observability` profile 后才会增加 OTel Collector、Prometheus 和 Grafana：

```powershell
docker compose --env-file deploy/observability/compose.env --profile observability up -d
```

- Prometheus：`http://localhost:9090`
- Grafana：`http://localhost:3000`，首次登录使用镜像默认账号并立即修改密码
- Collector OTLP gRPC/HTTP：`localhost:4317` / `localhost:4318`
- Collector Prometheus exporter：`http://localhost:9464/metrics`
- Trace 调试输出：`docker compose logs -f otel-collector`

Grafana 已自动配置名为 `SonnetDB Prometheus` 的默认数据源。此本地栈把 trace 输出到 Collector 日志，未附带生产级 trace 存储。

## Aspire Dashboard 联调

单独启动本地 Aspire Dashboard：

```powershell
docker run --rm -it `
  -p 18888:18888 -p 4317:18889 `
  -e DOTNET_DASHBOARD_UNSECURED_ALLOW_ANONYMOUS=true `
  mcr.microsoft.com/dotnet/aspire-dashboard:latest
```

本机运行 SonnetDB 时设置 `OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4317`；SonnetDB 运行在 Docker Desktop 容器中时使用 `http://host.docker.internal:4317`。浏览器打开 `http://localhost:18888` 查看 traces 和 metrics。匿名模式只适合本机开发，不应暴露到共享网络。

出现慢查询、积压或内存异常时，继续参阅[故障排查]({{ site.docs_baseurl | default: '/help' }}/troubleshooting/)。
