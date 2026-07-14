# AGENTS

本文件定义 AI 协作（如 GitHub Copilot Agent）在 SonnetDB 仓库工作的规范与约束。所有 AI 辅助生成的代码和文档均须遵守此规范。

---

## 项目目标

**SonnetDB** 是一个使用 C# / .NET 10 实现的多模型数据引擎，目标是：

> 八种数据模型，一套引擎；以数据库目录为持久化边界，通过 SQL、标准 API 和管理工具提供一致的数据能力。

当前推进以 [ROADMAP.md](ROADMAP.md) 的 2026-07-14 核查结果为准：先恢复 Parity/nightly 和容量发布证据，再推进 **Milestone 27 — AI / Agent 数据访问与治理** 的真实 provider/Agent 接线，随后处理 M32、M34、M35 和 M36 的未完成能力。

> 当前派单焦点：M20 修复后的完整 Parity stack 与 7 天 nightly 证据；M19 #125 和 M25 #174 的固定目标硬件报告；M27 typed MCP 合同、工业 Demo、在线 `IChatProvider` 接线、本地 embedding、Microsoft Agent Framework 真实集成或如实边界说明，以及 eval/成本指标。M29 功能实现完成，只剩 Studio 安装包和宿主生命周期实机验收。M14 不再标记为完全完成：当前是 `Microsoft.Extensions.AI` 抽象加自研 `CopilotAgent`，本地 ONNX 与在线 provider-neutral 路径尚未接通。M22 仍是上层应用/示例候选，不作为内置里程碑。M32 只处理当前真实缺口，不重复开发已经落地的 update、索引、change feed 和管理界面。M36 必须先用 golden journey 与 gap catalog 证明真实易用性缺口，再按共享合同和模型原生语义推进，不得把 M20/M29/M32 已有交付重新包装成新任务。

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

### 5. JSON 与 Native AOT 铁律

所有生产代码中的 `System.Text.Json` 序列化与反序列化必须保持 source-generated，并且必须支持 Native AOT；此规则不可弱化、不可绕过。

- 新增或修改 JSON DTO 时，必须同步注册到对应的 `JsonSerializerContext`，并使用 `JsonTypeInfo<T>` 或 `JsonSerializerContext` 重载
- `JsonSerializerOptions` 只能作为 source-generated context 的配置输入，不能作为反射元数据入口传给 `JsonSerializer.Serialize` / `Deserialize`
- 禁止使用 `JsonSerializer.Serialize(value, options)`、`JsonSerializer.Deserialize<T>(json, options)` 等依赖运行时反射或动态代码生成的重载
- 禁止为了通过构建而压制 `IL2026`、`IL3050` 或相关 trim/AOT 警告；必须改为 source-generated context、手写 `Utf8JsonReader` / `Utf8JsonWriter`，或显式的 AOT 友好转换器
- 第三方类型若无法纳入 source generation，必须通过手写转换、外部类型自带的 AOT 友好模型接口，或隔离在非 AOT 边界处理，不能回退到反射序列化
- 涉及 Server、CLI、Native connector、Frame/REST 客户端、发布工具链的 JSON 变更，必须在 AOT 分析或 NativeAOT 发布路径下保持 0 个 IL/AOT 警告

### 6. 格式版本变更

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
| 使用反射型 `JsonSerializerOptions` 重载 | 必须保持 source-generated JSON 与 Native AOT 兼容 |
| 修改二进制格式不升级 `FileHeader.Version` | 破坏向后兼容 |
| 压制编译警告（无注释说明） | 维护代码质量 |
| 一个 PR 混入多个 ROADMAP 条目 | 保持 PR 可审查性 |
| 提交 build artifacts（`bin/`、`obj/`、`.nupkg`） | 保持仓库整洁 |
