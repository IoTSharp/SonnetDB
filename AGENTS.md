# AGENTS

本文件是 SonnetDB 仓库的长期协作与工程规则，适用于负责源码、测试、文档、样例、发布准备和审查的 AI 智能体。它描述“如何工作”和“哪些合同不能被悄悄改变”；随日期变化的路线、候选版本和验收结果放在 [ROADMAP.md](ROADMAP.md)、[CHANGELOG.md](CHANGELOG.md) 及对应专题证据中。

## 1. 适用范围与优先级

- 结合用户当前请求、机器/上层 AGENTS 和任务所在目录的适用规则开展工作；发生冲突时遵守更高优先级指令，并在交接中说明。
- 本文件不自动授权提交、推送、发布、部署、重启、生产配置变更、数据库迁移或对外消息。只有用户明确授权的动作才能执行。
- 开始工作前必须阅读 [HANDOFF.md](HANDOFF.md)、检查当前分支和工作树，并确认任务涉及的代码、测试、文档和证据入口。
- 共享工作树中已有的修改属于其他工作时，必须保留其文件、字节、暂存状态和归属。不得使用 reset --hard、clean、全量 stash、宽泛 git add 或覆盖式复制来“整理”工作树。
- parity-results 是只保存 parity 结果的独立分支，必须始终保留本地和远端引用，不能合并到 main 或其他开发分支。
- 机器级 PowerShell、远程 shell、网络代理、有界执行、进程回收和 Graphify 禁止规则由上层 AGENTS 负责；本文件不重复改写，也不允许绕过。

## 2. 每项任务的工作流程

### 2.1 先把请求变成可验收的范围

在实现前检查相关代码、文档、测试和现有入口，明确：

- 目标用户是谁，问题是什么，为什么需要这项变化；
- 用户主流程、输入输出、边界和失败行为；
- 与现有 API、文件格式、权限、恢复和部署合同的关系；
- 可以由用户直接检查的验收标准；
- 仍会改变结果的未决问题。

如果问题会实质改变产品行为、兼容性或外部状态，应先提出具体问题并等待答案。请求已经足够明确、且选择不会改变合同的细节可以由智能体自行决定；不因模板化问题阻塞低风险工作。

### 2.2 确认归属并保持最小变更

- 为本任务列出拥有的文件、依赖、验证命令和完成边界；共享文档由主智能体串行维护。
- 优先修复已确认原因，避免顺手重构、重排无关文件或把已完成能力包装成新任务。
- 发现范围外问题时，记录为独立后续项；除非用户重新授权，不混入当前交付。
- 删除、移动或覆盖前必须核对绝对路径和文件归属，只清理本任务创建且确认不再需要的对象。

### 2.3 用分层证据报告状态

分别记录源代码、静态检查、构建/单元测试、真实服务、实际发布物、安装、固定硬件、现场操作、人验收和公开读回。一个层级的结果不能替代另一个层级。

统一使用以下状态：

- PASS：本轮按指定命令完成并有可核验收据；
- FAIL：实际执行且未满足条件；
- UNKNOWN：执行中断、结果不完整或外部状态无法确认；
- NOT_RUN：本轮没有执行；
- BLOCKED：存在明确的外部前置条件，且当前无法继续。

失败、超时、取消和未运行项必须保留原始边界，不能为了让摘要好看而改写为成功。性能、恢复、AOT、固定硬件、长稳和公开可见性必须逐项表述。

### 2.4 测试、界面和可访问性

涉及代码或用户流程时，先检查已有测试和实现，补充能证明行为的最小测试，至少覆盖：

- 正常输入；
- 无效、缺失和空输入；
- 最小值、最大值、单项和批量边界；
- 失败、取消、超时、重试和恢复；
- 可能影响现有用户的回归路径。

涉及页面或组件时，检查键盘导航和焦点、标签与错误消息、颜色对比度、窄屏布局和操作反馈。能自动化的部分自动化；需要真实浏览器、设备或人工判断的部分明确标为 NOT_RUN 或 UNKNOWN。

### 2.5 发布准备与外部写入

发布检查至少包括适用的 restore/build/format/test、配置和环境变量、迁移与兼容风险、监控和错误报告、回滚路径。没有用户明确授权，不得部署、重启、改生产设置或运行迁移。

所有外部创建、上传、POST、发布、关闭或删除动作都必须：

1. 重新读取当前目标状态，确认没有重复或状态漂移；
2. 对一次明确的写入只发出一次请求；
3. 独立读取写入结果和公开/目标侧状态；
4. 对未知结果先对账，再决定是否需要后续动作，不能盲目重试。

## 3. SonnetDB 产品边界

SonnetDB 是使用 C# / .NET 10 实现的单节点多模型数据引擎。九种原生数据模型分别是时序、关系表、KV、JSON 文档、全文、向量、对象、SonnetMQ 和原生属性图；空间/轨迹、流处理/订阅、CDC/本地同步、AI/RAG、治理/恢复/观测是跨模型平台能力。数据库目录是持久化边界，Server 级身份、授权和 .system/mq 等实例资源有各自范围。

稳定产品描述必须遵守这些边界：

- 单节点不等于内置集群、分片、高可用或自动故障转移；
- SQL、对象、MongoDB-like、Redis-like、MQ、GQL 等入口提供的是 SonnetDB 的已声明子集，不自动构成外部协议或官方驱动兼容；
- Graph 是有限合同的 Beta 能力；存在实现入口不等于完成容量、固定硬件、长稳或生产门禁；
- 数据库备份和 Server 实例/MQ、CDC、RAG、外部对象等恢复范围必须分别说明；
- 源码实现、公开包、真实消费者、NativeAOT、安装、现场和生产证据是不同事实层；
- 当前能力和剩余工作以 [ROADMAP.md](ROADMAP.md)、能力证据索引和专题合同为准，不能把计划 ID、历史测试或旧发布说明写成已交付能力。

## 4. 工程与兼容性合同

### 4.1 Safe-only 与固定二进制结构

SonnetDB 自有源码默认禁止使用 unsafe。需要例外时，必须在独立 PR/ADR 中说明原因、范围、替代方案和兼容风险，并取得评审批准。

底层内存操作优先使用：

- `Span<T>`、`ReadOnlySpan<T>`、`Memory<T>`；
- `MemoryMarshal`、`BinaryPrimitives`、`ArrayPool<T>`、`stackalloc`；
- `[InlineArray(N)]` 和 `CollectionsMarshal`。

固定二进制结构（例如 FileHeader、SegmentHeader、BlockHeader）必须是无托管引用的 unmanaged struct，使用：

~~~csharp
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct FileHeader
{
    // ...
}
~~~

多字节字段统一 little-endian。改变已发布布局时必须同时：

1. 升级对应的格式/版本字段；
2. 增加迁移或明确拒绝旧格式的逻辑；
3. 在 CHANGELOG.md 和 PR 描述中记录兼容影响；
4. 增加 round-trip、旧版本和恢复边界测试。

### 4.2 编译、依赖和警告

仓库项目通过 Directory.Build.props 统一启用：

~~~xml
<Nullable>enable</Nullable>
<ImplicitUsings>enable</ImplicitUsings>
<TreatWarningsAsErrors>true</TreatWarningsAsErrors>
~~~

不得用 #pragma warning disable、降低严重级别或删除断言来掩盖与本项目逻辑有关的警告和失败。核心库保持最小运行时依赖；新增依赖须说明用途、许可证、AOT/trim 状态和不可用行为，并通过评审。

### 4.3 JSON、Trim 和 NativeAOT

生产代码中的 `System.Text.Json` 必须使用 source-generated context、`JsonTypeInfo<T>` 或等价的手写 AOT 友好读写：

- 新增或修改 DTO 时同步登记对应 JsonSerializerContext；
- 不使用依赖反射或动态代码生成的 `Serialize(value, options)`、`Deserialize<T>(json, options)` 等重载；
- 不以压制 IL2026、IL3050 等警告代替修复；
- 第三方类型无法 source-generate 时，隔离在明确的非 AOT 边界或写显式转换；
- 涉及 Server、CLI、Native connector、Frame/REST 客户端和发布工具链的 JSON 变更，必须在 AOT 分析或 NativeAOT 发布路径下保持 0 个 IL/AOT 警告；
- AOT/trim 分析只证明被分析的程序集和宿主；发布、运行、消费者、安装和现场仍要分别验证。

### 4.4 SQL 标识符大小写合同

表、measurement、视图、物化视图、关系列和 measurement 的 TAG/FIELD 保留创建时拼写。未加双引号的名称按 `OrdinalIgnoreCase` 解析；双引号名称按 `Ordinal` 精确解析；双引号内的 `""` 表示一个双引号。

表、measurement、视图和物化视图共享 SQL 数据源命名空间，同一命名空间禁止仅大小写不同的新增或重命名。旧目录若已存在冲突，普通引用必须报歧义，迁移工具只能显式检查和处理，不能静默选取、合并或覆盖。跨模型精确同名且引号无法消歧时，必须通过指明对象类型的 DDL 显式迁移。解析器必须把引号信息交给统一绑定阶段，执行器使用绑定后的原名；限定符、别名、CTE、DDL/DML 以及 Point/Line Protocol 摄取入口都遵守同一合同。该规则不改变字符串值、JSON 属性键或其他模型的数据键语义。

## 5. 代码、文档和内容规范

### 5.1 C# 代码

遵循 .NET 命名规范：类型、方法和属性使用 PascalCase，私有字段使用 _camelCase，局部变量和参数使用 camelCase，接口使用 IXxx，常量使用 PascalCase。

所有 public API（类型、方法、属性、构造函数）提供中文 XML 文档注释。参数校验使用现代 ArgumentNullException.ThrowIfNull、ArgumentOutOfRangeException.ThrowIfNegative 等 API；不吞掉 IOException、InvalidDataException 等存储层异常；自定义异常放在 SonnetDB.Exceptions 命名空间并继承 Exception。

### 5.2 文档与发布状态

README 只写稳定产品入口、边界和导航。路线、候选包、验证矩阵、发布状态和历史修正写入 ROADMAP、CHANGELOG、release/audit 或专题文档。任何文档不能把源码存在、mock/fixture、真实服务、公开包、现场或生产证据混为一谈。

博客、新闻投稿、社交平台文章及其配图、人物素材、提示词、JSON/JSONL 台账、审核和发布回执统一放在仓库根目录 `.publishing/`，由 Git 与 Docker 忽略。正文与共用素材放 `.publishing/blogs/`，渠道运行材料放 `.publishing/channels/<channel>/`，新闻投稿放 `.publishing/press-releases/` 或 `.publishing/releases/`；不得重新写入 `docs/blogs/`、`docs/press-releases/` 或其他源码/文档目录。`docs/` 只保留正常产品介绍、使用/开发/运维文档、发行说明、工程审计及这些文档实际需要的图片。

一次性脚本、预览、截图和调试输出只放 `.publishing/tmp/<task>/`，任务结束后清理本任务创建且不再需要的文件。长期只保留必要的定稿、配图、来源/哈希、发布 ID、去重台账及必要失败回执；不得为每一步堆积整页快照、日志或重复副本。迁移旧材料时保留原发布身份与未知结果，不覆盖活跃台账，不改写冻结素材来刷新哈希。浏览器登录档案与凭据仍由各平台原有生命周期管理。

逐篇发布进度、浏览器操作流水及渠道交接写入 `.publishing/` 内相应渠道的 `PROGRESS.md` 或 `HANDOFF.md`。仓库根 `HANDOFF.md` 只记录必要的工程变更、目录约定和本地交接入口，不再追加逐篇发稿日志。

涉及 SonnetDB 讲解人物、文章封面或视频人物时，读取本机 `.publishing/blogs/character-profile.md` 和 `.publishing/blogs/assets/character/manifest.json`；生成前实际使用指定参考图和模型，保留输入/输出 SHA、提示词和目视审图结果。本地材料未恢复时先恢复原档案，不从代码仓库重建或编造批准记录。这个内容发布合同不应复制进普通代码任务。

## 6. 测试和验证基线

单元测试覆盖率目标为 80% 以上，具体门禁以 CI 和任务合同为准。至少保持以下类别的回归覆盖：

| 类别 | 要求 |
| --- | --- |
| 二进制结构 | 使用 AsBytes/MemoryMarshal.Read 做 round-trip |
| 边界 | 空输入、单点、最大值和最小值 |
| 持久化 | WAL replay、Catalog 重载、Segment 读取和目录重开 |
| 并发 | MemTable 或对应模型的并发读写边界 |
| 失败恢复 | 取消、超时、强杀/重开和未知提交结果按合同验证 |

基准测试集中放在 tests/SonnetDB.Benchmarks，报告必须绑定版本、机器、语料、命令和负载；同机粗测不能冒称生产性能。

提交前必须在最终待提交工作树执行与 CI 相同的格式检查：

~~~powershell
dotnet restore SonnetDB.slnx
dotnet format SonnetDB.slnx --verify-no-changes --no-restore --severity warn --exclude extensions/
~~~

格式检查失败时先修复并重跑；检查通过后如果继续修改，提交前必须再次运行。未取得 PASS 不得执行 git commit；不得降低检查级别或以未来 CI 代替提交前检查。不得把 bin/、obj/、.nupkg 等构建产物提交到仓库。

## 7. Git、PR、CHANGELOG 与交接

- Commit 使用 Conventional Commits：`type(scope): summary`；PR 围绕一个交付或缺口，写清变更、对应路线、测试、兼容性和 CHANGELOG。
- 每个 PR 必须更新 CHANGELOG.md 的 [Unreleased]，只记录实际行为或文档变更，不把未来计划写成发布记录。
- 普通代码 issue 以代码完整实现并完成必要的代码层验证为关闭条件：回复实现范围、提交/PR 和验证结果后关闭；部分实现必须说明已完成范围、功能缺口与后续工作并保持开放。发布、NuGet 公开包、部署、生产/现场验收和公开读回独立记录，默认不作为普通代码 issue 的关闭前置条件；issue 本身明确针对发布、部署或线上验收时，仍按其对应合同判断。
- 每次会话开始前阅读根 HANDOFF.md；若缺失，必须在开始修改前创建并记录缺口。每次会话结束前更新它，记录日期/时区、分支和提交、完成/未完成、验证状态、风险边界、授权边界、下一顺序以及本任务创建的进程或临时资源。
- 交接必须与真实工作树一致。若共享 dirty 状态导致无法提交，保留文件并明确说明原因，不能把局部结果写成完整完成。

## 8. 当前仓库地图

~~~text
SonnetDB/
├── src/
│   ├── SonnetDB.Core/             # 多模型核心与持久化
│   ├── SonnetDB/                  # Server、REST/Frame、MCP、Admin、Copilot
│   ├── SonnetDB.Data/             # ADO.NET 与 VectorData
│   ├── SonnetDB.EntityFrameworkCore/
│   ├── SonnetDB.Cli/
│   ├── SonnetDB.Studio/
│   └── SonnetDB.Protocol/
├── connectors/                    # 多语言和 .NET 客户端
├── extensions/                    # 缓存、CAP、媒体及协议扩展
├── samples/                       # 可运行样例
├── tests/                         # Core、Server、生态、Parity 和基准
├── web/                           # Admin/Studio 前端资源
├── docs/                          # 用户文档、设计、审计和发布记录
├── eng/                           # 构建、验证、发布与探针
├── ROADMAP.md
├── CHANGELOG.md
├── HANDOFF.md
├── llms.txt / llms-full.txt
└── SonnetDB.slnx
~~~

实现或文档变化时，先在对应专题页和现有入口中寻找权威合同，保持 README、docs 首页、AI 上下文和路线状态的职责边界。
