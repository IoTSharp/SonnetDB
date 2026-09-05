# 从 CHANGELOG 移出的历史计划

本文件于 2026-09-05 从基线 `3b5ff768` 的 `CHANGELOG.md` 归档历史 `Planned` 段落，保留原始设想供追溯。下面的版本号是当时的计划标签，不代表发布记录，也不是当前承诺。当前后续工作以 [ROADMAP](../../ROADMAP.md) 为准，核查结论见 [变更日志核查](changelog-verification-20260905.md)。

## 2026-09-05 核查结论

| 历史设想 | 当前处理 |
| --- | --- |
| 0.1.0 / 0.2.0 的基础能力 | 当前已有代码，本地也存在同名 Git 标签；旧 Planned 清单本身不能证明具体发布内容。保留已完成的历史实现条目，后续按标签补齐逐版本归档。 |
| 0.3.0 的压缩、Compaction、page manager 与基准 | 当前已有相关实现，不能因为旧清单仍写 Planned 就判定功能缺失；本地没有 `v0.3.0` 标签。 |
| 将 manifest / WAL / segments 合成单一 `.tsl` 文件 | 未按该设想交付，当前以数据库目录为持久化边界。此设想已被目录模型替代，不列为必须补实现的缺陷。 |
| 独立 `src/SonnetDB.Copilot/` 项目和 Microsoft Agent Framework | 当前实现位于 Server 的 `SonnetDB.Copilot` 命名空间，采用自研 `CopilotAgent` 与 `Microsoft.Extensions.AI`，不是 Microsoft Agent Framework 集成。当前路线应保留实际架构边界。 |
| 本地 ONNX 和 OpenAI-compatible provider | 基线已有真实 ONNX session/batch/profile 与在线 provider 接线；真实目标模型质量、固定硬件性能、成本报告仍有缺口，不能回填为早期全部交付。 |

## Copilot 原始计划快照

### Planned
- **Milestone 14 — SonnetDB Copilot：MCP 工具 + 知识库 + 智能体**：基于 Microsoft Agent Framework 新建独立项目 `src/SonnetDB.Copilot/`，复用现有 `/mcp/{db}` 工具集 + Milestone 13 的向量召回，把"用户文档 / 技能库 / 数据库 schema"统一存入 `__copilot__` 系统库（dogfooding）。Embedding/Chat 走统一 `IEmbeddingProvider` / `IChatProvider` 抽象，**本地 ONNX（bge-small-zh）** 与 **OpenAI 兼容端点（国际 / 国内任意 OpenAI-compat 网关）** 同时支持，可按部署场景切换。新增 HTTP 端点 `POST /v1/copilot/chat`（NDJSON / SSE 流式）+ Web Admin Chat Tab。详见 ROADMAP PR #63 ~ #69。

## 早期版本原始计划快照

## [0.1.0] — *Planned*

> 对应 ROADMAP Milestone 0 ～ Milestone 3

### Added
- 解决方案与项目骨架（`SonnetDB.sln`、`src/SonnetDB`、`src/SonnetDB.Cli`、`tests/SonnetDB.Core.Tests`、`tests/SonnetDB.Benchmarks`）
- `.editorconfig`、`Directory.Build.props`（统一 `LangVersion` / `Nullable` / `TreatWarningsAsErrors`）
- GitHub Actions CI（build + test，矩阵 ubuntu-latest / windows-latest）
- `SpanReader` / `SpanWriter`（`ref struct`，基于 `BinaryPrimitives` + `MemoryMarshal`）
- `[InlineArray]` 工具：`Magic8`、`Reserved16` 等固定缓冲
- 核心 `unmanaged struct`：`FileHeader`、`SegmentHeader`、`BlockHeader`、`BlockIndexEntry`、`SegmentFooter`
- 逻辑模型：`Point`、`DataPoint`、`SeriesFieldKey`、`AggregateResult`
- `SeriesKey` 规范化 + `SeriesId`（XxHash64）
- `SeriesCatalog`（内存 + 持久化）
- `WalWriter` / `WalReader`（append-only + replay）
- `MemTable`
- `SegmentWriter`（BlockHeader + payload + footer index）
- Flush 流程：MemTable → Segment，WAL truncate

---

## [0.2.0] — *Planned*

> 对应 ROADMAP Milestone 4 ～ Milestone 5

### Added
- `SegmentReader`（按 seriesId/time range 裁剪 block）
- `QueryEngine.QueryRaw`（合并 MemTable + 多 Segment）
- 聚合：`min/max/sum/avg/count` + 时间桶 `time(10s)` 分组
- SQL 词法与语法分析器（手写递归下降）
- `CREATE MEASUREMENT` / `INSERT INTO ... VALUES` / `SELECT ... WHERE ... GROUP BY time(...)` 语句支持
- ADO.NET 风格 API：`SndbConnection / SndbCommand / SndbDataReader`

---

## [0.3.0] — *Planned*

> 对应 ROADMAP Milestone 6 ～ Milestone 8

### Added
- 时间戳 delta 编码（block payload V2）
- 值列 delta 编码
- `CompactionEngine`（合并旧 segment）
- page manager + free list
- 将 manifest / wal / segments 合并为单一 `.tsl` 文件
- BenchmarkDotNet 基准（写入/查询/聚合）
- 发布 NuGet 包 `SonnetDB` 0.1.0

---
