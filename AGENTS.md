# AGENTS

本文件定义 AI 协作（如 GitHub Copilot Agent）在 SonnetDB 仓库工作的规范与约束。所有 AI 辅助生成的代码和文档均须遵守此规范。

---

## 项目目标

**SonnetDB** 是一个使用 C# / .NET 10 实现的嵌入式多模型数据库，目标是：

> 可以通过 SQL 语句进行 `INSERT` 和 `SELECT` 时序数据，以数据库目录为持久化边界，嵌入式进程内运行，并可扩展到服务端、多模型和 AI Copilot 场景。

当前推进：**Milestone 27 — Industrial Data Agent 与 AI-ready 产品化路线** 是对外门面与中长期 AI 产品主线，但当前状态为**滞后**；**Milestone 17 — 可观测性与运行时可见性**、**Milestone 18 — VS Code 数据库扩展** 并行推进（详见 [ROADMAP.md](ROADMAP.md)）。

> 当前派单焦点：M27 优先追赶 #183~#188（工具契约、工业 Demo、provider-neutral、本地模型、写入审批二阶段、eval 与成本指标），避免 AI-ready 对外门面长期只停留在 #182 文档第一批；M17 通过 #89~#98 补齐 OTel、结构化日志、诊断端点、Copilot 服务端会话持久化与 Web Admin 监控面板；M18 优先以 #99~#103 打通“远程连接 + Explorer + SQL + 结果视图”闭环。M22 重新定位为“基于 SonnetDB 的上层应用 / 示例方案候选”，不作为 SonnetDB 内置里程碑，暂停 #150~#159 内置派单；只有沉淀出可复用数据库能力缺口时，才拆成独立 Core / Server / Studio PR。M14、M15、M16、M20、M21、M23、M26 已完成或收口；M24/M25 承接 Document 管理面与发布治理，不再把旧 M16 子任务作为当前派单目标。

---

## 强制约束

以下约束**不得违反**。如需例外，必须在 PR 描述中明确说明理由，并通过 reviewer 评审后方可执行。

### 1. 禁止 `unsafe`

**第一版（Milestone 0 ～ Milestone 7）禁止使用 `unsafe` 关键字。**

所有底层内存操作必须通过以下安全 API 完成：

| API | 用途 |
|-----|------|
| `Span<T>` / `ReadOnlySpan<T>` / `Memory<T>` | 内存切片与传递 |
| `MemoryMarshal.CreateSpan` / `AsBytes` / `Cast` / `Read` / `Write` | 类型转换与 reinterpret |
| `BinaryPrimitives` | 小端/大端整数读写 |
| `[InlineArray(N)]` | 固定大小的栈/结构体内嵌缓冲（magic bytes、保留字段） |
| `ArrayPool<T>` | 可复用堆缓冲区 |
| `stackalloc` | 小型栈缓冲 |
| `CollectionsMarshal` | `List<T>` 底层 span 访问 |

### 2. 固定二进制结构体规范

所有固定二进制结构（`FileHeader`、`SegmentHeader`、`BlockHeader` 等）必须：

```csharp
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct FileHeader
{
    // ...
}
```

- 类型必须为 `unmanaged struct`（不含托管引用）
- 字节序统一 **little-endian**（使用 `BinaryPrimitives` 读写多字节字段）
- 修改布局时必须同步升级 `FileHeader.Version`，并在 CHANGELOG 中记录

### 3. 编译器选项

所有项目必须启用：

```xml
<Nullable>enable</Nullable>
<ImplicitUsings>enable</ImplicitUsings>
<TreatWarningsAsErrors>true</TreatWarningsAsErrors>
```

不得通过 `#pragma warning disable` 压制与本项目逻辑相关的警告，除非有充分注释说明。

### 4. 依赖约束

- 核心类库 `src/SonnetDB` **不得**引入任何第三方 NuGet 运行时依赖
- 测试项目可引用 `xUnit`、`xUnit.runner.visualstudio`、`Microsoft.NET.Test.Sdk`
- 基准项目可引用 `BenchmarkDotNet`
- **不得**引入 `Newtonsoft.Json`、`Dapper`、`EntityFramework` 等大型依赖
- 若确有必要引入新依赖，须在 PR 描述中说明理由并通过评审

### 5. 格式版本变更

不得修改已发布的文件二进制格式（`FileHeader`、`BlockHeader` 等结构体布局），除非同步：
1. 升级 `FileHeader.Version` 字段值
2. 在 PR 描述和 `CHANGELOG.md` 中明确标注格式变更
3. 添加格式迁移或拒绝旧格式的处理逻辑

---

## 代码规范

### 命名规范

遵循 [.NET 官方命名规范](https://learn.microsoft.com/zh-cn/dotnet/standard/design-guidelines/naming-guidelines)：

| 元素 | 规范 |
|------|------|
| 类型、方法、属性 | `PascalCase` |
| 私有字段 | `_camelCase` |
| 局部变量、参数 | `camelCase` |
| 常量 | `PascalCase`（不用全大写） |
| 接口 | `IXxx` |

### XML 文档注释

**所有 public API**（类型、方法、属性、构造函数）必须有 XML 文档注释，使用中文撰写：

```csharp
/// <summary>
/// 按时间范围查询原始数据点。
/// </summary>
/// <param name="seriesId">序列标识符。</param>
/// <param name="from">起始时间戳（毫秒，inclusive）。</param>
/// <param name="to">结束时间戳（毫秒，exclusive）。</param>
/// <returns>按时间递增排列的数据点序列。</returns>
public IEnumerable<DataPoint> QueryRaw(SeriesId seriesId, long from, long to) { ... }
```

### 异常处理

- 参数校验使用 `ArgumentNullException.ThrowIfNull`、`ArgumentOutOfRangeException.ThrowIfNegative` 等现代 API
- 不吞掉 `IOException`、`InvalidDataException` 等存储层异常
- 自定义异常继承 `Exception` 并放置在 `SonnetDB.Exceptions` 命名空间

---

## 测试要求

### 覆盖率目标

单元测试覆盖率目标 **≥ 80%**（以行覆盖率计）。

### 必测场景

| 场景 | 要求 |
|------|------|
| 二进制 round-trip | 所有 `unmanaged struct` 必须有 `AsBytes` 写入后 `MemoryMarshal.Read` 读取的 round-trip 测试 |
| 边界条件 | 空输入、单点、最大值/最小值 |
| 持久化恢复 | WAL replay、Catalog 重载、Segment 读取 |
| 并发安全 | MemTable 并发只读测试 |

### 基准测试

关键路径的 BenchmarkDotNet 基准在 **Milestone 8** 集中补齐，包括：

- 批量写入吞吐量（点/秒）
- 时间范围查询延迟
- 聚合查询延迟
- 内存占用

### 测试命名

遵循 `方法名_场景描述_预期结果` 格式：

```csharp
[Fact]
public void QueryRaw_WithTimeRange_ReturnsPointsInOrder() { ... }

[Fact]
public void SeriesKey_WithUnorderedTags_NormalizesToSameKey() { ... }
```

---

## PR 规范

### 标题格式

```
<type>: <简述>
```

`type` 取值范围：

| type | 用途 |
|------|------|
| `feat` | 新功能 |
| `fix` | Bug 修复 |
| `docs` | 文档变更 |
| `refactor` | 重构（不改变行为） |
| `perf` | 性能优化 |
| `test` | 测试相关 |
| `build` | 构建系统 |
| `ci` | CI 配置 |
| `chore` | 杂项（依赖升级、格式等） |

示例：
- `feat: 实现 SpanReader / SpanWriter`
- `test: 补充 SegmentWriter round-trip 测试`
- `docs: 更新 ROADMAP 中 Milestone 3 验收标准`

### PR 内容要求

每个 PR 描述必须包含以下部分：

```markdown
## 变更点
- 简述本 PR 新增/修改了什么

## 对应 ROADMAP
- PR #N：<标题>

## 测试说明
- 新增 X 个测试，覆盖以下场景：...

## 是否破坏兼容
- [ ] 是（说明原因及迁移方案）
- [x] 否

## CHANGELOG 更新
- [ ] 已在 CHANGELOG.md 的 [Unreleased] 段落中记录
```

### 单一职责

**一个 PR 只做一件事**，对应 ROADMAP 中的一个编号。

若发现范围外的 bug，单独创建 PR 修复，不混入当前 PR。

---

## Commit 规范

遵循 [Conventional Commits](https://www.conventionalcommits.org/zh-hans/)：

```
<type>(<scope>): <简述>

[可选正文]

[可选 footer，例如 BREAKING CHANGE: ...]
```

示例：

```
feat(io): 实现 SpanReader 与 SpanWriter

基于 BinaryPrimitives + MemoryMarshal 实现 ref struct 读写工具。
包含 byte/short/int/long/float/double 的 little-endian round-trip 测试。
```

---

## CHANGELOG 更新要求

**每个 PR 必须更新 `CHANGELOG.md` 的 `[Unreleased]` 段落**，在对应分类（`Added / Changed / Fixed / Removed`）下添加条目：

```markdown
## [Unreleased]
### Added
- 实现 `SpanReader` / `SpanWriter`，支持 little-endian 整数与 double 读写（PR #4）
```

---

## 目录约定

```
SonnetDB/
├── src/
│   ├── SonnetDB/                    # 核心类库（无第三方依赖）
│   │   ├── Api/                   # 公共 API：TsdbDatabase / Connection / Command / Reader
│   │   ├── Buffers/               # InlineArray 工具：Magic8、Reserved16
│   │   ├── Catalog/               # SeriesCatalog
│   │   ├── Compression/           # delta / XOR 编码
│   │   ├── Format/                # unmanaged struct：FileHeader 等
│   │   ├── IO/                    # SpanReader / SpanWriter
│   │   ├── Model/                 # Point / DataPoint / SeriesKey 等
│   │   ├── PageStore/             # page manager（Milestone 7）
│   │   ├── Query/                 # QueryEngine / Aggregator
│   │   ├── Sql/                   # Lexer / Parser / AST / Executor
│   │   ├── Storage/               # MemTable / SegmentWriter / Reader / Flush / Compaction
│   │   └── Wal/                   # WalWriter / WalReader
│   └── SonnetDB.Cli/                # 命令行工具
├── tests/
│   ├── SonnetDB.Core.Tests/              # xUnit 单元测试（目录结构镜像 src/SonnetDB）
│   └── SonnetDB.Benchmarks/         # BenchmarkDotNet 基准测试
├── docs/                          # 额外文档
├── .github/
│   └── workflows/
│       ├── ci.yml                 # build + test
│       └── publish.yml            # NuGet 发布
├── .editorconfig
├── Directory.Build.props
└── SonnetDB.sln
```

---

## 禁止事项清单

| 禁止 | 原因 |
|------|------|
| 使用 `unsafe` | 第一版 Safe-only 原则 |
| 在 `src/SonnetDB` 中引入运行时第三方依赖 | 保持零依赖特性 |
| 引入 `Newtonsoft.Json`、`Dapper` 等大型库 | 最小化依赖 |
| 修改二进制格式不升级 `FileHeader.Version` | 破坏向后兼容 |
| 压制编译警告（无注释说明） | 维护代码质量 |
| 一个 PR 混入多个 ROADMAP 条目 | 保持 PR 可审查性 |
| 提交 build artifacts（`bin/`、`obj/`、`.nupkg`） | 保持仓库整洁 |
