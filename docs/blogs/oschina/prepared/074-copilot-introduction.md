---
title: SonnetDB Copilot AI 架构解析：Agent Orchestrator + Knowledge Base + Skills + MCP Tools
categories: SonnetDB,Copilot,AI
draft: false
---

SonnetDB Copilot 将模型调用、知识检索、技能资料和数据库工具组合起来。已有在线 `IChatProvider` 与本地编排实现，不能称为空壳；当前自研 `CopilotAgent` 使用 Microsoft.Extensions.AI 相关抽象，不能冒称已经迁移到 Microsoft Agent Framework。

本文解释当前主线架构。正式 [4.0.0 Release](https://github.com/IoTSharp/SonnetDB/releases/tag/v4.0.0) 是独立发行物，主线新增接线与宿主合同应按实际版本核对。

## 编排与模型

本地 Agent 接收对话、裁剪历史、检索文档与技能，再进行有界的规划和工具调用。当前源码中的本地预算为历史 1200 token、最多 6 轮规划，每轮执行一个工具。这些是该实现的参数，不是云端或各宿主公共协议的统一预算。

可以从一个具体问题开始，例如：“列出 metrics 中可见的 measurement，查看 cpu schema，再生成最近一小时平均负载的查询。”先取得 schema，再审阅草稿，比让模型猜列名更有依据。

```sql
SELECT avg(usage) AS avg_usage
FROM cpu
WHERE host = 'server-01' AND time >= now() - 1h
GROUP BY time(1m);
```

示例假设 cpu 已具有 host TAG、usage 数值 FIELD。模型生成 SQL 不是授权，执行前仍要经过服务端语法与权限检查。

## 知识与技能

文档检索提供来源段落，技能资料提供特定主题的调用规则。Embedding 与 Chat 分别配置。builtin hash 向量可用于离线合同和确定性回退验证，不能被宣传为真实语义模型质量；ONNX 语义路径需要明确 tokenizer/input/pooling profile。

技能文件是带 YAML front matter 的 Markdown。检索命中一份技能，不意味着它可以修改权限、调用未授权工具或把其中所有历史示例视为当前 SQL 合同。

## typed MCP 与写入边界

公开 typed HTTP MCP 通过 `tools/list` 提供机器可读 schema，只允许获准只读工具，不能将内部 Agent 的 `execute_sql` 命名扩展为公开 MCP 写工具。外部 Agent 应使用已授权 MCP，不能直接读取数据库目录或内部系统表。

Web、StudioNative 与其它 runtime 模式还有各自的身份、出域与工具预算。当前本地 HTTP Chat 分支拒绝 read-write 请求；真实写操作仍需要对应的风险审查和显式确认流程。

架构存在、mock 通过和真实模型质量是不同证据。本文未执行模型调用、数据库操作或完整双网宿主旅程。

参考：[Provider 与模型目录](https://github.com/IoTSharp/SonnetDB/blob/main/docs/copilot-providers.md)、[typed MCP 合同](https://github.com/IoTSharp/SonnetDB/blob/main/docs/mcp-contract.md)、[本地 Agent](https://github.com/IoTSharp/SonnetDB/blob/main/src/SonnetDB/Copilot/CopilotAgent.cs)、[StudioNative 合同](https://github.com/IoTSharp/SonnetDB/blob/main/docs/studio-copilot-contract.md)。
