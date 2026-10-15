# GH-Issue #226 主键 schema 迁移实施审计

日期：2026-10-16（Asia/Shanghai）。本文件记录实施工作树证据；最终主树提交、推送、完整格式门禁及 Issue 操作由批次监督记录补充。

## 实现范围

在既有存量主键重命名之上，实现非空关系表的主键类型变更、单列/复合主键定义替换，以及无主键表增加主键。重建行键与普通/唯一索引，按原父行身份更新普通入站外键的类型、值和引用声明；严格预检 NULL、重复、转换、默认值、CHECK、长度、Decimal、FK 与资源预算。已有 AUTO_INCREMENT 高水位和 ROWVERSION 值保持，后续写入使用原生成规则。

生产增量共十二个文件：新的 journal、TableStore migration、TableManager migration/recovery、KV 有界迁移读取和事务 schema 绑定六个文件，以及 TableManager、TableSchema、TableSqlExecutor、SqlTransactionContext、RelationalSelectExecutor、TableCostPlanner 六个既有文件。依赖此前 #243 的 `TableRowIdentity` 和 v13 catalog 持久屏障；没有重复归属 #243 的无主键 DML 实现。没有新第三方运行时依赖、unsafe、JSON 反射序列化或 NativeAOT 警告压制。

恢复采用永久 v13 屏障 → before-image journal → 分页 raw/WAL → 新 catalog → complete → 内存发布。恢复早于 TableStore 构造和索引重建；全部表指纹和 generation 通过后才修改任何表。日志发布后的目录同步失败和完成标记结果未知均使当前句柄失效，重开后确认决定。恢复旧 schema 语义仍保持 v13；可见完整标记在允许新写前再次同步。

独立只读评审发现并修复了日志发布结果不确定、恢复降版本窗口、完成标记可见但未同步三项持久性风险，以及先分配大值页再计预算的问题。另补 STRING→FLOAT 对超出 Decimal 值域整数和高精度文本的严格无损验证。

扩大审查随后发现旧事务纯 INSERT 无 schema 身份的问题：原父行 `(id,code)=(1,2),(2,1)`，事务先缓冲子行指向 `id=1`，其他连接迁移 PK 到 code，旧事务可能把子行挂到原 `id=2`。两种隔离级别的实际复现均暴露该错误。修复为所有写集保存首次不可变 schema 引用；所有 overlay 消费和 AddOrMerge 在解释旧行前校验，TableManager 锁内的级联前及最终提交前再次校验。保存点按 undo 顺序移除新绑定，保留较早绑定，事务结束清理；错误码统一 `table_serialization_conflict`。独立评审在该修复及 74 项定向测试后确认没有剩余阻断，根仍须完成扩大回归。

公开 SQL 不支持移除主键；内部空定义迁移测试不能当作 `DROP PRIMARY KEY` 支持。自引用/共享子列/进一步级联主键等外键拓扑保守拒绝；32 表、100,000 行、256 MiB 逻辑工作集和日志上限，以及 #227 活动轻事务 DDL 边界见 [合同](../sql-primary-key-migration.md)。

## 本地证据与集成验证状态

工作树：`C:\Users\mysti\.codex\worktrees\issue226-primary-key-migration\SonnetDB`。依赖基线为主树 `b6e674c4`；该工作树自身 HEAD 仍是 `b99bf142`，期间通过文件快照接收 #312/#243 依赖，不能把它当作已提交分支。

证据目录：`artifacts/issue-226-20261016/`。

| 层级 | 最终证据 | 结果 |
|---|---|---|
| 事务 schema 修复后的新增/既有 Serializable 组合 | `core-transaction-schema-reviewed.log/.trx` | 74/74，0 失败、0 跳过；36 新增、38 既有 |
| schema guard 前的迁移/恢复/现有 ALTER/keyless 组合 | `core-migration-frozen.log/.trx` | 94/94，0 失败、0 跳过；由根在最终源码重新扩大验证 |
| schema guard 前的真实子进程强制终止 | `crash-migration-frozen.log/.trx` | 10/10，0 失败、0 跳过；由根在最终源码重新验证 |
| 真实旧 codec 兼容拒绝探针 | `old-codec-probe.log` | exit 0，旧 DLL 拒绝 v13 且目录字节不变 |
| journal 单文件独立探针 | `journal-probe-final.log/.trx` | 4/4，仅辅助结构校验，不计引擎集成验收 |
| 真实 embedded/REST/Frame HTTP2，同步/异步及两隔离级别 | `RemotePrimaryKeyMigrationTests.cs` | 18 项已编写交主智能体验证；本文件此检查点未宣称修复后的远程通过 |

上述测试使用 `Release`、`-m:2`，进程环境 `DOTNET_PROCESSOR_COUNT=2`、`MSBUILDDISABLENODEREUSE=1`，通过日志没有编译 warning/error。schema guard 后的定向命令：

```powershell
dotnet test tests/SonnetDB.Core.Tests/SonnetDB.Core.Tests.csproj -c Release -m:2 --filter 'FullyQualifiedName~SqlPrimaryKeyMigrationTransactionTests|FullyQualifiedName~SqlSerializableTransactionTests' --logger 'trx;LogFileName=core-transaction-schema-reviewed.trx' --results-directory artifacts/issue-226-20261016
```

原迁移 Core 命令：

```powershell
dotnet test tests/SonnetDB.Core.Tests/SonnetDB.Core.Tests.csproj -c Release -m:2 --filter 'FullyQualifiedName~SqlPrimaryKeyMigrationTests|FullyQualifiedName~TableSchemaMigrationRecoveryTests|FullyQualifiedName~TableStoreSchemaMigrationTests|FullyQualifiedName~TableSchemaMigrationJournalTests|FullyQualifiedName~AlterColumn|FullyQualifiedName~SqlSafeDdlEvolutionTests|FullyQualifiedName~SqlKeyless' --logger 'trx;LogFileName=core-migration-frozen.trx' --results-directory artifacts/issue-226-20261016
```

Crash 命令：

```powershell
dotnet test tests/SonnetDB.CrashTests/SonnetDB.CrashTests.csproj -c Release -m:2 --filter 'FullyQualifiedName~PrimaryKeyMigration|FullyQualifiedName~PrimaryKeyTypeMigration' --logger 'trx;LogFileName=crash-migration-frozen.trx' --results-directory artifacts/issue-226-20261016
```

Core 覆盖父子关系、默认/CHECK/unique/生成状态、PK/内部 keyless 变换、无损转换失败、源 catalog/WAL 不变、取消及后续新写、所有表先校验、损坏 journal、完整标记再次同步失败和磁盘大值读取前预算拒绝。最初一次扩展运行 92/94，修正预取消异常合同及过时的“主键类型一律拒绝”断言后达到 94/94。其后保留 `core-transaction-reproduction.trx` 的两个真实失败，修复后 36 个新测试覆盖两隔离级别的五种写集形状、定义/类型迁移、overlay/合并/级联先拒绝、保存点移除及保留绑定、新事务和不相关表。首轮 62/72 的十个失败来自测试选用了不支持的 INT→STRING，改成已有 INT→DECIMAL 后新增两项保存点测试，最终 74/74；没有扩大生产转换语义。中间日志全部保留，各重叠集合不累加。

Crash 十项覆盖屏障前/后、journal 后、1032 行迁移第一 raw 页后、父表完成子表前、新 catalog 后、complete 后、主键 INT→DECIMAL 类型迁移的 catalog/complete 两个边界，以及恢复写回旧语义 v13 catalog 后再次终止。最后一种场景第三次打开恢复成功，并验证后续新写再次重开仍存在。所有十项曾在迁移冻结生产源码上重跑；schema guard 修复后由根统一重跑，不将此前结果提升为修复后的最终通过。

旧 codec 探针通过独立 AssemblyLoadContext 执行先前 #314 树实际 Core DLL 的公开 `TableSchemaCodec.Load`，明确得到 `TableSchema: unsupported format version 13.`；当前 codec 可写读同一 v13 fixture，旧读取前后 fixture 字节一致。这是实际旧 codec 执行证据，不是仅检查新版本头；不推导旧 Server/ORM/完整安装包验收。

- 旧 Core DLL SHA256：`9467C760CD9B86554108FFB870042D863054B7680008C83340E3889B43300E76`。
- 当前探针 Core DLL SHA256：`B26F534450991AAB3BEDFE54E0DC47BE92332C494F244F50004C40154A18A48D`。
- v13 fixture SHA256：`B47075748B33C7711D7AEC06A50FCAD2755E497B650BAF9CF920A4D461D6ACA5`。

## 集成与剩余验证

22 个生产/Core/Crash 增量文件由 `issue226-source-reviewed.json` 绑定 SHA256，清单 SHA256 为 `21CACCBE5EF0FAEB46380EED66C71DAAAF07C388A2F7480BD3EB8A98094EC7F4`，替代 schema guard 前的 17 文件清单。清单不包含此前 #312/#243 依赖和共享 remote fixture，避免覆盖主树独立提交；RelationalSelectExecutor 已接收主树 #314 版本后再修改两处 overlay 调用。独立测试智能体交付的 `RemotePrimaryKeyMigrationTests.cs` SHA256 为 `A675DE7E7C304F48B9F8738024BAB810D02D426BE66F2956666004EF971CEE3A`。

本工作树未提交、推送、关闭 Issue 或发布。主智能体须整合源码和文档，在 schema guard 后扩大 Core、真实传输和实际终止测试，再执行完整 restore 与原 Format Check 后独立提交 #226；源文件若有修正须更新相应 SHA 与证据。首次可安装版本、正式包、XCode/FreeSql 消费者、完整远端 CI、新 NativeAOT 发布及断电测试没有在此检查点通过，不能由本地定向测试或旧 codec 探针替代。

## 根主树最终验证

根在 `b6e674c4` 上接收上述22份增量及最终远程文件，23份源码/测试SHA与冻结输入一致，保留#312/#313/#314/#243的已提交能力。Core、Server、Crash三个Release构建均0警告、0错误。

| 最终主树检查 | 结果 | 根证据 |
|---|---:|---|
| Core SQL/Tables/ADO/Backup扩大回归 | 3313/3313，0失败/跳过 | `core-reviewed.log/.json/.trx` |
| 远程ADO、迁移、无主键、重命名、事务恢复组合 | 104/104，0失败/跳过 | `server-reviewed.log/.json/.trx` |
| 真实子进程终止恢复组合 | 15/15，0失败/跳过 | `crash-reviewed.log/.json/.trx` |

根证据目录为 `artifacts/issue-supervision-20261016/issue226/`，`validation-final-root.json`绑定命令、退出码、23份输入、6份产物及最终TRX的SHA256。Core包含修复后的36项事务绑定回归及既有Serializable、DDL、键更新、无主键、索引、约束、默认、自增和备份；排除本批#243隔离树已验证且本次未修改的慢`TableOnlineCheckpointAdmissionTests`。Server包含18项新迁移用例（12项同步/异步成功失败恢复、6项三协议×两隔离级别的旧事务拒绝/新事务恢复），逐例核实实际通过。15项kill包含本项10个边界、无主键3个边界和既有主键重命名2个边界。

首轮根525/27/13+2在旧事务保护修复前完成，保留为过程证据，不替代上述最终结果。子树74/94/10和根最终集合存在重叠，不累加，也不称为完整solution suite。未新增JSON序列化边界、依赖或unsafe，未在本检查点执行新的NativeAOT发布、远端CI、正式包发布或ORM消费者验收。

本次最终树按仓库要求完整执行 `dotnet restore SonnetDB.slnx` 与 `dotnet format SonnetDB.slnx --verify-no-changes --no-restore --severity warn --exclude extensions/`；收据实际exit 0后才独立提交，提交SHA以Git记录为准。正常批次推送、远端SHA/CI和Issue回复结果由监督记录继续补充。
