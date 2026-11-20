---
layout: default
title: "嵌入式部署、Native AOT 与多进程合同"
description: "为 SonnetDB Core、ADO.NET、EF Core 和 Server 选择部署方式，并按证据层理解性能、兼容性和恢复边界。"
permalink: /embedded-deployment-contract/
---

# 嵌入式部署、Native AOT 与多进程合同

本文把嵌入式入口、包边界和验证证据放在同一页，帮助业务应用在选型时区分进程内调用、独立 Server、Native AOT、包兼容性和恢复验收。SonnetDB 是单节点数据库引擎，数据库目录是持久化边界。

## 入口选择

| 入口 | 适合的宿主 | Native AOT 产品口径 | 多进程口径 | 证据入口 |
| --- | --- | --- | --- | --- |
| `SonnetDB.Core` 的 `Tsdb` / `SqlExecutor` | 需要最小依赖、直接使用引擎能力的 .NET 进程 | Core 的实现保持 safe-only 和 AOT 友好设计，但每个宿主仍须独立执行 trim/AOT 分析、publish 和 run。Core 源码可分析不等于发行物或现场通过。 | 同一目录由一个进程拥有。多个进程应改用 Server 或由单一拥有进程提供服务。 | [嵌入式 API](embedded-api.md)、[文件格式与目录布局](file-format.md) |
| `SonnetDB.Data` / ADO.NET | 需要 `DbConnection`、参数、读取器和统一本地/远程连接字符串的应用 | 包级别声明 `IsAotCompatible=false`。ADO.NET 基类和 schema 反射路径仍属于运行时边界。即使某个远程 JSON 路径使用 source generation，也不能把整个包标为 Native AOT 兼容。 | 嵌入式连接仍是单进程目录拥有者。多进程应用使用 `sonnetdb+http` 连接 Server。 | [ADO.NET](ado-net.md)、[SonnetDB.Data 包说明](../src/SonnetDB.Data/PackageReadme.md) |
| `SonnetDB.EntityFrameworkCore` | 已采用 EF Core 模型、LINQ 和 migrations 的应用 | 包级别声明 `IsAotCompatible=false`。EF Core 运行时模型、表达式树和 provider 反射边界需要 CoreCLR 运行时。最小 analyzer 构建通过也不能替代公开包消费者和 NativeAOT publish/run。 | 与 `SonnetDB.Data` 相同。EF Core 不提供跨进程目录协调。 | [EF Core 包说明](../src/SonnetDB.EntityFrameworkCore/PackageReadme.md)、[#396 AOT 审计](audits/issue-396-ef-aot.md) |
| SonnetDB Server + HTTP/Frame | 多进程、多个服务、权限隔离或需要独立运维的部署 | Server 的 Native AOT、安装、启动、连接和升级分别验收。客户端包的 AOT 边界不会自动扩大 Server 的发布边界。 | Server 是目录唯一拥有者，业务进程通过认证连接访问。 | [架构总览](architecture.md)、[发布与打包](releases/) |

选择入口时先确定进程拓扑，再选择 API。把嵌入式连接字符串换成远程连接字符串可以减少应用层改动，但不会把本地目录访问变成跨进程文件协议。

## 通用嵌入式多进程合同

### 单进程

- 一个进程可以创建多个 `SndbConnection` 或多个 Core 调用者。`SonnetDB.Data` 在同一进程内按规范化目录复用 `Tsdb` 实例，并对引用计数。
- 连接关闭只释放本进程的引用。最后一个引用关闭后，底层引擎才会关闭。
- 事务、会话、命令和读取器仍属于各自连接或调用上下文，不能把一个连接的事务对象交给另一个连接。
- 进程内共享不代表所有 API 都可并发无界使用。批量、事务、后台 flush、compaction 和取消仍受各自 API 的预算与生命周期约束。

### 多进程

- 多个进程不得同时以嵌入式方式打开同一数据库目录。WAL 活跃段和目录更新需要独占句柄，第二个进程应收到打开失败并停止重试。
- 不要通过删除锁文件、复制 WAL、直接编辑 segment 或绕过 `SndbConnection` 来制造跨进程共享。这样会破坏恢复和目录一致性合同。
- 需要多进程访问时，启动一个拥有数据库目录的 Server，所有业务进程使用认证的远程连接。Server 的身份、授权、连接超时、会话过期和运维生命周期单独验收。
- 如果业务必须保留嵌入式形态，应由一个明确的拥有进程封装所有数据库操作，并通过业务 IPC 或 HTTP 向其它进程提供服务。IPC 的排队、权限、重试和未知提交结果属于业务封装合同，SonnetDB 不替其声明 exactly-once。
- 同一目录上的网络文件系统、同步盘或容器共享卷只有在目标平台实际验证锁、原子替换、目录持久化和恢复后才能使用。单机单进程测试不能证明这些语义。

### 关闭、崩溃和未知结果

正常 `Dispose` 会结束本进程的连接生命周期，但不等同于数据库备份或跨进程租约。写入确认、进程崩溃、内核崩溃和掉电的持久性取决于 WAL 选项，具体等级见[架构总览](architecture.md)。网络请求断开或进程被终止时，调用方必须按原请求键、事务状态或重开后的数据对账，不能仅凭异常判断“已回滚”。

数据库备份只覆盖其声明的数据库范围。实例级 SonnetMQ、身份、授权、CDC、外部对象和应用 IPC 状态分别按照各自合同备份和恢复，不能把数据库目录副本当作整个平台恢复。

## Native AOT 与轻量嵌入式产品面

### Core 路径

`SonnetDB.Core` 是需要直接使用引擎能力和最小依赖时的嵌入式路径。使用者仍需把以下项目分别作为交付门禁。

1. 对实际宿主运行 `EnableTrimAnalyzer` 和 `EnableAotAnalyzer`。
2. 以固定 RID 执行 NativeAOT publish，并核对 warning、二进制和依赖。
3. 启动发布物执行最小读写、重开和失败路径。
4. 对目标系统、文件系统、架构和部署方式单独记录结果。

上述四步未完成前，产品状态应写作“设计上保持 AOT 友好，实际发布资格未验证”。

### Data 与 EF Core 路径

`SonnetDB.Data` 和 `SonnetDB.EntityFrameworkCore` 的包级别 `IsAotCompatible=false` 是产品合同。应用可在普通 CoreCLR 下使用这两个包，也可以将它们用于远程 Server 访问，但不应把它们放进声称 Native AOT 支持的产品基线。需要 Native AOT 的嵌入式应用应从 Core API 设计入口，避免先建立 ADO.NET 或 EF Core 依赖后再用单个 analyzer 结果推导整包兼容。

`docs/audits/issue-396-ef-aot.md` 记录了当前 EF Core analyzer 探针的证据层。探针中的 ProjectReference、强制 analyzer build、公开 NuGet 消费者、真实 EF 应用和 NativeAOT 运行分别计数，任何一项都不能替代其它项。

## 性能、兼容性与恢复证据地图

业务评审应按下面的顺序读取证据。每一层只回答自己的问题。

| 问题 | 首要入口 | 当前可得证据 | 不能从中推导 |
| --- | --- | --- | --- |
| API 和 SQL 是否有实现 | 对应主题文档、Core/Server 测试、[能力成熟度](capability-maturity.md) | 源码、自动化回归和协议层结果 | 固定硬件吞吐、公开包安装或生产容量 |
| ADO.NET / EF Core 是否可用 | [ADO.NET](ado-net.md)、[EF Core 包说明](../src/SonnetDB.EntityFrameworkCore/PackageReadme.md)、生态样例 | 嵌入式或真实 Server 的定向调用 | Native AOT、跨进程目录共享或业务模型兼容 |
| 性能是否达到目标 | [性能与可靠性近期变更](performance-reliability-updates.md)、[基准说明](benchmarks/ecosystem-soak.md) | 绑定版本、机器、语料和命令的基准或有界 smoke | 未执行的容量、长稳、现场 SLO 或生产因果 |
| 旧版本是否可读 | [文件格式与目录布局](file-format.md)、对应 compatibility/audit | 明确版本、旧样本和恢复回归 | 任意未覆盖格式、跨架构部署或手工改文件 |
| 崩溃和备份能否恢复 | [备份与恢复](backup-restore.md)、[SonnetMQ 实例恢复](sonnetmq-instance-recovery.md)、CrashTests 审计 | WAL replay、重开、备份 manifest 和隔离恢复 | 物理掉电、完整实例、外部对象、业务幂等和 RPO/RTO |
| 能否交付到现场 | [环境与现场资格路线](roadmap-environment-validation.md)、发布审计 | 固定硬件、安装、现场和公开读回的独立回执 | 本机测试、源码存在、历史报告或单次 HTTP 200 |

状态使用 `PASS`、`PARTIAL`、`UNKNOWN`、`NOT_RUN` 和 `BLOCKED`。一个入口的 PASS 不能替换另一个入口的证据。性能、兼容性、恢复、Native AOT、安装和现场资格应在发布或业务验收清单中逐项列出。

## 轻量嵌入式选型

| 目标 | 推荐基线 | 需保留的边界 |
| --- | --- | --- |
| 单进程设备代理或本地工具 | `SonnetDB.Core` + `Tsdb` | 自己负责 SQL/Point 调用、取消、重开和 AOT 发布验证 |
| 既有 ADO.NET 业务代码 | `SonnetDB.Data` | 使用 CoreCLR，单进程目录，需按 ADO.NET 入口验证兼容性 |
| 既有 EF Core 业务代码 | `SonnetDB.EntityFrameworkCore` | 使用 CoreCLR，按 EF 模型、迁移和 provider 翻译验证 |
| 多个服务共享数据 | Server + `SonnetDB.Data` 远程连接 | 认证、授权、会话、升级、备份恢复和网络故障单独验收 |

不要为了“轻量”把数据库目录复制到每个进程，或把 ADO.NET / EF Core 的 analyzer 结果当作 Native AOT 发行资格。轻量产品面首先由单一目录拥有者、明确 API 层和可复现的证据矩阵组成。
