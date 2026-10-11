---
title: SonnetDB typed MCP、Copilot 与 RAG：只读工具如何接入 AI
categories: SonnetDB,AI,MCP,RAG
draft: false
---

SonnetDB 的 AI 接入由三部分组成：Provider-neutral 的 Copilot、持久 RAG/向量检索，以及 typed MCP HTTP 工具。它们可以组合使用，但不应混写成“AI 自动拥有数据库写权限”。当前 MCP 入口是 `/mcp/{database}` 的无状态 Streamable HTTP。

本文于 2026-10-07 对照源码复核，公开文档基线为提交 `c785f0479686a3d6584787e1a2b40bd67b0e38c5`。GitHub Release 与 NuGet.org 的 `SonnetDB.Core` 均已存在 4.0.0；仓库内仍保留“候选”字样的发行说明不能单独用于判断是否已发行。下面介绍当前源码合同，MCP 的 schema 仍以实际部署返回的 `tools/list` 为准；本次文章复核未连接真实模型或运行数据库示例。

## 从一个只读数据库接入

先在普通管理入口创建数据库、写入少量测试数据，为 Agent 签发仅拥有目标数据库 Read grant 的 Token。以下用 `metrics` 和已存在的 `temperature` measurement 举例；不存在这些对象时，应先在管理入口准备，MCP 不负责建库或写入。

在支持 Streamable HTTP 的 Agent 宿主中配置连接。不同宿主的外层字段可能不同，这里沿用 SonnetDB Agent 指南的示意格式：

```json
{
  "servers": {
    "sonnetdb": {
      "type": "streamable-http",
      "url": "https://db.example.com/mcp/metrics",
      "headers": { "Authorization": "Bearer ${SONNETDB_TOKEN}" }
    }
  }
}
```

`db.example.com` 是部署地址占位符；环境变量插值须由宿主支持，否则使用其凭据存储。让 MCP 客户端完成 `initialize` 后读取 `tools/list`，先核对工具名、输入和输出 schema，再读取 schema resource 或调用 `describe_measurement`。

可以把下列参数交给 `query_sql`，它们是工具参数，不是直接 POST 到 MCP URL 的完整协议报文：

```json
{
  "sql": "SELECT time, device_id, value FROM temperature WHERE time >= 1713676800000 AND time < 1713763200000 ORDER BY time ASC LIMIT 5",
  "maxRows": 5
}
```

先把相同 SQL 交给 `explain_sql`，观察受支持形状的扫描估算，再执行查询。成功时检查 `contractVersion`、`database`、`columns`、`returnedRows` 和 `truncated`，回答中保留数据库和时间范围。`LIMIT 5` 已把查询限定为五行，不能据 `truncated=false` 判断整个时间范围只有五行；该字段描述工具对本次 SQL 结果的截断情况。返回行数预算也不等于整个执行器的内存或扫描成本上限。

## 当前 MCP 工具

进入 MCP 前会校验 Bearer、数据库存在性和 Read grant。工具集合包括：

- `list_databases`、`list_measurements`、`describe_measurement`；
- `sample_rows`、`query_sql`、`explain_sql`；
- `docs_search`、`skill_search`、`skill_load`。

这些工具面向读取、解释和知识检索，没有 SQL 写工具。旧文章中出现的 `sonnetdb-mcp-server`、`show_measurements` 或通用 `execute_sql` 示例不应继续复制。

若把合法 `INSERT` 语句交给 `query_sql`，应得到 `isError=true` 与稳定错误码 `read_only_violation`。机器客户端先检查 `isError`，再解析第二个文本块的 JSON；缺少必填参数或类型错误可能更早被协议参数绑定器拒绝。数据库不存在或无 Read grant 则在进入工具层前失败，不能把这些错误当成“模型没有找到数据”。

## Copilot 与 Provider

CopilotAgent 通过 `IChatProvider` 接入不同模型配置，Provider-neutral 表示模型提供方可替换，不表示每个提供方都已经完成真实质量和成本验收。OpenAI-compatible、Azure、Ollama/vLLM 等 profile 应显式配置，模型调用、SQL 草稿和结果提交要分开审计。

当前实现是自研 `CopilotAgent`，不能称作 Microsoft Agent Framework。Azure 原生 deployment URL 若要求 `api-key` 和 `api-version`，不能直接填入当前 Bearer `/v1/chat/completions` 适配器，应通过兼容网关接线。本地 HTTP Chat 分支拒绝 `read-write`，配置存在也不等于 readiness 或真实推理通过。

StudioNative 又有独立边界：Web 构建必须显式选择 `StudioNative`，公网 runtime 使用批准的 HTTPS 地址；短期公网 Token 经原生输入存入 Windows Credential Manager。数据库 Token 与公网 Token 不可互换，工具结果出域需要明确的工具允许名单和外发配置。本地 bridge/fixture 验证不能替代真实 WebView2、IdP、公网 provider 和双网部署验收。

## RAG 的版本边界

RAG 摄取会形成带 profile、generation 和 revision 的已发布快照。查询在同一个租约中完成全文候选、精确向量候选、融合和可选重排，发布切换不会让一次查询混用不同代际。内容分块、ACL、删除和任务恢复仍要按对应文档和真实语料验收；hash fallback、tiny fixture 和合成 embedding 只能用于路径回归，不能当作模型质量结论。

公开 Core 入口已有 `RagIngestionWriter.WriteAsync` 和 `ResumeAsync`。writer 使用完整快照：省略旧内容表示删除；未完成任务须先续跑或显式放弃，不能开始另一份写入。embedding 回调由调用方提供，进程崩溃后可能再次调用 provider，不能承诺模型计费 exactly-once。

查询已发布的 `manuals` stream 时，可在引用 `SonnetDB.Core` 的 .NET 10 应用中使用：

```csharp
using SonnetDB.Engine;
using SonnetDB.SemanticContent;

static async Task<IReadOnlyList<SemanticSearchHit>> SearchAsync(
    Tsdb database, EmbeddingProfile profile, float[] queryVector,
    CancellationToken cancellationToken)
{
    var reader = new RagGenerationSearch(database, "manuals", profile);
    return await reader.SearchAsync(
        "pressure calibration", queryVector,
        new SemanticSearchFusionOptions
        {
            TopK = 5,
            MaxCandidatesPerSource = 100,
            MaxScannedDocuments = 10_000,
            MaxDuration = TimeSpan.FromSeconds(5),
            Mode = SemanticSearchFusionMode.ReciprocalRank,
        },
        cancellationToken: cancellationToken);
}
```

这段函数需要调用方先发布 stream，并用摄取时同一个完整 profile 为查询生成真实向量；它不自动生成 embedding。结果 `Id` 是稳定分块 ID，`ContentId` 是父内容，引用应保留这些来源。当前 reader 是有界精确向量扫描，加全文候选与 RRF，不应把它写成大规模 ANN 性能证据。扫描、时间或 hook 出错时不返回部分结果；期限采用协作取消，不能强行终止不遵守 token 的回调。

## 尚未实现的 stdio bridge

M47-U08 规划了 `sonnetdb mcp` stdio bridge 和 WorkBuddy/Cursor/Codex 配置生成，但该 bridge 不能从现有 HTTP MCP 推断为已经交付。文章、README 和自动化脚本在它完成前都应标注为规划或 `NOT_READY`。

参考：[MCP 合同](https://github.com/IoTSharp/SonnetDB/blob/c785f0479686a3d6584787e1a2b40bd67b0e38c5/docs/mcp-contract.md)、[Agent 接入指南](https://github.com/IoTSharp/SonnetDB/blob/c785f0479686a3d6584787e1a2b40bd67b0e38c5/docs/ai-agent-guide.md)、[StudioNative 合同](https://github.com/IoTSharp/SonnetDB/blob/c785f0479686a3d6584787e1a2b40bd67b0e38c5/docs/studio-copilot-contract.md)、[RAG 摄取](https://github.com/IoTSharp/SonnetDB/blob/c785f0479686a3d6584787e1a2b40bd67b0e38c5/docs/rag-ingestion-core.md)、[RAG 检索](https://github.com/IoTSharp/SonnetDB/blob/c785f0479686a3d6584787e1a2b40bd67b0e38c5/docs/rag-search-fusion.md)、[Provider 配置](https://github.com/IoTSharp/SonnetDB/blob/c785f0479686a3d6584787e1a2b40bd67b0e38c5/docs/copilot-providers.md)、[4.0.0 Release](https://github.com/IoTSharp/SonnetDB/releases/tag/v4.0.0)。
