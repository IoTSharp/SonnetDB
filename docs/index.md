---
layout: default
title: "SonnetDB 文档中心"
description: "SonnetDB 当前版本的产品、开发与部署文档总览，覆盖工业边缘数据引擎、Copilot、Studio、嵌入式、ADO.NET、CLI、服务端和批量写入。"
permalink: /
---

SonnetDB 是一个面向 C# / .NET 10 工业边缘应用的本地优先数据引擎。它以设备遥测和时序数据为核心起点，同时提供关系表、KV、JSON 文档、全文检索、向量检索、Hybrid Search、对象桶、本地消息队列、SonnetDB Studio 和 Copilot 智能协作能力。

它的产品门面不是“再做一个大而全数据库”，而是让设备网关、采集程序、轻量 MES / SCADA、离线数据记录器和 Industrial Data Agent 可以在一个本地数据库目录里完成主要数据工作。

当前版本的持久化方式是数据库目录中的多文件布局，不再以“单文件数据库”作为产品描述。文档中的示例、目录结构和启动方式都以当前仓库代码为准。

<div class="hero-link-row">
  <a class="hero-link hero-link-primary" href="{{ site.home_primary_url | default: '/admin/' }}">{{ site.home_primary_text | default: '打开管理界面' }}</a>
  <a class="hero-link hero-link-secondary" href="{{ site.docs_baseurl | default: '/help' }}/getting-started/">开始使用</a>
</div>

<div class="callout-grid">
  <section class="callout-card">
    <strong>嵌入式与服务端</strong>
    <p>可在进程内打开数据库目录，也可作为 HTTP 服务运行，并通过同一套 SQL 访问。</p>
  </section>
  <section class="callout-card">
    <strong>工业边缘数据核心</strong>
    <p>支持设备时序、关系表、KV、JSON 文档、全文、向量、Hybrid Search、对象桶和本地消息队列。</p>
  </section>
  <section class="callout-card">
    <strong>AI、Agent 与运维</strong>
    <p>内置聚合、窗口、预测、PID、地理空间、Copilot、MCP、权限、Token、备份恢复和管理后台。</p>
  </section>
</div>

## 从哪里开始

| 如果你要做什么 | 建议先看 |
| --- | --- |
| 启动 Docker 镜像、完成首次安装、打开后台 | [开始使用]({{ site.docs_baseurl | default: '/help' }}/getting-started/) |
| 了解 measurement、tag、field、time 和 series 的关系 | [数据模型]({{ site.docs_baseurl | default: '/help' }}/data-model/) |
| 编写 `CREATE/INSERT/SELECT/DELETE` 或控制面 SQL | [SQL 参考]({{ site.docs_baseurl | default: '/help' }}/sql-reference/) |
| 想直接复制一段常用 SQL 模板 | [SQL Cookbook]({{ site.docs_baseurl | default: '/help' }}/sql-cookbook/) |
| 使用 SonnetDB Studio 浏览 schema、编辑 SQL、做 staged preview | [SonnetDB Studio]({{ site.docs_baseurl | default: '/help' }}/web-workbench/) |
| 了解 Copilot 如何辅助 SQL、排障和知识检索 | [SonnetDB Studio]({{ site.docs_baseurl | default: '/help' }}/web-workbench/) 和 [Copilot 技能库](https://github.com/IoTSharp/SonnetDB/tree/main/copilot/skills) |
| 构建工业 AI 应用或 Industrial Data Agent | [使用 SonnetDB 构建工业 AI 应用]({{ site.docs_baseurl | default: '/help' }}/industrial-ai-applications/) |
| 在进程内直接使用引擎 | [嵌入式与 in-proc API]({{ site.docs_baseurl | default: '/help' }}/embedded-api/) |
| 使用轻量 KV keyspace 存储 metadata 或小对象 | [KV Keyspace]({{ site.docs_baseurl | default: '/help' }}/kv-keyspace/) |
| 通过 ADO.NET 访问本地或远程实例 | [ADO.NET 参考]({{ site.docs_baseurl | default: '/help' }}/ado-net/) |
| 通过 EF Core DbContext 与现有 .NET 应用集成 | [EF Core Provider]({{ site.docs_baseurl | default: '/help' }}/efcore/) |
| 使用 `sndb` 命令行工具 | [CLI 参考]({{ site.docs_baseurl | default: '/help' }}/cli-reference/) |
| 走 Line Protocol、JSON 或批量 VALUES 快路径 | [批量写入]({{ site.docs_baseurl | default: '/help' }}/bulk-ingest/) |
| 了解当前组件关系与存储路径 | [架构总览]({{ site.docs_baseurl | default: '/help' }}/architecture/) 和 [文件格式与目录布局]({{ site.docs_baseurl | default: '/help' }}/file-format/) |
| 复核最近的性能、可靠性和格式演进 | [性能与可靠性近期变更]({{ site.docs_baseurl | default: '/help' }}/performance-reliability-updates/) |
| 查看发布产物与打包说明 | [发布与打包]({{ site.docs_baseurl | default: '/help' }}/releases/) |

## 当前产品形态

SonnetDB 现在由这些主线组成：

1. 多模型核心库 `SonnetDB.Core`
2. HTTP 服务端 `SonnetDB`
3. ADO.NET 提供程序：NuGet 包 `SonnetDB`，命名空间 `SonnetDB.Data`
4. EF Core Provider：NuGet 包 `SonnetDB.EntityFrameworkCore`
5. CLI 工具 `SonnetDB.Cli`
6. SonnetDB Studio 与 CopilotDock
7. C、Go、Rust、Java、Python、VB6、PureBasic 等连接器
8. 面向 AI / Agent 的 `llms.txt`、MCP 工具和工业应用文档

这几部分共享同一套底层存储格式和大部分 SQL 行为。服务端额外增加了：

- 首次安装流程
- 用户、授权、Token 管理
- `/admin/` 管理界面
- `/help/` 静态帮助中心
- `/v1/events` SSE 事件流
- `/healthz` 与 `/metrics`
- `/mcp/{db}` 工具入口
- Copilot 云端桥接、上下文摘要和写入审批

## 文档约定

- 示例优先使用当前测试和包说明中已经验证过的写法。
- 详细示例统一放在具体主题页，首页只保留导航和产品定位。
- 如果代码行为与常见 TSDB 习惯不同，会在对应页面明确标注当前真实行为。
