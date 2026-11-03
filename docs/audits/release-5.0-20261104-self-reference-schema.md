# 5.0 原生非主键 schema 迁移与自引用外键修复

日期：2026-11-04（Asia/Shanghai）。修复位于 `29f206fc9238919721f314a7763a66a835cab842` 之后的共享工作树，尚未由主流程提交或固定最终发布 SHA。本次范围为一个 Core 私有迁移分支、真实回归和本报告；未修改 EF、MCP、其它 CI、根 HANDOFF/CHANGELOG 或 Git index。

## 已复现的实际阻断

真实 ABP 标准升级把 43 个 DateTime 和 4 个 DateTimeOffset 列迁到原生类型时，在 `AbpOrganizationUnits` 的非主键列 ALTER 遇到 `TableConstraintException: 主键迁移暂不支持自引用外键`。该表的 `Id` 主键没有变更；当时 ABP 为 20/22，尚未取得 22/22。

独立 Core 回归先创建带 `DATETIMEOFFSET` 的 `nodes`，含原有 `stamp DATETIME`、主键 `id`、自引用 `parent_id → nodes(id)`、覆盖日期索引、唯一标签索引，以及另一表 `links → nodes(id)`。写入 root、child 和外部引用三条真实关联行，再执行：

```sql
ALTER TABLE nodes ALTER COLUMN stamp TYPE DATETIME2
```

修改 Core 前，实际测试 **0 PASS / 1 FAIL / 0 SKIP**。异常与 ABP 相同；调用链为 `ApplySchemaTransformLocked` → `ApplyPrimaryKeySchemaMigrationLocked` → `PrepareSchemaMigrationChildren` 的无条件自引用拒绝。原始回执为 `artifacts/release-5.0-20261104-self-reference-schema/before-self-reference.trx`。

原因是原生类型的非主键转换也必须走完整持久 schema 迁移；原 children 准备代码把所有入站引用都视为主键变更。回退到普通 `ApplySchemaTransform` 会绕开既有 native 格式 floor/journal，因此没有采用。也没有通过临时删除外键、执行多个非原子 DDL 后再补约束解决此问题。

## 最小实现与完整性条件

仅修改 `src/SonnetDB.Core/Tables/TableManager.SchemaMigration.cs`：

1. 主键约束名和有序主键列名必须按 `Ordinal` 完全相同。
2. 每个主键 `TableColumn` 的完整 record 定义必须相同，仅归一化 `Ordinal`。非主键列增删可以移动列位置，而键编码按主键名称和次序提取值；类型、DECIMAL precision/scale/explicit、长度、NULL/default、生成/ROWVERSION 等定义均不得被忽略。
3. principal 仍调用原 `PrepareSchemaMigration`，完整执行行转换、唯一性、索引及 row/default/CHECK 校验。对它已准备的每一行，要求 `OriginalPrimaryKey` 与 `UpdatedRow.PrimaryKey` 字节完全相同；不满足时在任何 barrier、journal 或 raw 写入前拒绝。
4. 满足上述条件才不为入站引用生成无用子表重写计划。每一原行都保留同一键，原有引用的主键列身份、编码和完整键集合因而不变。真正主键列/约束定义或实际键编码变更不能借此放行。

原 `PrepareSchemaMigrationChildren` 中涉及出站外键源列的类型变更拒绝保留在新分支之前。真正主键变更继续原子级联或原拒绝路径，自引用主键演进没有新增支持。

原 `ValidateSchemaMigrationForeignKeys` 保持执行；自引用按 principal 准备后的完整目标 key 集合检查，未删除或暂停外键。原 `CommitSchemaMigration`、native required version/recovery barrier、undo journal、raw 应用、目录发布与完成标记全部保留，未引入文件格式或降低格式 floor。

新增逐行检查只访问原计划已经受 **100,000 行 / 256 MiB**预算约束的内容，不另扫描所有子表、不复制键集合、不新增全局状态，并在每行调用既有 SQL 取消检查。

## 验证

新增 `tests/SonnetDB.Core.Tests/Tables/TableSelfReferenceSchemaMigrationTests.cs`，共 **7 个真实操作案例**：

- 原生类型表中修改非主键 legacy DATETIME → DATETIME2；保留原 root/child 和外部引用、主键约束名、覆盖/唯一索引；外部引用表 WAL 不增加。支持后续 100ns 日期写入，两次关闭/重开后继续核对原值和新行。
- self 和外部 child 的孤儿插入均拒绝；仍被 self 或外部行引用的主键删除均拒绝；唯一标签重复仍拒绝。升级前、后及重开后均执行这些真实 DML。
- 空表和有数据表中，真正主键列替换或主键类型变更仍按原自引用边界拒绝，catalog/WAL/原数据保持不变，共 4 例。
- `parent_id INT → STRING` 即使主键稳定，也仍按原出站 FK 类型合同拒绝；catalog/WAL/关联数据保持不变。
- 在 `after-table:nodes` 注入 I/O 失败，确认原 journal 回滚已应用的 raw 行和索引，恢复 legacy 日期类型/值及外键；重开后可重新升级，再次重开保留正确数据。

| 检查 | 结果 | 证据 |
| --- | --- | --- |
| 修改 Core 前的实际 ALTER | FAIL：0/1 | `before-self-reference.trx`，同一自引用迁移异常。 |
| 最终定向回归 + Release 编译 `/warnaserror` | PASS：7/7，0 SKIP | `after-self-reference-final.trx`；CPU 2、MSBuild `-m:2`、禁止 compiler/node 复用。 |
| Core Tables、SQL 主键迁移/rename、DATETIME2/DTO/Duration 相关组 | PASS：403/403，0 SKIP | `related-tables-native-schema-final.trx`；使用现有 Release 二进制，`--no-build --no-restore`，总耗时 4m09s；不与候选打包竞争编译。 |
| 最终源差异/UTF-8/空白检查 | PASS | `git diff --check` 限定范围通过；Core 源、回归、审计均为严格 UTF-8/LF。 |
| 新候选包的真实 ABP 升级 | NOT_RUN（本 worker） | 由主流程重新构建 Core/Data/Protocol 包，EF worker 验证最终同一候选。 |
| 最终完整 restore/Format、提交与十二类门禁 | NOT_RUN（本 worker） | 统一集成后的最终树由主流程执行；不继承旧 SHA 的通过结果。 |

测试命令、TRX 和进程记录位于 `artifacts/release-5.0-20261104-self-reference-schema/`。本机使用 .NET SDK 10.0.403、PowerShell 7.6.9 和 Cordonix 0.0.5 的独占 JSON request；定向任务最多 360 秒，CPU 2。完整相关组最终使用 420 秒总限及 210 秒 inactivity 限制。测试目录均由每例 GUID 创建并在 Dispose 删除；Cordonix 负责回收其 owned 子进程树，最终相关组进程 PID 7488 已报告 `cleanup_complete=true`、`active_processes_after_cleanup=0`。

首次扩大组使用的 90 秒 blame 诊断阈值短于既有 `TableOnlineCheckpointAdmissionTests` 的 3 分钟 fixture 总预算和 150 秒续建预算，因此在 117 项通过后过早中断；保留 `related-tables-native-schema.trx` 和原 hangdump，不把该中断归为产品回归。核对源代码后，仅将本次本机诊断阈值调整为 210 秒并完整重跑同一滤器；没有排除任何 case、缩小 100,001 行规模、修改产品或原测试预算，最终 403/403 通过。

保留的夹具失败：最初开发编译发现测试 helper 返回类型应为 `IReadOnlyList<object?>`，修正后才取得上述真实失败前基线。第一次修复后为 6 PASS / 1 FAIL，是回滚夹具把 legacy DATETIME 的既有 SQL `DateTime` 结果误写成 `long 0`；改为 `DateTime.UnixEpoch` 后最终 7/7。原 `after-self-reference.trx` 的失败仍保留，未改为 SKIP。

EF worker 已只读复核实际生产 diff，确认出站变更 guard、实际 key bytes 检查位置、prepared 自引用校验及原无主键隐藏身份复用路径未被绕过；该评审没有替代实际测试或 ABP 包消费者证据。

`verification-summary.json` 独立解析三份最终/基线 TRX 的计数，并记录实际 `Core.Tests/bin/Release/net10.0` 运行副本的完整 SHA256。CoreTests 为 `760744868579db6b1a1b32d6ef8440822f73c1b9205b2f711c728510a5f6bcdd`，Core 为 `7052b13868a9b0561ee0bb50be4397c2221b9f3df39631d6809aae2e0eaefd3a`；Data/Protocol 同文件记录。各 DLL mtime 均早于完整组启动及随后新候选打包，读取前后 size/mtime 一致；不把 pack 输出中的版本元数据差异视为该次运行程序集变化。

## 集成边界

此修复关闭的是 Core 非主键原生列迁移被误归类的阻断。真实 ABP 47 列升级、候选包身份、完整 EF/公开包消费者、最终发布门禁仍由主流程按新 SHA 独立取得。主流程需统一更新 HANDOFF/CHANGELOG；本 worker 未改写这些共享记录、Git/index 或此前 Parity 交付。

## 追加：纯 legacy GUID 表首次进入 native 编码

主流程独立审查后要求补足首次 native 升级边界。本次只在上述测试文件增加一个真实案例及必要的 `SonnetDB.Sql` using，未修改生产代码，也未重跑或覆盖原 403 项结果。原 7/7、403/403 和 `verification-summary.json` 仍绑定旧测试源码 SHA256 `5a3f8fd4dfc86104fac6c869d2b4649059e73df3b1df1f9b372669af20be55f3` 及该批运行 DLL；新增证据单独保存为 `guid-first-native-summary.json`。

新案例 `AlterColumn_LegacyGuidTableFirstNativeDateTime_PreservesKeyBytesReferencesAndReopens` 按 ABP 原始 OrgUnits 模型建立 GUID 主键/ParentId（实际 EF 存储映射均为 STRING），使用原主键、自引用约束名及默认 Restrict 行为。`CreationTime`、`LastModificationTime`、`DeletionTime` 最初均为 legacy DATETIME；在 ALTER 前明确断言没有 DATETIME2、DATETIMEOFFSET 或 DURATION 列。创建日期覆盖索引、唯一名称索引及另一表 OrgLinks 的入站引用，写入真实 root/child GUID 行与外部引用。

测试从真实 `GetByPrimaryKey` 结果保存 root、child 两行的持久主键字节，再执行 `ALTER TABLE AbpOrganizationUnits ALTER COLUMN CreationTime TYPE DATETIME2`。首次迁移后、100ns 日期更新后及两次关闭/重开后，分别核对这两行的键字节完全不变、GUID/父子/外部引用关系、已有日期/NULL 值、索引查找结果和原 FK 拒绝孤儿及被引用行删除的行为。重开后另写入一条带 100ns 时间的子行和外部引用，再次重开验证持久结果。实际 key guard 未拒绝，无需调整任何生产条件。

本批共两次 `dotnet test` invocation，受统一 15 分钟总上限、每次最多 480 秒及 CPU 2 / MSBuild `-m:2` 约束。第一次单例调用在测试夹具编译阶段报告缺少 `SqlParameters` 命名空间（CS0246），没有执行产品；补齐 using 后，第二次直接执行本类全部 **8/8 PASS / 0 FAIL / 0 SKIP**，包含新增 GUID 首次 native 案例。没有第三次调用或 403 项复跑，原历史结果保持原范围。

该次编译显式沿用最终 dirty-core-schema 属性：Version/PackageVersion=5.0.0、AssemblyVersion/FileVersion=5.0.0.0、RepositoryCommit=`29f206fc9238919721f314a7763a66a835cab842`、InformationalVersion=`5.0.0+29f206fc9238919721f314a7763a66a835cab842.dirty-core-schema`；SourceRevisionId 沿用 Git 自动发现，未宣称显式传入。执行前 HEAD 已核对仍为上述提交。

回执为 `guid-first-native-class-eight.trx`，精确调用参数为 `guid-class-eight.request.json`，本批测试源码、未改生产源码及实际 CoreTests/Core/Data/Protocol 运行 DLL 哈希另记在 `guid-first-native-summary.json`。Cordonix 最终进程 PID 19036 已报告 `cleanup_complete=true`、`active_processes_after_cleanup=0`，精确命令查询无残留；本批 shared 编译已释放。此追加只关闭 GUID/legacy 首次 native 的已有数据边界，不扩展为当前测试源码已重跑完整 403 项或新发布 SHA 已放行。
