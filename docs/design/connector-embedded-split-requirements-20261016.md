# 连接器与嵌入式产品线拆分：确认版需求与分批路线

日期：2026-10-16（Asia/Shanghai）。状态：**主要需求已确认，规划已收敛；首片实现与验证见[实施矩阵](m49-implementation-matrix.md)，本文保留确认时基线。**

用户确认两条产品线：`connectors/` 为各语言原生 HTTP/2 二进制 Frame 远程 SDK；平行的 `embedded/` 为托管 .NET 引擎入口与统一 Native AOT C ABI 的非 .NET 绑定。**首批只完成九模型数据访问；允许分批正式发行，剩余语言、平台和能力后续交付。** 直接重构，不保留旧 SDK/API/ABI 兼容层，不增加过度设计。

## 1. 本次确认的决定

| ID | 主题 | 用户决定与实施含义 |
|---|---|---|
| Q1 | 远程协议 | 必须二进制 Frame；只有具体语言确实无法满足才讨论有证据的例外，不预先豁免任何语言 |
| Q2 | .NET 嵌入式 | .NET 直接托管；C、Go、Rust、Python、Java 共用同一 AOT 库与 C ABI |
| Q3 | VB6 / PureBasic | 仅支持远程连接器，不提供嵌入式版本；取消为其维护嵌入式桥接的规划 |
| Q4 | 平台 | Linux、Windows、macOS 都纳入目标，Linux glibc/musl 与 x64/ARM64 按适用性列矩阵；每批按实际验收声明支持 |
| Q5 | 首批范围 / 位数 | 首批只完成九模型数据访问；引擎/原生库先 64 位，一般 32 位后续再议 |
| Q6 | 嵌入式并发 | 采用建议：本进程多线程生命周期，不新增跨进程同目录共享写承诺 |
| Q7 | 旧兼容 | 不保留旧包门面、旧函数签名或 local/remote 混用入口的兼容层，直接重构 |
| Q8 | Java | 兼容 Java 8；确有需要可提供独立 Java 8 库，优先一个兼容库及 JNI，FFM 按需 |
| Q9 | 正式发行 | 允许分批正式发行；不等待两条线所有目标语言/平台全部完成 |
| Q5 补充 | 后续 AI 联网 | 用户允许显式配置外部模型/API 联网；不将 AI 重新加回首批 |

VB6 固有的 32 位进程是远程客户端，与 64 位 Server 无需同位数；不为它构建 32 位 AOT 引擎或 64→32 嵌入式桥接。ODBC 保持既有后续预留，没有纳入本轮首批。

## 2. 产品结构与依赖边界

| 项目 | connectors 远程 | embedded 嵌入式 |
|---|---|---|
| 正式目标语言 | .NET、C、Go、Rust、Python、Java、VB6、PureBasic | .NET、C、Go、Rust、Python、Java |
| 访问对象 | SonnetDB Server | 本进程引擎与本地数据目录 |
| .NET | 本语言 HTTP/2 / Frame SDK，脱离 Core 运行依赖 | 直接托管访问引擎 |
| C | 原生网络客户端、Frame 编解码与 C API | AOT C ABI 的头文件与示例 |
| Go / Rust / Python / Java | 每语言独立网络协议实现和惯用 API | 共用 AOT 库的 FFI / 安全封装 |
| BASIC | 本语言编码 Frame，使用适当系统/网络库 | 不支持 |
| 共享内容 | 数据/错误/预算/终态规范与测试语料 | 同一引擎与语义、统一 C ABI |

建议目录如下；本次没有创建产品目录或迁移实现：

```text
connectors/
  dotnet/     远程 .NET SDK / 示例入口
  c/          C HTTP/2 Frame 客户端、头文件、示例
  go/         原生 Go SDK / database/sql
  rust/       原生 Rust SDK
  python/     原生 Python SDK / DB-API
  java/       Java 8 兼容的原生远程 SDK
  vb6/        仅远程
  purebasic/  仅远程
embedded/
  dotnet/     托管入口与示例
  native/     唯一 C# Native AOT 导出项目
  c/          统一头文件、C 示例与构建入口
  go/         cgo 绑定
  rust/       FFI 与安全封装
  python/     ctypes 绑定
  java/       Java 8 兼容 JNI 绑定
```

.NET 工程物理位置可遵循现有 `src/` 约定，不复制引擎或再建平行实现。远程端可依赖成熟 HTTP/2/TLS 库；不依赖 SonnetDB AOT 引擎、不通过 dotnet 桥接进程或隐藏本地 Server。嵌入式绑定共享同一 C ABI，不分别实现引擎。

Linux 交付 `.so`，Windows `.dll`，macOS `.dylib`。目标平台矩阵列出 Linux glibc/musl 的 x64/ARM64、Windows x64/ARM64、macOS x64/ARM64；语言与工具链是否适用在 C01 核验，不把规划矩阵当作已支持。VB6 仅列 Windows x86 远程客户端例外，普通 32 位库/引擎不在首批。

## 3. 固定基线与发现

canonical 为 `D:/source/SonnetDB`，main/HEAD `dcadffc7137f5653ec46c55a4171cf5f2d83de03`，落后已获取 origin/main 187 个提交并有共享修改。较新源码与已有 M49 规划位于 `C:/Users/mysti/.codex/worktrees/connector-batch-roadmap/SonnetDB`，HEAD `67a5c8c8d64c36002aee5ebcfcb40634e7d5f482`，分支 `codex/m49-connector-batches-roadmap`。本轮不执行 fetch/pull 或覆盖 canonical 源码。

| 证据 | 已观察到的现状 | 对规划的影响 |
|---|---|---|
| E01：connectors/README 与各语言实现 | 原绑定共同经 C ABI，可访问本地或远程 | 绑定迁至 embedded；原生远程 SDK 是新增实现工作 |
| E02：Native.csproj | PublishAot、NativeLib=Shared 已存在，引用 Data | 复用 AOT 基础，分离不需要的远程/provider 依赖 |
| E03：Data.csproj | 单包含本地/远程并引用 Core，IsAotCompatible=false | 目录移动不足以证明拆包或 AOT 兼容，需独立发布路径验收 |
| E04：frame-protocol.md、*FrameOp.cs | Frame 当前只覆盖部分 SQL/图/文档等操作 | 新增 M49-F01 补齐首批模型操作，不以 REST 回退替代 |
| E05：sonnetdb.h 与 NativeExports | 已有 SQL/bulk/KV/文档/对象/MQ，公共 SQL 类型只有基础五类 | 扩展九模型与精确类型操作矩阵，不以 SQL 可触达代替完整覆盖 |
| E06：NativeResult.From、embedded 结果与 FrameChannel | 部分路径仍先全量物化或缓冲 | 复用 M49 的端到端有界读取、资源、取消和终态工作 |
| E07：ROADMAP 与能力索引 | 九模型与平台能力有各自成熟度；Graph 为 beta | 首批九模型访问，平台能力后续，成熟度如实标记 |

这些是固定基线静态发现，本次没有构建、AOT 发布、连接器运行、性能或消费者验收。源码存在、文档完成与发行完成分别记录。

## 4. Frame 与语言路线

所有首批远程模型操作通过 HTTP/2 二进制 Frame。补齐模型所需的 SQL 写入/参数/适用事务、KV 原子/TTL、文档查询/修改、全文/向量/图、对象分块与资源操作、MQ 消费/订阅等入口。JSON 文档作为模型数据负载传输是允许的，不等于 REST/JSON RPC。

保留当前引擎语义和鉴权授权；支持配置现有 token 经认证头访问。若实现用户名密码握手/刷新，也须设计对应 Frame 路径，不能暗中调用 REST 登录。用户/角色管理 API 不在首批新增范围。错误体、完整终态、取消、背压和写入未知提交按公共合同处理。

h2c 与 TLS/ALPN 分别验证；必须读取实际协商协议并拒绝不满足的结果。不预先因 Java 8、VB6 或 PureBasic 老旧而认定语言无法处理二进制。例外必须明确语言/运行环境、阻碍、替代路径和实际证据。

VB6 可评估直接使用系统 WinHTTP API，无需 SonnetDB 原生桥接。官方文档显示启用 HTTP/2 的选项仍允许较旧 HTTP，因此还必须查询实际协议；这只证明候选网络 API 存在，不证明 VB6 双工/取消/完整模型已通过。

Java 8 是兼容目标；优先一个兼容的远程库与 JNI 嵌入式绑定，必要时才隔离独立 Java 8 包。FFM 为后续可选项，不再把两个本地后端同时完成设为首批门槛。候选 HTTP/2 库的 Java 8 版本、h2c、TLS/ALPN 和双工能力在 C01 实测后选定。

## 5. 首批九模型访问矩阵

| 模型 | 首批逐操作核对与补齐 |
|---|---|
| 时序 | schema、typed/批量写入、时间精度/稀疏、范围读取与聚合 |
| 关系 | DDL/DML、参数、精确类型、约束/索引、JOIN、适用事务与多结果 |
| KV | 二进制值、命名空间、TTL、分页扫描与 NX/XX/CAS 等原子操作 |
| 文档 | collection、CRUD、过滤/投影/排序/分页、局部更新、批处理与索引/校验 |
| 全文 | 索引、analyzer、检索/评分/高亮及适用组合查询 |
| 向量 | 维度/schema、写入更新、精确/近似检索、过滤与索引 |
| 对象 | bucket、元数据/内容、Range、分块/multipart、模型适用版本/条件写操作 |
| MQ | topic、发布/批发、消费组、pull/订阅、ack/nack、offset |
| 图 | schema/索引、点边 CRUD、邻接/遍历/路径、预算与分页，保留 beta 边界 |

最终操作集绑定当前准备发行的引擎支持语义，不扩大为完整 SQL/GQL/S3 等外部兼容承诺。模型创建与必要 schema/索引操作属于数据访问；通用管理控制面另列。

AI/RAG、通用流处理、CDC/同步、备份/用户/角色治理等公共 API 后续单独规划。MQ 模型自己的订阅/ack 仍是本批模型访问；现有鉴权授权、预算、安全、数据持久化和关闭重开/故障恢复验证不能因控制面后移而省略。后续 AI 可由用户显式配置联网，本地数据访问保持进程内运行。

共同合同覆盖 NULL/缺失/空二进制、精确 Int64/时间/向量/UTF-8、参数与标识符大小写、分页/行字节时间预算、错误与未知提交、慢消费/取消/提前关闭、线程/句柄/缓冲所有权和持久化确认。C ABI 不暴露 CLR 对象、内部指针或磁盘结构。保持 Core 零第三方运行时依赖、Safe-only、source-generated JSON 和适用发布路径零 IL/AOT 警告。

直接重构 SDK/API/ABI 不等于任意修改持久格式；现有存储版本、迁移/拒绝旧格式、CHANGELOG 及 PR 规则继续适用。新 ABI/Frame 需明确版本，不静默复用编号解释另一语义；不为旧客户端增加兼容门面或回落链。

## 6. 实施与分批发行

与既有 [M49 专题](m49-connector-analytics-bounded-batches.md) 对齐：保留 C01～C08/R01～R05，增加 D01（目录/包/依赖直接拆分）、F01（首批模型 Frame 补齐），共 15 个 planned 包。

| 阶段 | 交付 | 验收边界 |
|---|---|---|
| G0 | C01/C02：模型操作、平台/语言、协议/ABI/预算/终态矩阵 | 明确当前可交付与缺口，网络候选能力做有界微试 |
| G1 | D01/F01 + R01/R02/R03/C03：.NET/C 两条纵向路径 | 远程纯 Frame，不加载引擎；嵌入式无需 Server，AOT/取消/恢复有实际证据 |
| G2 | C04～C07：其它语言 | Go/Rust/Python/Java 两条线；VB6/PureBasic 仅远程；各端独立验收 |
| G3 | R04/R05：成本、性能与故障 | 同候选、同语料，慢消费、超限、断连/重开和真实资源数据 |
| G4 | C08：分批正式包 | 每批冻结语言/平台/操作与依赖，干净消费者从包运行，通过后可独立发行 |

建议以 .NET/C 的 Windows x64 与 Linux x64 纵向切片开工，以后逐语言/平台扩展；这是工程执行顺序，不删除用户的全平台最终目标。可分批正式发行，每批只声明已通过矩阵，不等待整个目标集合；仍需满足该批适用的仓库质量、AOT、真实服务、恢复和包消费者门槛。没有数据的性能或运行项保持 UNKNOWN/NOT_RUN。

每个已支持模型提供关键数据操作 demo、头文件/类型文档、失败/取消与关闭重开示例；另有跨模型示例。每批不足九模型覆盖时必须明确其局部状态，不能标为“九模型完整支持”。发布、上传、公开下载和消费者运行分别取证。

## 7. 官方核对与当前交付状态

已核对的官方资料：

- [Native AOT deployment](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/)：平台/架构与工具链范围，实际发布仍需逐目标验证。
- [Building native libraries](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/libraries)：共享库包含所需运行时；Native AOT 库不能按普通库方式卸载，数据库 Close 与卸载必须区分。
- [WinHTTP option flags](https://learn.microsoft.com/en-us/windows/win32/winhttp/option-flags)：启用与查询实际 HTTP 协议的选项。
- [Apache HttpClient overview](https://hc.apache.org/httpcomponents-client-5.5.x/index.html)：存在纯 Java HTTP/2 实现；本次不据此宣称 Java 8/指定 TLS 环境已经实跑。

本轮仅更新确认需求、M49 工作包和入口索引、规划 CHANGELOG 与交接。产品实现、构建/AOT、各语言运行、CI、性能、恢复和包消费者验收全部 NOT_RUN。没有未回答的首批架构问题；下一步可按 G0 执行。文档暂未提交：共享工作区有既有修改，本轮未执行提交前完整 restore/Format Check，也未执行发布。
