---
title: SonnetDB 全文维护实操：Analyze、评分解释与重建终态
categories: SonnetDB
draft: false
---

# SonnetDB 全文维护实操：Analyze、评分解释与重建终态

SonnetDB 的全文维护可以沿着“看分词、解释命中、审批重建、核对终态”逐步排查。本文把 FullText 工作台已有的 Analyze 和审批重建，与 HTTP 接口提供的评分解释、重建状态串成一条可复核的流程。

![SonnetDB 全文维护：Analyze、评分解释与重建状态](https://img2024.cnblogs.com/blog/1725297/202610/1725297-20261016130447070-1688085927.png)

图：AI 主题示意图，并非实测截图；Analyze、评分解释与重建有不同入口，不表示在线无阻塞重建。

## 先固定环境和最小数据

本文核查日期为 2026-10-16，源码基线为 `dcadffc7137f5653ec46c55a4171cf5f2d83de03`。当次公开 GitHub 正式 Release 和 NuGet Core 页面显示 4.0.0；这不证明当前源码里的所有管理接口已经包含于读者安装的包。先核对自己的 Server 版本及入口，不把工作台预览版或源码实现当作正式版全量交付。

在一个可写的测试数据库执行：

```sql
CREATE DOCUMENT COLLECTION logs;
INSERT INTO logs (id, document)
VALUES ('a', '{"message":"pump alarm"}');
CREATE FULLTEXT INDEX ft_logs_message ON logs ('$.message') USING unicode;
```

这组对象名称和语句来自仓库管理合同测试。读者应保存实际 SQL 结果，后续请求的数据库、collection、index、field 必须指向同一组对象。本文不重新展开全文入门或 BM25 公式，已有入门文章可从系列索引进入。

## Analyze 先回答“查询变成了哪些词”

Web Admin 或复用该页面的 Studio 中，选择目标数据库和 FullText 索引，切到 Analyzer，选择 `unicode`，输入 `Pump alarm` 后执行 Analyze。当前页面调用 `/v1/db/{db}/fulltext/analyze`，显示 token 及其 offset。

也可以在 PowerShell 7 中发送同一只读诊断请求。`$env:SONNETDB_TOKEN` 应由已有凭据机制注入，示例不要求把令牌写进脚本：

```powershell
$base = 'http://127.0.0.1:8080' # 改成已确认的 Server 地址
$db = 'ftdiag'                # 改成上述对象所在数据库
$headers = @{ Authorization = 'Bearer ' + $env:SONNETDB_TOKEN }
$analyze = Invoke-RestMethod -Method Post `
    -Uri "$base/v1/db/$db/fulltext/analyze" -Headers $headers `
    -ContentType 'application/json' -TimeoutSec 30 `
    -Body '{"tokenizer":"unicode","text":"Pump alarm"}'
$analyze.tokens | Format-Table text,startOffset,endOffset,positionIncrement
```

按当前 UnicodeTokenizer 的代码，ASCII 示例应形成 `pump`（0–4）和 `alarm`（5–10），位置增量均为 1；这是源码推导的预期，不能标为本轮实测输出。offset 对应原始文本的位置，结束位置不包含在 token 中。不要据此推断所有语言的分词效果。

`analyze` 接受 `unicode`、`cjk`、`jieba`。它检查指定 tokenizer，不会修改现有索引。若要比较当前索引分析结果和候选 tokenizer，使用 `/fulltext/analyzer-diff`，例如请求体：

```json
{"collection":"logs","index":"ft_logs_message","text":"Pump alarm","tokenizer":"cjk"}
```

响应中的 `current`、`candidate`、`added`、`removed` 用于比较。候选输出不等于配置已保存，也不等于索引已重建。

## 用原文档 ID 解释一次命中

当前 FullText 工作台展示搜索分数和命中详情；源码中没有把 Relevance Explain 接成专用按钮。需要评分解释时，使用已实现的 HTTP 或 `SndbFullTextClient.ExplainRelevanceAsync` 入口：

```powershell
$explain = Invoke-RestMethod -Method Post `
    -Uri "$base/v1/db/$db/fulltext/relevance-explain" -Headers $headers `
    -ContentType 'application/json' -TimeoutSec 30 `
    -Body '{"collection":"logs","index":"ft_logs_message","field":"$.message","query":"pump","documentId":"a"}'
$explain | Select-Object documentId,score,tokenizer,queryTerms
$explain.contributions | Format-Table term,matched,scoreContribution
```

核对 `documentId` 是 `a`、tokenizer 与索引一致、查询词包含 `pump`，并查看该词的 `matched`。`scoreContribution` 的 DTO 明确称为近似 BM25 贡献，不应把它宣传成任意查询下各项精确可加的证明，也不把分数当百分比、概率或跨索引可直接比较的质量指标。

Analyze、settings、analyzer-diff、relevance-explain 都是需要数据库 Read 权限的诊断入口，虽使用 HTTP POST，仍不表示写操作。

## 审批后重建，再读取原对象的状态

工作台的 Rebuild 先生成审批计划，目标包含数据库、collection 和 index。确认后走维护入口，`targetModel=document_fulltext`，实际调用 Core 的 `RebuildFullTextIndex`。只读身份不能据此写入。

HTTP 专用重建入口是 `/fulltext/rebuild`，需要 Write 权限。下面代码只在操作者明确决定重建这个测试索引时执行一次：

```powershell
$target = '{"collection":"logs","index":"ft_logs_message"}'
$result = Invoke-RestMethod -Method Post `
    -Uri "$base/v1/db/$db/fulltext/rebuild" -Headers $headers `
    -ContentType 'application/json' -TimeoutSec 60 -Body $target
$result | Select-Object state,processedDocuments,totalDocuments,error,startedUtc,completedUtc
```

当前实现先同步执行重建，再返回状态；它不是提交一个可跨进程恢复的后台任务。状态读取是另一入口：

```powershell
$status = Invoke-RestMethod -Method Post `
    -Uri "$base/v1/db/$db/fulltext/rebuild/status" -Headers $headers `
    -ContentType 'application/json' -TimeoutSec 30 -Body $target
$status | Select-Object state,processedDocuments,totalDocuments,error,completedUtc
```

`pending` 表示尚无这次对象上的重建记录，不能写为成功；`completed` 和处理计数表示该次调用的终态，不证明恢复、在线无阻塞、数据规模或长稳验收。请求超时后，先核对同一数据库和索引的状态，不因浏览器报错立即重复提交。索引被删除或不存在时，保留实际错误并重新确认对象身份。

## 怎样保存可用的排查记录

一次排查至少保存：Server 版本、数据库/索引名称、输入文本、tokenizer、原文档 ID、原始响应和重建前后时间。先解决字段路径或 tokenizer 错配，再讨论相关性；只看截图中的分数无法判断索引主数据是否一致。

本轮仅核对源码、DTO、专题文档和现有测试代码，未启动数据库、执行上述请求或重跑产品测试；没有真实语料相关性、固定硬件性能或生产重建证据。Graph 仍为 Beta，与本文全文维护能力分别验收。

## 可核查依据

- [管理工具与三面能力边界](https://github.com/IoTSharp/SonnetDB/blob/5424b66672e37f49a2a5633fdde0b54a9b1f0290/docs/management-tools.md)
- [FullText 页面实际入口](https://github.com/IoTSharp/SonnetDB/blob/5424b66672e37f49a2a5633fdde0b54a9b1f0290/web/src/components/FullTextSearchWorkbench.vue)
- [HTTP Analyze、Explain 与重建合同](https://github.com/IoTSharp/SonnetDB/blob/5424b66672e37f49a2a5633fdde0b54a9b1f0290/src/SonnetDB/Endpoints/Routes/ManagementContractEndpoints.cs)
- [对应管理合同测试](https://github.com/IoTSharp/SonnetDB/blob/5424b66672e37f49a2a5633fdde0b54a9b1f0290/tests/SonnetDB.Tests/ManagementContractEndpointTests.cs)
