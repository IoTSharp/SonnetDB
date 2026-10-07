---
title: SonnetDB typed MCP、Copilot 与 RAG：只读工具如何接入 AI
categories: SonnetDB,AI,MCP,RAG
draft: false
---

SonnetDB 的 AI 接入包含 Provider-neutral 的 Copilot、持久 RAG/向量检索，以及 typed MCP HTTP 工具。它们有不同的入口和配置，可以组合使用。当前 MCP 入口是 `/mcp/{database}` 的无状态 Streamable HTTP，typed contract 版本为 `1.0`。本文依据当前主分支整理，示例没有在本次发布准备中执行，也没有重新验收真实模型。

## 当前 MCP 工具

进入 MCP 前会校验 Bearer、数据库存在性和 Read grant。工具集合包括：

- `list_databases`、`list_measurements`、`describe_measurement`；
- `sample_rows`、`query_sql`、`explain_sql`；
- `docs_search`、`skill_search`、`skill_load`。

这些工具面向读取、解释和知识检索，没有 SQL 写工具。旧文章中出现的 `sonnetdb-mcp-server`、`show_measurements` 或通用 `execute_sql` 示例不应继续复制。

例如，客户端完成 MCP 初始化、绑定存在的数据库 `demo` 并持有其 Read 凭据后，可以发送下面的工具调用。初始化、协议版本协商和传输 header 应由兼容的 MCP 客户端处理：

```json
{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "tools/call",
  "params": {
    "name": "query_sql",
    "arguments": {
      "sql": "SELECT 1 AS ok",
      "maxRows": 10
    }
  }
}
```

调用前读取 `tools/list` 的输入/输出 schema。`query_sql` 的 `maxRows` 默认 100，范围 1–1000；返回的 `truncated` 表示结果窗口被截断，不证明服务端所有扫描和内存都由这个数字硬性限制。模型生成 INSERT 或 DELETE 也不会取得写权限，服务端按 SQL AST 拒绝超出只读子集的语句。机器客户端还需先检查 `isError`，按工具错误合同处理，不能把错误文本当成功结果。

## Copilot 与 Provider

CopilotAgent 通过 `IChatProvider` 接入不同模型配置，Provider-neutral 表示模型提供方可替换，不表示每个提供方都已经完成真实质量和成本验收。OpenAI-compatible、Azure、Ollama/vLLM 等 profile 应显式配置，模型调用、SQL 草稿和结果提交要分开审计。现有 Copilot 使用 `Microsoft.Extensions.AI` 与自研 agent，不把它称为已经迁移到 Microsoft Agent Framework。

StudioNative 的本机 bridge、公网 runtime 和数据库 token 有各自凭据边界。配置只读工具不自动批准向公网模型发送结果；允许名单、数据库 grant、结果预算和内容外发策略都需要满足。受控 fixture 的客户端验证也不等于真实 WebView2、公网 provider 或双网部署验收。

## RAG 的版本边界

`RagIngestionWriter` 的持久摄取使用完整快照，形成带 profile、generation 和 revision 的已发布状态；新快照省略旧内容表示删除，不是增量补丁。Core 入口 `RagGenerationSearch` 在同一个租约中完成全文候选、精确向量候选、融合和可选重排，避免一次查询混用不同代际。这是有界精确检索合同，不把它写成大规模 ANN 性能结论。

本地 Copilot 文档库当前默认仍为 `legacy` 后端，使用持久 RAG 需要显式切换 `StorageMode=rag`、配置一致的 profile，并完成摄取发布；不是所有 `docs_search` 调用都自动切到新 reader。分块权限在计分和重排前过滤，远程重排 hook 本身也不授予内容外发权限。

内容分块、ACL、删除和任务恢复仍要按对应文档和真实语料验收。hash fallback、tiny fixture 和合成 embedding 可以证明受控行为；随机向量还可以测算法集合召回，但都不能代替真实模型在业务语料上的质量与成本证据。

## 尚未实现的 stdio bridge

M47-U08 规划了 `sonnetdb mcp` stdio bridge 和 WorkBuddy/Cursor/Codex 配置生成，但该 bridge 不能从现有 HTTP MCP 推断为已经交付。文章、README 和自动化脚本在它完成前都应标注为规划或 `NOT_READY`。

参考：[`mcp-contract.md`](https://github.com/IoTSharp/SonnetDB/blob/main/docs/mcp-contract.md)、[`studio-copilot-contract.md`](https://github.com/IoTSharp/SonnetDB/blob/main/docs/studio-copilot-contract.md)、[`rag-ingestion-core.md`](https://github.com/IoTSharp/SonnetDB/blob/main/docs/rag-ingestion-core.md)、[`rag-search-fusion.md`](https://github.com/IoTSharp/SonnetDB/blob/main/docs/rag-search-fusion.md)、[`copilot-rag-migration.md`](https://github.com/IoTSharp/SonnetDB/blob/main/docs/copilot-rag-migration.md)、[`copilot-providers.md`](https://github.com/IoTSharp/SonnetDB/blob/main/docs/copilot-providers.md)。
