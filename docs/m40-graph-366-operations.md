---
layout: default
title: "Graph 运维产品面"
description: "SonnetDB M40 #366 Graph 运维概览、可视化、受限编辑、导入导出、两阶段维护与审计合同。"
permalink: /graph-operations/
---

# Graph 运维产品面

M40 #366 在既有 Graph V1 存储、事务、遍历和维护能力之上，提供一致的 Server、typed SDK、CLI 与 Web/Studio 运维入口。它覆盖 schema/index/degree 统计、慢遍历诊断、有界可视化、带版本条件的元素编辑、JSON 导入导出，以及 repair/rebuild、checkpoint、compaction 的两阶段审批和持久审计。

本项没有修改 Graph V1 record、key、WAL、checkpoint 或备份格式，也没有增加第二套图存储、权限或维护执行器。固定硬件容量、LDBC/Graphalytics、7 天 mixed workload、kill/reopen、backup/restore 和 Production 发布决定仍属于 #367。在 #367 通过前，产品定位继续是“八种数据模型，一套引擎”，不能据 #366 宣称第九模型或生产级图数据库已经发布。

## 权限矩阵

| 能力 | 最低数据库权限 | 说明 |
|---|---|---|
| 列出 Graph、概览、可视化、导出、读取元素 | `Read` | 所有读取都绑定明确数据库和 Graph。 |
| 创建/删除 Graph、受限元素编辑、JSON 导入 | `Write` | 编辑继续使用既有 element version 和幂等 request ID。 |
| 暂存、批准、拒绝维护和读取维护审计 | `Admin` | 暂存不会执行；批准才进入既有维护 API。 |

Web/Studio 仍会在执行普通元素 mutation 前显示本地写确认。repair/rebuild、checkpoint 和 compact 不能通过该本地确认直接执行，必须先在服务端暂存，再使用返回的审批 ID 批准或拒绝。

## HTTP API

| 方法与路径 | 权限 | 合同 |
|---|---|---|
| `GET /v1/db/{db}/graphs/{graph}/operations/overview` | `Read` | 返回 graph/schema 摘要、snapshot sequence、vertex/edge 数、label/index cardinality、degree histogram、最近慢遍历和 capability flags。 |
| `GET /v1/db/{db}/graphs/{graph}/operations/visualization?limit=250` | `Read` | 返回同一 statement snapshot 上的顶点及这些顶点之间的边，并显式返回 `truncated`。 |
| `GET /v1/db/{db}/graphs/{graph}/operations/export?maxElements=100000` | `Read` | 通过响应 `PipeWriter` 流式写出 importer-compatible JSON。 |
| `POST /v1/db/{db}/graphs/{graph}/maintenance/stage` | `Admin` | 暂存维护，返回 10 分钟有效的 `approvalId`，HTTP 状态为 `202`。 |
| `POST /v1/db/{db}/graphs/{graph}/maintenance/{approvalId}/approve` | `Admin` | 原子认领尚未决策的审批并执行维护，返回 `completed` 或 `paused` 等终态。 |
| `POST /v1/db/{db}/graphs/{graph}/maintenance/{approvalId}/reject` | `Admin` | 拒绝尚未决策的审批；可选 reason 最长 512 字符。 |
| `GET /v1/db/{db}/graphs/{graph}/maintenance/audit?limit=200` | `Admin` | 按时间倒序返回审批和执行事件。 |

元素 `GET`/`PUT`/`DELETE` 与 `POST .../import` 继续复用既有 `/v1/db/{db}/graphs/{graph}` 路由；#366 没有创建绕开 Graph transaction 的管理专用写入口。

### 固定预算

| 项目 | 默认值 | 上限/行为 |
|---|---:|---|
| 可视化顶点 `limit` | 250 | 1~1,000；只返回所选顶点间的边，内部 edge scan 最多 100,000 条并显式标记截断。 |
| JSON 导出 `maxElements` | 100,000 | 1~1,000,000 个顶点与边；分页扫描并流式写入，不全量物化导出文档。 |
| 运维概览统计 | 固定 | 最多扫描 50,000,000 个条目、生成 1,000,000 个统计组；超限返回 `413 graph_statistics_budget_exceeded`。 |
| maintenance `maxWorkUnits` | 64 | 1~4,096；repair/rebuild 可以返回 `paused` 并按既有 continuation 续作。 |
| 审计 `limit` | 200 | 1~2,000。 |
| 审批有效期 | 10 分钟 | 过期批准写入 `expired` 事件，并要求重新暂存。 |

导出文档中的 `snapshotSequence` 固定读取时刻，`elementCount` 是实际写出的顶点与边总数。只有 `truncated=false` 才是完整 round-trip 数据集；`truncated=true` 的文件可用于有界检查或部分导入，但不能当成 Graph 的完整备份或完整导出再导入证据。

## 两阶段维护与审计

暂存请求示例：

```json
{
  "action": "RepairRebuild",
  "maxWorkUnits": 64,
  "compactOnCompletion": false
}
```

支持的 action 是 `RepairRebuild`、`Checkpoint` 和 `Compact`。`compactOnCompletion` 只适用于 repair/rebuild。每次状态变化都会追加审计事件，典型状态序列为：

```text
staged -> applying -> completed
                   -> paused
                   -> failed
staged -> rejected
staged -> expired
```

审计使用 source-generated JSON 按 NDJSON 追加并在返回前 durable flush。服务端文件位于：

```text
<DataRoot>/.system/graph-maintenance-audit.ndjson
```

嵌入式 SDK/CLI 文件位于：

```text
<embedded-db>/.system/graph-maintenance-audit.ndjson
```

因此暂存和批准可以由两个独立 CLI 进程完成。损坏的审计行会在打开时明确拒绝，不能静默跳过；同一个审批 ID 只能决策一次，且必须匹配原数据库和 Graph。

## .NET SDK

`SndbGraphClient` 对嵌入式与远程连接公开相同方法：

```csharp
using SonnetDB.Data.Graphs;
using SonnetDB.Graphs;

using var client = new SndbGraphClient(connectionString);

GraphOperationsOverviewDto overview =
    await client.GetOperationsOverviewAsync("knowledge");
GraphVisualizationDto visualization =
    await client.GetVisualizationAsync("knowledge", limit: 250);

await using (var output = File.Create("knowledge.graph.json"))
    await client.ExportJsonAsync("knowledge", output, maxElements: 100_000);

GraphMaintenanceApprovalDto staged = await client.StageMaintenanceAsync(
    "knowledge",
    new GraphMaintenanceStageRequest
    {
        Action = GraphMaintenanceAction.RepairRebuild,
        MaxWorkUnits = 64,
        CompactOnCompletion = false,
    });

GraphMaintenanceApprovalDto result =
    await client.ApproveMaintenanceAsync("knowledge", staged.ApprovalId);
```

嵌入式 overview 没有 Server SQL 慢查询环，因此 `slowTraversalDiagnostics=false` 且 reason 为 `not_available_embedded`；其他 schema/index/degree、可视化、导入导出、受限编辑、维护和审计能力保持一致。

## CLI

CLI 的 Graph 命令同时支持 `Data Source=<directory>` 嵌入式连接和远程 SonnetDB 连接字符串：

```text
sndb graph list --connection "<conn>"
sndb graph overview --connection "<conn>" --graph knowledge
sndb graph visualize --connection "<conn>" --graph knowledge --limit 250
sndb graph export --connection "<conn>" --graph knowledge --output ./knowledge.graph.json --max-elements 100000
sndb graph import --connection "<conn>" --graph knowledge --input ./knowledge.graph.json --batch-size 1000

sndb graph maintenance stage --connection "<conn>" --graph knowledge --action repair --max-work-units 64
sndb graph maintenance approve --connection "<conn>" --graph knowledge --approval <id>
sndb graph maintenance reject --connection "<conn>" --graph knowledge --approval <id> --reason "operator cancelled"
sndb graph maintenance audit --connection "<conn>" --graph knowledge --limit 200
```

`graph overview`、`visualize`、maintenance 和 audit 输出 source-generated JSON，便于脚本消费。`graph import` 支持显式 `--request-id`，并继续通过 `SndbGraphImporter` 分批使用既有幂等 Graph import transaction。

## Web/Studio

Explorer 会把 Graph 作为独立对象类型列出并可按名称筛选。打开 Graph 后，Workbench 提供五个任务页：

| 任务页 | 能力 |
|---|---|
| Canvas | ECharts force-directed 有界拓扑、snapshot/truncation 状态和元素检查器。 |
| Schema & diagnostics | label/index cardinality、degree histogram 与 Server 慢遍历样本。 |
| Edit | vertex/edge 点读、upsert/delete、expected version 和本地高风险写确认。 |
| Import/export | 有界流式 JSON 导出、截断警告和 importer-compatible JSON 导入。 |
| Maintenance | repair/rebuild、checkpoint、compact 的暂存、批准/拒绝和审计表。 |

Workbench 使用现有连接、数据库选择、权限和 URL workspace routing；桌面与 390x844 移动视口均有 Playwright 回归，Canvas 还通过像素检查证明 ECharts 实际绘制而不是空画布。

## 发布边界

#366 关闭的是运维产品面和多客户端合同，不是 Production gate。以下项目仍保持 `NOT_RUN` 或 open，并统一归 #367：

- 固定 x64/ARM64 目标硬件的 1m/10m 容量与 P50/P95/P99 报告；
- LDBC SNB 和 Graphalytics 子集、Neo4j/PostgreSQL 外部对拍；
- 7 天 mixed workload、kill/reopen、backup/restore 与恢复正确性报告；
- 发布构建的 Native AOT 证据归档和 Couplet C4 联合门禁；
- 是否把对外定位从八模型更新为九模型的发布决定。
