---
layout: default
title: MCP Typed Contract
description: "SonnetDB MCP v1 工具输入、输出、错误、权限与兼容性合同。"
---

# MCP Typed Contract

SonnetDB Server 通过 `/mcp/{database}` 提供无状态 Streamable HTTP MCP。当前 typed contract
版本为 `1.0`。`tools/list` 返回的 `inputSchema` 和 `outputSchema` 是机器可读的权威合同；本页说明
默认值、安全边界和兼容规则。

## 版本与兼容

- 每个成功的工具返回和 JSON resource 都包含 `contractVersion: "1.0"`。
- `1.x` 只做 extend-only 演进：不删除或重命名工具、现有输入、现有输出字段和稳定错误码，也不改变既有默认值。
- `1.x` 可以新增工具、可选输入、可忽略的输出字段和新的错误码。客户端必须忽略未知工具/输出字段，并在调用前读取 `tools/list`。
- 破坏性变化必须升级 major。客户端可以接受相同 major 的更高 minor，并对未知 major fail closed。
- `docs_search` / `skill_search` 保留既有 `k` 兼容行为：省略、`0` 或负数使用 5，大于 50 按 50 处理。

## 权限边界

所有工具均声明 `readOnly=true`、`destructive=false`、`idempotent=true`、`openWorld=false`。
这些 annotation 只用于发现，不能替代服务端授权：

- 进入 MCP 协议处理前，Server 会校验 Bearer 身份、数据库名、数据库存在性和目标数据库的 `Read` grant。
- `list_databases` 只返回当前凭据可见的数据库；未授权数据库不会出现在结果中。
- `query_sql` / `explain_sql` 在 SQL AST 层只允许只读语句。MCP 不提供写工具，也不能绕过数据库 grant。
- `docs_search`、`skill_search` 和 `skill_load` 同样位于绑定数据库的授权 endpoint 后；provider 未就绪不会放宽权限。

数据库名非法、不存在或无 grant 时，调用尚未进入工具层，Server 分别以 HTTP
`bad_request`、`db_not_found` 或 `forbidden` 结束请求。

## 工具合同

| 工具 | 输入 | 默认值/上限 | 成功输出 |
|---|---|---|---|
| `list_databases` | 无 | 无 | `currentDatabase`, `databases` |
| `list_measurements` | `maxRows?` | 默认 100，范围 1..1000 | `database`, `measurements`, `truncated` |
| `describe_measurement` | `name` | 必填 | `database`, `measurement`, `columns[]` |
| `sample_rows` | `measurement`, `n?` | `n` 默认 5，范围 1..100 | `database`, `measurement`, `requestedRows`, `columns`, `rows`, `returnedRows`, `truncated` |
| `query_sql` | `sql`, `maxRows?` | `maxRows` 默认 100，范围 1..1000 | `database`, `statementType`, `columns`, `rows`, `returnedRows`, `truncated` |
| `explain_sql` | `sql` | 必填 | `database`, `statementType`, `measurement` 与扫描估算字段 |
| `docs_search` | `query`, `k?` | 兼容归一化后范围 1..50 | `query`, `requested`, `hits[]` |
| `skill_search` | `query`, `k?` | 兼容归一化后范围 1..50 | `query`, `requested`, `hits[]` |
| `skill_load` | `name` | 必填、精确名称 | `name`, `description`, `triggers`, `requiresTools`, `body`, `source` |

所有成功输出还包含 `contractVersion`。`query_sql` 自动为未充分限界的 `SELECT` 多取一行，
用于准确设置 `truncated`，最多只向调用方返回 `maxRows` 行。

## 只读 SQL

`query_sql` 接受 `SELECT`、`SHOW MEASUREMENTS`、`SHOW TABLES`、`SHOW VIEWS`、
`SHOW MATERIALIZED VIEWS`、对应的 `DESCRIBE` 以及 `EXPLAIN`。其他语句返回
`read_only_violation`。权限判断和只读分类由 Server 执行，模型生成的 SQL 不构成授权。

## 错误合同

工具失败时 `isError=true`。为兼容已有客户端，`content[0]` 继续是人类可读纯文本；
`content[1]` 是 source-generated JSON：

```json
{
  "code": "read_only_violation",
  "message": "query_sql 仅支持只读语句。",
  "retryable": false,
  "contractVersion": "1.0"
}
```

错误不设置 `structuredContent`，因为 `outputSchema` 严格描述成功返回体。机器客户端应先检查
`isError`，再解析第二个文本块；旧客户端可以继续只读取第一个文本块。

缺失 `inputSchema.required` 参数或传入错误 JSON 类型时，请求会在工具执行前由 MCP 参数绑定器
以协议级 `invalid params` 拒绝，因此不会产生下面的工具级稳定错误体。调用方应以 `tools/list`
发布的 schema 先校验参数；下表错误码适用于参数已经成功绑定并进入工具执行的调用。

| code | 含义 | 通常可重试 |
|---|---|---|
| `invalid_argument` | 参数为空、越界或格式不合法 | 否 |
| `invalid_sql` | SQL 词法或语法不合法 | 否 |
| `read_only_violation` | 语句不属于 MCP 只读子集 | 否 |
| `measurement_not_found` | measurement 不存在 | 否 |
| `skill_not_found` | 技能名称不存在 | 否 |
| `provider_unavailable` | embedding/provider 未就绪或暂不可用 | 是 |
| `request_cancelled` | 调用被取消 | 是 |
| `operation_failed` | 已授权操作执行失败 | 否 |

成功与错误 JSON 均通过 `ServerJsonContext` source generation 序列化，不使用反射型
`JsonSerializerOptions` 重载。
