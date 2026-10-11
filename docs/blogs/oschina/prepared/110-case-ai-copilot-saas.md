---
title: 案例：用 SonnetDB + AI Copilot 构建无代码数据分析平台
categories: SonnetDB,Copilot,参考架构
draft: false
---

本文是自然语言分析平台参考架构，使用合成数据，没有真实 SaaS 客户续费率、成功率或成本报告。“无代码”描述用户交互目标，不表示后端不需要 schema、授权和质量验证。

## 先建立租户身份与权限

由实例管理员在控制面执行：

```sql
CREATE DATABASE tenant_demo;
CREATE USER tenant_analyst WITH PASSWORD '<set-your-own-password>';
GRANT READ ON DATABASE tenant_demo TO tenant_analyst;
ISSUE TOKEN FOR tenant_analyst;
```

Token 明文只在签发时返回，权限来自用户当前 grant。旧稿的命名 Token、数据库 ROLE 与任意 EXPIRE 语法不作为当前合同。

由有写权限的账户连接 `tenant_demo` 准备数据：

```sql
CREATE MEASUREMENT production_quality (
    line_id TAG,
    quality_score FIELD FLOAT,
    passed FIELD BOOL
);

INSERT INTO production_quality (time, line_id, quality_score, passed) VALUES
    (1700000000000, 'LINE-02', 97, true),
    (1700000001000, 'LINE-02', 90, false),
    (1700000002000, 'LINE-02', 98, true);
```

`passed` 的判定由业务规则生成；不能未经说明把任意评分阈值称为客户的合格标准。

## 把问题转成可审核查询

用户问“这段时间有多少合格样本”，可以先执行两条范围一致的只读查询：

```sql
SELECT count(*) AS total_samples FROM production_quality
WHERE line_id = 'LINE-02'
  AND time >= 1700000000000 AND time < 1700000003000;

SELECT count(*) AS passed_samples FROM production_quality
WHERE line_id = 'LINE-02' AND passed = true
  AND time >= 1700000000000 AND time < 1700000003000;
```

应用在总数非零时计算比例，并说明两次请求的并发一致性边界。原稿中的任意 FILTER/窗口组合不能未经核对直接当成通用模板。

## 使用现有 AI 与 MCP 合同

当前 `IChatProvider` 与 `IEmbeddingProvider` 分开配置。外部 Agent 经 `/mcp/tenant_demo` 的 typed 只读工具访问数据；工具 schema、grant 和结果预算由服务端校验，模型生成文本不构成授权。

知识库 roots、ingest 和 RAG profile 必须显式配置。内置 hash fallback 只能验证功能，不等于真实语义 embedding。RAG 迁移、模型质量、双网和数据外发应分别取得证据。

## 正确性与成本

模型可能生成错误 SQL、误读空结果或提出无依据结论。纠错次数、总时间和工具调用数应有边界，失败需要如实呈现，不能保证“重试后永远正确”。

缓存键应绑定租户、数据库、有效权限、时间范围和数据版本，避免跨租户复用。真实评测记录模型版本、输入输出、答案标签、延迟与费用；不能沿用原稿未经验证的百分比和单次成本。

参考：[MCP 合同](https://github.com/IoTSharp/SonnetDB/blob/main/docs/mcp-contract.md)、[Provider 说明](https://github.com/IoTSharp/SonnetDB/blob/main/docs/copilot-providers.md)、[RAG 迁移](https://github.com/IoTSharp/SonnetDB/blob/main/docs/copilot-rag-migration.md)。
