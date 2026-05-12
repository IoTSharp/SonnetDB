---
layout: default
title: "SonnetDB 文档中心"
description: "SonnetDB 当前版本的产品、开发与部署文档总览，覆盖嵌入式、ADO.NET、CLI、服务端与批量写入。"
permalink: /
---

SonnetDB 是一个基于 C# / .NET 10 的时序数据库项目，同时提供嵌入式引擎、ADO.NET 提供程序、CLI、HTTP 服务端、管理后台和内置帮助中心。

当前版本的持久化方式是数据库目录中的多文件布局，不再以“单文件数据库”作为产品描述。文档中的示例、目录结构和启动方式都以当前仓库代码为准。

<div class="hero-link-row">
  <a class="hero-link hero-link-primary" href="{{ site.home_primary_url | default: '/admin/' }}">{{ site.home_primary_text | default: '打开管理界面' }}</a>
  <a class="hero-link hero-link-secondary" href="{{ site.docs_baseurl | default: '/help' }}/getting-started/">开始使用</a>
</div>

<div class="callout-grid">
  <section class="callout-card">
    <strong>嵌入式优先</strong>
    <p>可以直接在进程内打开数据库目录，使用 <code>Tsdb</code>、SQL 执行器或 ADO.NET 访问。</p>
  </section>
  <section class="callout-card">
    <strong>统一访问面</strong>
    <p>本地嵌入式、远程 HTTP、CLI 和 ADO.NET 共享一套相近的 SQL 与连接方式。</p>
  </section>
  <section class="callout-card">
    <strong>服务端可运维</strong>
    <p><code>SonnetDB</code> 提供首次安装、用户授权、Token、SSE、帮助文档和管理后台。</p>
  </section>
</div>

## 从哪里开始

| 如果你要做什么 | 建议先看 |
| --- | --- |
| 启动 Docker 镜像、完成首次安装、打开后台 | [开始使用]({{ site.docs_baseurl | default: '/help' }}/getting-started/) |
| 了解 measurement、tag、field、time 和 series 的关系 | [数据模型]({{ site.docs_baseurl | default: '/help' }}/data-model/) |
| 编写 `CREATE/INSERT/SELECT/DELETE` 或控制面 SQL | [SQL 参考]({{ site.docs_baseurl | default: '/help' }}/sql-reference/) |
| 想直接复制一段常用 SQL 模板 | [SQL Cookbook]({{ site.docs_baseurl | default: '/help' }}/sql-cookbook/) |
| 使用 Web Admin Workbench 浏览 schema、编辑 SQL、做 staged preview | [SonnetDB Workbench]({{ site.docs_baseurl | default: '/help' }}/web-workbench/) |
| 在进程内直接使用引擎 | [嵌入式与 in-proc API]({{ site.docs_baseurl | default: '/help' }}/embedded-api/) |
| 通过 ADO.NET 访问本地或远程实例 | [ADO.NET 参考]({{ site.docs_baseurl | default: '/help' }}/ado-net/) |
| 使用 `sndb` 命令行工具 | [CLI 参考]({{ site.docs_baseurl | default: '/help' }}/cli-reference/) |
| 走 Line Protocol、JSON 或批量 VALUES 快路径 | [批量写入]({{ site.docs_baseurl | default: '/help' }}/bulk-ingest/) |
| 了解当前组件关系与存储路径 | [架构总览]({{ site.docs_baseurl | default: '/help' }}/architecture/) 和 [文件格式与目录布局]({{ site.docs_baseurl | default: '/help' }}/file-format/) |
| 复核最近的性能、可靠性和格式演进 | [性能与可靠性近期变更]({{ site.docs_baseurl | default: '/help' }}/performance-reliability-updates/) |
| 查看发布产物与打包说明 | [发布与打包]({{ site.docs_baseurl | default: '/help' }}/releases/) |

## 当前产品形态

SonnetDB 现在由四条主线组成：

1. 嵌入式引擎 `SonnetDB`
2. ADO.NET 提供程序 `SonnetDB.Data`
3. CLI 工具 `SonnetDB.Cli`
4. 服务端 `SonnetDB`

这几部分共享同一套底层存储格式和大部分 SQL 行为。服务端额外增加了：

- 首次安装流程
- 用户、授权、Token 管理
- `/admin/` 管理界面
- `/help/` 静态帮助中心
- `/v1/events` SSE 事件流
- `/healthz` 与 `/metrics`

## 文档约定

- 示例优先使用当前测试和包说明中已经验证过的写法。
- 详细示例统一放在具体主题页，首页只保留导航和产品定位。
- 如果代码行为与常见 TSDB 习惯不同，会在对应页面明确标注当前真实行为。
