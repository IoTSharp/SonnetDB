# AGENTS

本文件定义 AI 协作（如 GitHub Copilot Agent）在 SonnetDB 仓库工作的规范与约束。所有 AI 辅助生成的代码和文档均须遵守此规范。

---

## 项目目标

**SonnetDB** 是一个使用 C# / .NET 10 实现的多模型数据引擎，目标是：

> 九种数据模型，各有原生语义，共享一套引擎；以数据库目录为持久化边界，通过 SQL、标准 API 和管理工具提供一致的治理与访问能力。

当前推进以 [ROADMAP.md](ROADMAP.md) 的 2026-09-05 核查结果和 [综合审计](docs/audits/2026-09-05_project-SonnetDB-report.md) 为准：先验证 MCP 启动修复并恢复完整 Parity/nightly，再按九模型 gap catalog 推进真实用户旅程、恢复边界与性能缺口。已有实现不得重新包装为新任务，构建、mock、真实服务和生产门禁证据必须分开。

> 当前派单焦点：M20 最新七次 scheduled 全部失败，须在修复后重新取得 light/full 与 7 天证据；M19 #125、M25 #174 和 M42 的固定目标硬件报告；M27 真实模型质量/成本、双网客户端和 Agent Framework 边界。typed MCP、工业 Demo、在线 `IChatProvider` 和显式 profile 的本地 ONNX 路径已有实现，不能再称为空壳；hash fallback 和合成模型测试不能计作真实语义证据。M14 仍是 `Microsoft.Extensions.AI` 加自研 `CopilotAgent`，不是 Microsoft Agent Framework。M29 安装包/宿主合同已实现，实机安装仍待验证；M32/M34 已有交付不重做；M35 仍缺持久摄取/resume 等切片。M36 已扩为九模型验收，Graph 引擎仍归 M40；优先远程 KV、对象有界分页/传输、MQ 实例恢复和 SQL/Graph 工作流，单库备份不得冒称覆盖 Server 实例 MQ。M22 继续保留为上层应用候选。

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

### 7. 提交前格式检查铁律

**每次提交代码前，必须在最终待提交的工作树上执行与 CI `Format Check` 相同的检查，并确认通过。**不得以提交后的 CI 检查代替，也不得跳过或降低检查级别：

```bash
dotnet restore SonnetDB.slnx
dotnet format SonnetDB.slnx --verify-no-changes --no-restore --severity warn --exclude extensions/
```

检查失败时必须先修正格式问题，再重新运行检查；检查通过后若继续修改代码，提交前必须重新检查。未取得通过结果不得执行 `git commit`。

### 8. SQL 标识符大小写合同（GH-Issue #211）

- SQL 表名、measurement 名称、视图及物化视图名称、关系表列名及 measurement 的 TAG/FIELD 列名必须保留创建时的原始拼写，不折叠为小写或大写；catalog、`SHOW`/`DESCRIBE` 与结果列元数据使用已保存的名称。
- 未加双引号的名称按 `OrdinalIgnoreCase` 解析；双引号名称按 `Ordinal` 精确解析。创建 `DeviceID` 后，`deviceid`、`DEVICEID` 与 `"DeviceID"` 可引用它，`"deviceid"` 不匹配。该规则不依赖当前语言区域或数据 collation。
- 新建或重命名时，禁止在同一命名空间内产生仅大小写不同的名称，即使加双引号也不允许；表、measurement、视图和物化视图共享 SQL 数据源命名空间。旧 catalog 中已存在的此类冲突须显式迁移；普通引用必须报歧义，允许用双引号精确检查和迁移，不能静默选取、合并或覆盖。跨模型精确同名无法由引号消歧时，须通过指明对象类型的 DDL 显式迁移。
- 限定符、别名、CTE 名及 DDL/DML 中的名称引用遵循相同规则。解析器须把引号信息传给统一名称绑定阶段，执行器使用绑定后的原名；禁止各入口自行折叠或选择不同的比较规则。双引号内的转义 `""` 代表一个 `"`。
- Point、Line Protocol 等摄取入口的 measurement/TAG/FIELD 名称也须解析到已有 schema 的拼写，避免仅因大小写变化而新增列或 series。此合同不改变字符串数据值、数据 collation、JSON 文档属性键或其他模型的数据键语义。

### 9. 分支治理

- `parity-results` 是仅用于保存 parity 结果的独立分支，严禁合并到 `main` 或其它开发分支；分支清理、同步和合并操作必须始终保留该分支及其远端引用。

### 10. 会话交接

- 每次会话开始前必须先阅读仓库根目录的 `HANDOFF.md`，把上次会话记录的未完成事项、待完善内容、边界和注意事项纳入当前计划；若文件不存在，必须在开始修改前创建并记录这一缺口。
- 每次会话结束前必须更新 `HANDOFF.md`，记录本次已完成、未完成、下一次继续顺序、验证结果、风险边界以及下一会话需要知道的其它事项。交接文件必须与实际工作树、提交和分支状态一致，不得把计划或局部 PASS 写成完整完成。
- `HANDOFF.md` 固定放在仓库根目录；会话结束前应把它纳入提交或明确说明为何暂不能提交。

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
