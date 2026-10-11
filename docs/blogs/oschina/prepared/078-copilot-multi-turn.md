---
title: 多轮对话与 SQL 自修复：可达 3 次重试的智能 Agent
categories: SonnetDB,Copilot,SQL
draft: false
---

多轮对话可以让模型基于先前问题和工具观测继续工作。SonnetDB 的本地 `CopilotAgent` 会规范化消息、按预算裁剪历史，将上下文用于检索与后续规划。这个实现已经存在，但不代表无限记忆或所有 runtime 模式具有同一轮数合同。

## 先看 schema，再生成查询

一段可审阅的对话可以按以下顺序组织：

1. 列出当前凭据可见的 measurement。
2. 查看目标 measurement 的字段类型与原名。
3. 生成带设备、时间过滤和结果上限的只读 SQL。
4. 检查工具结果、截断标志与引用，再回答。

```sql
SELECT time, usage
FROM cpu
WHERE host = 'server-01' AND time >= now() - 1h
ORDER BY time
LIMIT 100;
```

这条示例假设 schema 已确认；模型不能凭历史文本创造一个真实列，也不能凭自然语言请求绕过数据库 grant。

## “3 次”的准确含义

当前本地源码 `MaxSqlRepairAttempts=3`，循环从 attempt 1 到 3。因此是最多三次执行尝试，首次失败后最多两次模型改写，并不是在首次执行之外再无条件重试三次。只有可处理的 `SqlExecutionException` 进入该修复路径。

本地规划另有最多 6 轮、历史 1200 token 的预算。公有宿主工具循环、外部模型超时与结果预算另有合同，不能将内部常量宣传为统一云端服务承诺。

## 读写与未知结果

公开 typed MCP 的 query_sql 是只读工具。内部 Agent 中存在 execute_sql 名称，不能由此宣称外部 MCP 提供任意写能力。当前本地 HTTP Chat 分支拒绝 read-write；真实写操作仍走对应风险审查和显式确认入口。

只读 SQL 改写与写入重试必须区分。写请求断连或超时时，未知结果不等于回滚；不能让模型自行反复写入直到“看起来成功”。权限失败也不应通过改写 SQL 绕过。

## 性能和验收

部分本地数据库工具在进程内执行，但完整链路还有检索、模型 HTTP、事件序列化、权限和取消成本。旧稿“零序列化、毫秒延迟、事务一致性”的通用保证不成立。

本文核对当前文档和源码，没有运行真实模型、完整多轮对话或质量测试。正式 [4.0.0](https://github.com/IoTSharp/SonnetDB/releases/tag/v4.0.0) 与主线新增行为分别核对。

参考：[本地 Agent 实现](https://github.com/IoTSharp/SonnetDB/blob/main/src/SonnetDB/Copilot/CopilotAgent.cs)、[Provider 接线](https://github.com/IoTSharp/SonnetDB/blob/main/docs/copilot-providers.md)、[MCP 合同](https://github.com/IoTSharp/SonnetDB/blob/main/docs/mcp-contract.md)。
