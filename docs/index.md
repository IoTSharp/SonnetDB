---
layout: default
title: "SonnetDB 文档中心"
description: "SonnetDB 多模型数据引擎的产品、开发、运维与 AI Agent 文档总览。"
permalink: /
---

SonnetDB 是一款单节点多模型数据引擎：时序、关系表、KV、JSON 文档、全文、向量、对象、消息队列和原生属性图各自保留原生语义，共享一套数据库引擎、目录、权限入口和管理工具。

SonnetDB 可以嵌入 .NET 进程，也可以作为独立 Server 运行。SQL、标准 API、HTTP REST/Frame、CLI、Web Admin、Studio、VS Code、MCP 和多语言连接器分别覆盖应用访问、管理和自动化场景。

<div class="hero-link-row">
  <a class="hero-link hero-link-primary" href="{{ site.home_primary_url | default: '/admin/' }}">{{ site.home_primary_text | default: '打开管理界面' }}</a>
  <a class="hero-link hero-link-secondary" href="{{ site.docs_baseurl | default: '/help' }}/getting-started/">开始使用</a>
  <a class="hero-link hero-link-secondary" href="https://marketplace.visualstudio.com/items?itemName=iotsharp.sonnetdb-vscode">安装 VS Code 扩展</a>
</div>

<div class="callout-grid">
  <section class="callout-card">
    <strong>嵌入式与服务端</strong>
    <p>在进程内打开数据库目录，或连接独立 Server，通过同一套 SQL 和模型 API 工作。</p>
  </section>
  <section class="callout-card">
    <strong>九种数据模型</strong>
    <p>覆盖设备时序、关系表、KV、JSON 文档、全文、向量、对象、消息队列和原生属性图。</p>
  </section>
  <section class="callout-card">
    <strong>管理与 AI</strong>
    <p>提供 Web Admin、Studio、VS Code、Copilot、MCP、权限、备份恢复和可观测性入口。</p>
  </section>
</div>

## 按目标开始

### 使用产品

| 目标 | 先看 |
| --- | --- |
| 启动 Docker、完成首次安装、打开后台 | [开始使用]({{ site.docs_baseurl | default: '/help' }}/getting-started/) |
| 了解 measurement、tag、field、time 和 series | [数据模型]({{ site.docs_baseurl | default: '/help' }}/data-model/) |
| 编写 CREATE、INSERT、SELECT、DELETE 或控制面 SQL | [SQL 参考]({{ site.docs_baseurl | default: '/help' }}/sql-reference/) |
| 复制常用查询和写入模板 | [SQL Cookbook]({{ site.docs_baseurl | default: '/help' }}/sql-cookbook/) |
| 通过 ADO.NET 或 EF Core 接入应用 | [ADO.NET]({{ site.docs_baseurl | default: '/help' }}/ado-net/) · [EF Core]({{ site.docs_baseurl | default: '/help' }}/efcore/) |
| 在进程内直接使用引擎 | [嵌入式与 in-proc API]({{ site.docs_baseurl | default: '/help' }}/embedded-api/) |
| 使用 KV、JSON 文档、全文或向量 | [KV Keyspace]({{ site.docs_baseurl | default: '/help' }}/kv-keyspace/) · [Document Store]({{ site.docs_baseurl | default: '/help' }}/document-store/) · [向量搜索]({{ site.docs_baseurl | default: '/help' }}/vector-search/) |
| 使用对象存储、批量摄取或设备协议 | [对象传输]({{ site.docs_baseurl | default: '/help' }}/object-transfer-manager/) · [批量写入]({{ site.docs_baseurl | default: '/help' }}/bulk-ingest/) · [设备接入]({{ site.docs_baseurl | default: '/help' }}/protocol-ingest/) |
| 使用 Graph Beta、SQL/PGQ 或 GraphRAG | [Graph GQL 入口]({{ site.docs_baseurl | default: '/help' }}/graph-gql/) · [知识图谱合同]({{ site.docs_baseurl | default: '/help' }}/knowledge-graph-contract/) |
| 使用 CLI、备份或恢复 | [CLI 参考]({{ site.docs_baseurl | default: '/help' }}/cli-reference/) · [备份恢复]({{ site.docs_baseurl | default: '/help' }}/backup-restore/) |

### 管理、AI 与 Agent

| 目标 | 先看 |
| --- | --- |
| 浏览 schema、编辑 SQL、使用 staged preview | [SonnetDB Studio]({{ site.docs_baseurl | default: '/help' }}/web-workbench/) |
| 对比 Web Admin、Studio 和 VS Code 的范围 | [管理工具与三面能力矩阵]({{ site.docs_baseurl | default: '/help' }}/management-tools/) |
| 配置 Copilot Provider、模型分组或本地模型 | [Copilot Provider 与模型目录]({{ site.docs_baseurl | default: '/help' }}/copilot-providers/) |
| 让外部模型安全理解 SonnetDB | [AI Agent 指南]({{ site.docs_baseurl | default: '/help' }}/ai-agent-guide/) |
| 对接版本化 MCP typed contract | [MCP Typed Contract]({{ site.docs_baseurl | default: '/help' }}/mcp-contract/) |
| 构建工业 AI、RAG 或语义图片应用 | [工业 AI 应用]({{ site.docs_baseurl | default: '/help' }}/industrial-ai-applications/) · [RAG 摄取 Core]({{ site.docs_baseurl | default: '/help' }}/rag-ingestion-core/) · [语义图片检索]({{ site.docs_baseurl | default: '/help' }}/semantic-image-search/) |

### 工程与运维

| 目标 | 先看 |
| --- | --- |
| 了解组件关系、目录和持久化 | [架构总览]({{ site.docs_baseurl | default: '/help' }}/architecture/) · [文件格式与目录布局]({{ site.docs_baseurl | default: '/help' }}/file-format/) |
| 了解 REST、Frame、MQTT 和 Sparkplug B | [帧协议]({{ site.docs_baseurl | default: '/help' }}/frame-protocol/) · [Sparkplug B]({{ site.docs_baseurl | default: '/help' }}/sparkplug-b/) |
| 接入 Prometheus、OTLP、Grafana 或 Aspire Dashboard | [可观测性]({{ site.docs_baseurl | default: '/help' }}/observability/) |
| 定位慢查询、内存、ThreadPool、Flush 或 Compaction | [故障排查]({{ site.docs_baseurl | default: '/help' }}/troubleshooting/) |
| 查看发布物、Docker 和安装边界 | [发布与打包]({{ site.docs_baseurl | default: '/help' }}/releases/) |
| 查看性能、恢复和长稳证据 | [基准说明]({{ site.docs_baseurl | default: '/help' }}/benchmarks/ecosystem-soak/) · [性能与可靠性近期变更]({{ site.docs_baseurl | default: '/help' }}/performance-reliability-updates/) |
| 查看路线、能力成熟度和证据索引 | [ROADMAP](https://github.com/IoTSharp/SonnetDB/blob/main/ROADMAP.md) · [环境与现场资格路线](roadmap-environment-validation.md) · [能力成熟度]({{ site.docs_baseurl | default: '/help' }}/capability-maturity/) · [十四项能力索引](https://github.com/IoTSharp/SonnetDB/blob/main/docs/audits/fourteen-capability-evidence-index.json) |

## 产品边界

- 数据库以目录为持久化边界，当前不是单文件数据库。
- 单节点部署不提供内置复制、分片、自动故障转移或高可用集群。
- SQL、MongoDB-like、Redis-like、对象、MQ 和 Graph 入口遵循 SonnetDB 自己的合同，不自动承诺外部协议或官方驱动兼容。
- Graph 是有限合同的 Beta 能力；性能、恢复、固定硬件、安装、现场和生产门禁必须查看对应证据。
- Server 实例级 MQ、身份和授权与单库数据有不同备份/恢复范围。
- 文档示例说明用法；发布版本、live schema 和实际验收优先于路线计划或历史报告。

## 文档约定

- 示例优先使用当前测试、样例和匹配版本的包说明。
- 详细行为和限制放在对应主题页，首页只负责导航和稳定产品口径。
- 如果实现与常见数据库习惯不同，主题页会明确标注 SonnetDB 的实际行为。
- 版本、发布物、路线和验收状态属于易变信息，分别以 [发布记录]({{ site.docs_baseurl | default: '/help' }}/releases/)、[ROADMAP](https://github.com/IoTSharp/SonnetDB/blob/main/ROADMAP.md) 和审计/专题文档为准。
