---
title: SonnetDB MCP 协议支持：以 Model Context Protocol 赋能 AI 驱动数据库管理
categories: SonnetDB,MCP,AI
draft: false
---

MCP 为 AI 客户端提供可发现的工具合同。SonnetDB Server 已实现数据库绑定的 Streamable HTTP MCP，当前 typed contract 版本为 `1.0`。它提供受权限限制的只读数据库访问，而不是让模型拥有任意写权限。

## 使用真实入口

当前入口为 `http://127.0.0.1:5080/mcp/metrics`，其中 `metrics` 是目标数据库。客户端需要支持 Streamable HTTP，并配置 Bearer 身份；具体配置字段按所用客户端文档填写。

旧底稿的 `sonnetdb-mcp-server --db-path ...` 不是这里已核对的启动命令。路线图中的 stdio bridge 也不能直接写成已安装的生产入口。

连接后先读取 `tools/list`。它发布的 `inputSchema` 与 `outputSchema` 是机器可读合同，客户端应先校验输入并忽略允许新增的未知输出字段。

## 工具与调用示例

当前工具包括 `list_databases`、`list_measurements`、`describe_measurement`、`sample_rows`、`query_sql`、`explain_sql`，以及文档和技能检索入口。

下面是协议请求示例，前提是 `cpu` 已存在且含 `usage` FIELD：

```json
{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "tools/call",
  "params": {
    "name": "query_sql",
    "arguments": {
      "sql": "SELECT time, usage FROM cpu ORDER BY time DESC LIMIT 5",
      "maxRows": 5
    }
  }
}
```

`query_sql` 的行数默认 100，上限 1000。成功输出包含列、行、返回数量、`truncated` 与 `contractVersion`。多取一行有助于准确判断截断，但输出上限不等于执行器全部扫描和内存预算。

## 服务端执行授权

进入工具处理前，Server 检查 Bearer 身份、数据库存在性和目标库 Read grant。`list_databases` 只列凭据可见数据库。

`query_sql` 与 `explain_sql` 在 SQL AST 层限制只读语句。当前 MCP 不提供写工具，annotation 也不能代替服务端授权。模型生成 `INSERT` 或 `DELETE` 并不会获得执行许可。

## 处理错误和版本

工具错误设置 `isError=true`，保留人类可读文本与带稳定 code 的 JSON 错误块。机器客户端应先检查错误状态，不能把错误文本解析成成功 schema。

同一 major 的增量合同可新增可选字段和工具；未知 major 应保守拒绝。provider 不可用、取消、权限拒绝与解析错误具有不同含义，不能无界重试。

MCP 证明了工具接入路径，不自动证明真实模型质量、外网数据边界或生产长稳。[typed contract](https://github.com/IoTSharp/SonnetDB/blob/main/docs/mcp-contract.md)、[路线图](https://github.com/IoTSharp/SonnetDB/blob/main/ROADMAP.md)、[4.0.0 发行](https://github.com/IoTSharp/SonnetDB/releases/tag/v4.0.0)说明当前与发行版本的核对入口。
