---
title: SonnetDB typed MCP、Copilot 与 RAG：只读工具如何接入 AI
categories: SonnetDB,AI,MCP,RAG
draft: false
---

SonnetDB 的 AI 接入由三部分组成：Provider-neutral 的 Copilot、持久 RAG/向量检索，以及 typed MCP HTTP 工具。它们可以组合使用，但不应混写成“AI 自动拥有数据库写权限”。当前 MCP 入口是 `/mcp/{database}` 的无状态 Streamable HTTP。

## 当前 MCP 工具

进入 MCP 前会校验 Bearer、数据库存在性和 Read grant。工具集合包括：

- `list_databases`、`list_measurements`、`describe_measurement`；
- `sample_rows`、`query_sql`、`explain_sql`；
- `docs_search`、`skill_search`、`skill_load`。

这些工具面向读取、解释和知识检索，没有 SQL 写工具。旧文章中出现的 `sonnetdb-mcp-server`、`show_measurements` 或通用 `execute_sql` 示例不应继续复制。

## Copilot 与 Provider

CopilotAgent 通过 `IChatProvider` 接入不同模型配置，Provider-neutral 表示模型提供方可替换，不表示每个提供方都已经完成真实质量和成本验收。OpenAI-compatible、Azure、Ollama/vLLM 等 profile 应显式配置，模型调用、SQL 草稿和结果提交要分开审计。

## RAG 的版本边界

RAG 摄取会形成带 profile、generation 和 revision 的已发布快照。查询在同一个租约中完成全文候选、精确向量候选、融合和可选重排，发布切换不会让一次查询混用不同代际。内容分块、ACL、删除和任务恢复仍要按对应文档和真实语料验收；hash fallback、tiny fixture 和合成 embedding 只能用于路径回归，不能当作模型质量结论。

## 尚未实现的 stdio bridge

M47-U08 规划了 `sonnetdb mcp` stdio bridge 和 WorkBuddy/Cursor/Codex 配置生成，但该 bridge 不能从现有 HTTP MCP 推断为已经交付。文章、README 和自动化脚本在它完成前都应标注为规划或 `NOT_READY`。

参考：[`mcp-contract.md`](../mcp-contract.md)、[`studio-copilot-contract.md`](../studio-copilot-contract.md)、[`rag-ingestion-core.md`](../rag-ingestion-core.md)、[`rag-search-fusion.md`](../rag-search-fusion.md)、[`copilot-providers.md`](../copilot-providers.md)。
