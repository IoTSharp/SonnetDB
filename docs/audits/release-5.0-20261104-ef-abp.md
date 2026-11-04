# 5.0.0 EF 日期修复与 ABP 消费者复验

日期：2026-11-04（Asia/Shanghai）。SonnetDB 工作树为 `codex/release-5-0-20261103`，基线提交 `29f206fc9238919721f314a7763a66a835cab842`；本记录描述其上的未提交 EF 修复。对应发布验收为 [GH-Issue #230](https://github.com/IoTSharp/SonnetDB/issues/230)。

新 Core 修复组合下，EF 完整套件最终 **309/309 PASS、0 FAIL、0 SKIP**；全新缓存的 ABP 候选完整测试 **22/22 PASS、0 FAIL、0 SKIP**。真实 SonnetDB HTTP 与 Passkey 两项迁移阻断已恢复。此前 20/22 的失败及原始证据保留；当前结果仍是明确标注源码和 DLL 身份的私有候选验收，统一提交与公开正式发行另验。

## 修复原因与源码范围

ABP 使用真实时钟产生亚毫秒 `DateTime`。原 EF 默认映射声明 `DATETIME`、绑定 `DbType.DateTime`，而 5.0 的旧日期参数合同明确只接受完整毫秒；真实保存因此失败。将时钟截断会丢失输入，不能作为兼容修复。

本次只调整 EF provider 及其测试：

- 新增 `SonnetDbDateTimeTypeMapping`，默认使用 `DATETIME2` / `DbType.DateTime2`；SQL 字面量以 UTC 100ns ticks 对应的完整 ISO 日期显式 `CAST AS DATETIME2`。
- `SonnetDbTypeMappingSource` 同时识别默认/显式 `DATETIME2` 和显式旧 `DATETIME`。旧声明继续使用 `DbType.DateTime`；亚毫秒参数由已有 ADO 合同拒绝，亚毫秒字面量由日期映射拒绝，均不截断调用方值。
- 新增 DateTime 回归，覆盖 UTC/Local/Unspecified、默认/显式原生/旧列、nullable、参数、字面量、查询、更新、重开、拒绝后零写入和连接继续使用。另以真实存量行验证 `DATETIME` → `DATETIME2` 后保留旧值、接受亚毫秒更新并重开。
- 原 DateTimeOffset 测试只将默认 DateTime 的预期声明改为 `DATETIME2`。未修改 DateTimeOffset 映射、Core、Data 或 Protocol 源码。

首轮新增测试编译触发 `EF1002`，未进入测试执行，记为失败。随后把测试中的插值 raw SQL 改为仅拼接 provider 生成字面量的固定 SQL，保留 analyzer，没有压制警告。该失败及成功复验均保留。

## EF 验证

| 检查 | 结果 | 证据 |
| --- | --- | --- |
| 日期/偏移定向 | 16/16 PASS，0 SKIP | `ef-focused-02.result.json`、`focused02-trx/` |
| 原完整 EF 套件加首批 DateTime 回归 | 308/308 PASS，0 SKIP | `ef-full-01.result.json`、`full-trx/` |
| 旧日期存量行实际升级及重开 | 1/1 PASS，0 SKIP | `ef-legacy-migration-01.result.json`、`legacy-migration-trx/` |
| Core 修复前的完整 EF 套件 | 309/309 PASS，0 SKIP | `ef-full-02.result.json`、`full02-trx/` |
| 私有候选 EF 5.0.0 pack | PASS | `ef-pack-01.result.json`、`ef-pack-01.stdout.log` |
| 新 Core/Data/Protocol 私有候选 pack | 三包 PASS | `corefix-pack-{protocol,core,data}.result.json` |
| 新 Core 组合最终完整 EF 套件 | 309/309 PASS，0 SKIP | `ef-full-03-corefix.result.json`、`full03-corefix-trx/` |

以上相对证据路径位于忽略目录 `D:/source/SonnetDB/artifacts/release-5.0-20261103-ecosystem/ef-datetime-20261104/`。测试计数均从真实 TRX 读取，未以进程退出码或无测试的支持程序集代替测试通过。

本记录不替代最终工作树的完整 solution restore 与规定 Format Check。最终统一提交、十二类远程发行门禁和公开包消费者复验仍由主发布流程执行。

## 私有包与实际消费者身份

ABP 候选基线为 `IoTSharp/SonnetDB.Abp` 的 `841567fd963621a8656a904121462234c6c80c0e`，独占候选目录 `D:/s5abp-20261103`。使用全新 `nuget-ef-20261104` 缓存和限定来源的 NuGet 配置，没有跨仓库 ProjectReference、复制引擎源码或改共享 NuGet 缓存。

最终使用新的 `ef-datetime-20261104/nuget-corefix/` 混合候选 feed：保留此前 EF 日期修复包，重新从主树打出 Core/Data/Protocol 5.0.0，并沿用另外六个 `cb158831616a5c69992e62152b0746751691ede4` 包。三个新包的 InformationalVersion 标记 `dirty-core-schema`，EF 包保留 `dirty-ef-datetime`；其 nuspec 提交均是 `29f206fc` 基线，不能把基线提交当成已包含全部未提交修复的正式源码身份。源文件 SHA256 和完整 Core 差异另行保存。

旧 `nuget/` feed、旧缓存与 `candidate-manifest.json` 保留供重现 04/05 失败；最终 `candidate-corefix-manifest.json` 保存十包及其中 DLL 的 SHA256、源提交、Core 与两个 EF 生产文件的 SHA256。`core-schema-diff-01.stdout.log` 保存 Core 工作树差异。最终 ABP 使用全新 `D:/s5abp-20261103/nuget-corefix-20261104` 缓存，实际四套输出程序集中的以下四个 DLL 逐一与新缓存比较均一致；原始回执为 `D:/source/SonnetDB/artifacts/release-5.0-20261103-ecosystem/abp-corefix-attempt06/result.json`，重点见 `packageBindings`。

| 程序集 | SHA256 |
| --- | --- |
| `SonnetDB.EntityFrameworkCore.dll` | `f69a7f64ec05b072909f805deaa326f7266f6a3d2ab9eb8baa233c24877efb41` |
| `SonnetDB.Data.dll` | `ace6674a8b55733946be47c03645e53b4e2ba7633e1987420be3061171f99881` |
| `SonnetDB.Core.dll` | `9205a0fe2eda61bab5a9f9597ceb630cfff6c366a5c52856793e0ac125909b89` |
| `SonnetDB.Protocol.dll` | `0c1c24d384f1a161e9efce67a0a53f51dd2d67aa8de4187e8d990a5609b6f9b0` |

测试名中历史保留的 `WithPublicSonnetDB` 不表示本轮使用公开 5.0 包。统一新 SHA 十包、NuGet.org 和最终 Server 身份验证均为独立待办。

## ABP 依赖与标准迁移

候选中央依赖将 SonnetDB EF 固定为 5.0.0、EF/对应 Microsoft 补丁对齐 10.0.14，并显式集中固定 Pkcs/Xml 10.0.14；保留 NuGet 审计和 warnings-as-errors。对固定源码 manifest 的 1969 个原始文件逐一核 SHA256，1967 个保持原样，只有中央依赖及 ModelSnapshot 改变；新增源文件只有这次标准迁移及 Designer。业务代码、ABP 框架、Clock 和模型警告策略未改变，详见 `abp-source-scope-verified.json`。

新 EF 包恢复了八项 SonnetDB 仓储/UOW 测试，首次完整复验 `abp-ef-datetime-attempt04` 为 20 PASS / 2 FAIL。剩余 BookStore Sonnet HTTP 和 Passkey 在种子写入时遇到旧 `DATETIME` 列与原生参数的类型冲突。

随后通过现有标准 EF CLI 10.0.5 生成 `20261103161631_NativeDateTimeTicks5`、Designer 和更新的 ModelSnapshot。CLI 保留低于 EF 10.0.14 的工具版本提示；没有安装或升级工具。新增迁移只包含 43 个 DateTime 和 4 个 DateTimeOffset 列的类型演进；原初始迁移及 Designer 的 SHA256 保持不变：

- InitialSonnetDB：`a2bca447d6e71904fb72adede3a9803339de4372964f9a5fd9f25d682b90089a`。
- InitialSonnetDB.Designer：`45074b2fdf6214d59c21a94a1302ea2b87658c56496d9b4ffbbcfb33869b67e7`。

新生成文件已按候选仓库格式规则调整并重新验证。标准 `dotnet-ef migrations has-pending-model-changes --no-build` 退出 0，确认新快照没有待迁移模型变更；证据为 `abp-pending-model-check-01.result.json` 及原始日志。`abp-candidate-changes.patch` 提供中央依赖和三份迁移文件的可审差异；`abp-patch-audit.json` 保存精确文件清单、SHA256 和操作数量。候选本地 NuGet.Config 仅用于隔离验收，未作为公开消费者配置交付。

## 自引用迁移阻断与恢复

`abp-ef-datetime-attempt05` 在新增迁移后的 restore、原 Format Check 和 Release build 均 PASS，构建 0 警告 / 0 错误；完整测试仍为 20 PASS / 2 FAIL / 0 SKIP。失败发生在迁移本身，错误为 `TableConstraintException: 主键迁移暂不支持自引用外键。`，并非再次出现旧日期参数丢精度问题。

`TableManager.ApplySchemaTransformLocked` 只要表含 Duration/DateTimeOffset/DateTime2 就使用持久化主键 schema 迁移路径；`PrepareSchemaMigrationChildren` 对自引用外键无条件拒绝。因此仅修改 `AbpOrganizationUnits` 的日期列也触发该边界，尽管它的 `Id` 主键未改变。涉及源码为 `src/SonnetDB.Core/Tables/TableManager.cs` 与 `TableManager.SchemaMigration.cs`，本工作项只读定位，没有修改。

没有直接采用 DropForeignKey → AlterColumn → AddForeignKey：现行 EF DDL generator 的 `EndDdlStatement` 使用 `suppressTransaction: true`，不能将这种序列声称为原子迁移。没有删除自引用外键、重写历史迁移或忽略 PendingModelChangesWarning 来取得通过。

主流程的独立 Core worker 随后最小修复该边界：主键约束名、有序列名和完整列定义保持相同（仅允许非键列演进带来的 ordinal 变化）时，不再重写未改变键的入站子表；实际 principal plan 逐行检查旧/新主键编码字节一致，且检查发生在 barrier/journal 持久写入前。原出站外键类型 guard、native journal、格式 floor、全量行和外键校验继续执行，真正主键变更仍走原迁移路径。Core 源文件 SHA256 为 `20cd60abcecdc4c0eeba6d4b358d61a953cb74bc5eff4ebb0a67ed225d745be0`。

本工作项只读复核上述边界，随后按主流程授权重新 pack 并消费修复包；没有使用非原子 DropForeignKey workaround。最终 `abp-corefix-attempt06` 的完整 restore、原 Format Check、Release build 和原始 22 项测试全部 PASS，构建 0 警告 / 0 错误，实际 TRX 为 22 PASS / 0 FAIL / 0 SKIP。三份仅含基类的支持程序集没有独立测试，不额外计数。SonnetDB 的标准 47 列迁移、重复初始化、实际 Identity 登录、CRUD/分页、应用重建读取和 Passkey 存储按原测试执行；没有改业务、Clock、历史迁移或模型警告策略。

新 Core 工作树组合的 EF Release 重建与完整套件再复验为 309/309 PASS、0 SKIP，证据 `ef-full-03-corefix.result.json`。最终 `final-ef-abp-summary.json` 逐项确认 EF 测试实际加载的 Core/Data/Protocol DLL 与 ABP 候选包完全相同；源码测试 EF 自身使用 `dirty-core-schema` 元数据，保留的私有 EF 包使用 `dirty-ef-datetime` 元数据，两个 EF DLL 的 SHA 分开记录，生产源文件 SHA 保持相同。这些源码/候选包结果不等于最终统一提交、精确 SHA 的远程门禁或公开 NuGet 消费者验证。

## 资源与交接

所有命令通过 Cordonix 有界执行，保留程序、argv、PID、父 PID、开始时间、日志和退出码；重编译采用 CPU=2、MSBuild `-m:2`。各次 native invocation 完成时回收其进程树。PostgreSQL 仅用于既有对照测试，使用带任务 label 的新容器，按完整 ID/创建时间/owner 核对后停止和删除；对本轮三个 ID 再独立 inspect 均返回 No such container，最终轮回读保存于 `abp-corefix-container-readback.result.json`，未碰旧 Demo 容器、镜像或共享缓存。

可审源文件、混合候选包、隔离缓存、TRX、原始失败及审计收据保留供后续复验。SonnetDB 的 Git/index、分支、tag、NuGet/GitHub/Docker 发布没有由本工作项改写。根 CHANGELOG/HANDOFF 由主发布流程统一维护。EF/ABP 本地候选阻断已恢复；下一步为最终统一 SHA 十包和公开消费者复验，正式发行未在此验收完成。
