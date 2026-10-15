# 第二批十任务：2026-10-16

本批接续本地 `f562bf52`，保留首批实现 `ac71b100` 和交接提交。用户明确选择继续交接建议的第二批十项任务。主智能体负责冻结范围、串行集成和最终验收，三个实施智能体各自在隔离工作树处理主键重命名、序列、注释与字符串长度。任务按产品能力计数，不把构建、测试或文档另计一项。

## 接收与范围冻结

- 接收时工作树干净，`main` 比 `origin/main` 领先 2 个提交；`git fetch origin --prune` 后仍为 2/0。没有重置、合并或改写本地提交。
- `origin/parity-results` 保持 `2ab6caa94b9f07d25fdd0760a7aa682f7ca26295`，未并入开发分支。
- GitHub 直连超时后使用本地代理读取公开 API；本批首次快照为 48 条 open Issue，完整公开正文保存在本地忽略目录 `artifacts/batch-02-20261016/github-open-issues.json`。历史交接记录不替代本轮线上状态。
- #226 主键类型/定义迁移及 #227 DDL 事务需要独立的目录和存储恢复合同；#232 schema 命名空间、#233 锁定读与 #236 DATETIME 精度也保留为后续独立任务。本批不删除现有拒绝检查来冒充完成。
- #243 的无主键删除依赖 #253/#255 的无主键写入能力；现有无主键表只允许空表迁移。本批交付已支持主键的关系表有界删除，不把该切片提升为整条 Issue 完成。

| 任务 | 归属 | 冻结的产品交付 | 验收状态 |
|---|---|---|---|
| B02-01 | GH-Issue #226 / M43 | 存量主键列重命名，同步目录、索引声明、关联外键及 CHECK，失败保持一致 | 本地实现与专属回归通过 |
| B02-02 | GH-Issue #231 / M43 | 独立持久序列 CREATE/DROP 生命周期与正常重开 | 本地实现与专属回归通过 |
| B02-03 | GH-Issue #231 / M43 | 显式 SQL 原子取号，Int64、并发及事务外耗号 | 本地实现与专属回归通过 |
| B02-04 | GH-Issue #231 / M43 | INSERT 表达式、批量及 DEFAULT 的逐行序列分配 | 本地实现与专属回归通过 |
| B02-05 | GH-Issue #237 / M43 | 持久化表注释的设置、替换、清除与读取 | 本地实现与专属回归通过 |
| B02-06 | GH-Issue #237 / M43 | 持久化列注释的设置、替换、清除、重命名保留与读取 | 本地实现与专属回归通过 |
| B02-07 | GH-Issue #240 / M43 | STRING(n) 声明、目录持久化和所有关系写入口的长度约束 | 本地实现与专属回归通过 |
| B02-08 | GH-Issue #240 / M43 | ALTER 长度扩大与缩短，全量预检后原子发布 | 本地实现与专属回归通过 |
| B02-09 | GH-Issue #240 / M43 | 结果及 ADO/远程目录准确提供声明长度，支持反向建模 | 本地实现与专属回归通过 |
| B02-10 | GH-Issue #243 / M43 | 单条 DELETE WHERE LIMIT 精确限制完整主键，包含复合键、事务与 RETURNING | 本地实现与专属回归通过 |

## 验证与交付边界

专属 Core 测试新增 130 项：主键重命名 9、序列 48、注释/长度 39、有界删除 25、根组合 3、取消/字节边界 6。Server 测试项目新增 18 项，其中 17 条实际 Kestrel REST/h2c 路径、1 条嵌入式主键重命名；另新增 2 条真实子进程强制终止后重开验证。组合测试在同一数据库中验证字符串主键重命名、入站外键、双方长度和注释、保存失败补偿、序列默认值、回滚耗号、正常重开和有界删除。新增数量已经包含在扩大集合中，不能再与集合总数相加。

最终主树的构建、扩大 Core 回归、Server SQL/ADO/Frame（含真实 REST/h2c）、实际强制终止恢复及提交前门禁见下表。独立工作树的 249、157、145 等局部通过数不与最终结果相加。


| 最终验证 | 实际结果 | 本地证据文件 |
|---|---|---|
| Core/Data 与 Core 测试 Release 构建 | 0 warning / 0 error | `core-reviewed-build.log` |
| Server 与测试 Release 构建 | 0 warning / 0 error | `server-frozen-build.log` |
| Crash 子进程与测试 Release 构建 | 0 warning / 0 error | `crash-reviewed-build.log` |
| Core SQL/ADO/Tables/BackupService 扩大回归 | 3157/3157，失败 0、跳过 0 | `core-reviewed.trx` |
| Server SQL/ADO/Frame 回归 | 412/412，失败 0、跳过 0；包含实际 Kestrel REST/h2c，也含 registry/unit/嵌入式回归 | `server-frozen.trx` |
| 主键重命名目录提交前/后实际子进程强制终止与重开 | 2/2，失败 0、跳过 0 | `crash-reviewed.trx` |
| 完整 solution restore | exit 0 | `restore-before-commit.log` / `.json` |
| 原 CI Format Check（warn、只排除 extensions/） | exit 0 | `format-before-commit.log` / `.json` |

上述证据位于忽略目录 `artifacts/batch-02-20261016/`，没有作为构建产物提交。`validation-inputs-frozen.json` 绑定 49 个改动 C# 源文件及 26 个 DLL/PDB，SHA256 为 `242567D2DE7EDA7B3A12A058A00E07651B480546E582F2A82E739BBF7BEC2B97`；最终 `validation-assets-final.json` 引用此冻结记录、三份最终 TRX、构建日志和门禁收据。提交前再次核对全部输入哈希及暂存内容，避免把较早运行归到最终源码。

门禁使用原命令 `dotnet restore SonnetDB.slnx` 与 `dotnet format SonnetDB.slnx --verify-no-changes --no-restore --severity warn --exclude extensions/`，未降低级别或扩大排除。首次格式检查发现两处 object initializer 换行，修正后使用最终树重跑；原失败日志保留。生产构建启用既有 AOT/trim 分析且零警告，未执行新的 NativeAOT publish。

## 可执行合同

```sql
CREATE SEQUENCE OrderIds START WITH 100 INCREMENT BY 2 NO CYCLE;
CREATE TABLE Orders(Id INT DEFAULT nextval('OrderIds'), Name STRING(20), PRIMARY KEY(Id));
COMMENT ON TABLE Orders IS '订单';
COMMENT ON COLUMN Orders.Name IS '订单名称';
INSERT INTO Orders(Name) VALUES('第一单'),('第二单');
ALTER TABLE Orders RENAME COLUMN Id TO OrderId;
ALTER TABLE Orders ALTER Name TYPE VARCHAR(40);
DELETE FROM Orders WHERE OrderId >= 100 LIMIT 1 RETURNING OrderId;
```

- 主键重命名不改列序号、类型或键/行/索引载荷，通过一次完整表目录替换和一次 catalog 快照发布同步跨表引用。保存或发布失败补偿旧目录与已持有 store；补偿失败则停止使用当前实例，要求重开。实际终止测试分别停在目录提交前、提交后，重开必须恢复完整旧合同或新合同。
- 序列使用独立内部 KV 和同步 WAL，单次成功返回前持久化新高水位；关系事务回滚及失败插入不回收已分配号码。独立序列锁避免与 schema/table 锁反序，备份在检查点和完整目录复制期间持有序列锁。WAL/fsync 结果未知后停止发号，重开从 WAL 恢复。支持形状、错误码、引用及备份恢复边界见 [序列说明](../sql-sequences.md)。
- 表列注释区分未设置 `NULL` 与显式空串；最长 65535 个 UTF-8 字节，超限拒绝并恢复旧目录。名称引用沿用双引号精确匹配合同，注释从 catalog、information_schema 和嵌入式/远程 `GetSchema` 读取。
- `STRING(n)` 与 `VARCHAR(n)` 按 UTF-16 代码单元计数，中文单字为 1，补充平面字符通常为 2；NULL 不占长度，超长拒绝且不截断。ALTER 先检查所有现有行及默认值，取消在校验和发布前生效。结果 `ColumnSize`、目录 `CHARACTER_MAXIMUM_LENGTH` 返回保存的声明；未声明 STRING 仍保留原无界元数据合同。
- 表目录有长度或注释时写 v12，读取兼容 v1～v12；无新属性时仍写 v11。未修改 `FileHeader`、表行或键编码。旧引擎会拒绝 v12，回退须使用升级前副本，不能让旧 writer 忽略新约束。
- `DELETE LIMIT` 接受非负 Int64 字面量或整型参数；零不扫描或清表，日期、小数、NULL、布尔、字符串及越界参数拒绝。完整复合键用于原子删除，RETURNING 与直接影响行数一致。上限不承诺候选扫描/内存上界，也不包含既有外键级联和触发器额外修改行数。

## 独立审查与修正

三个实施智能体使用独立 detached 工作树，主智能体串行集成共享的 parser/executor/schema 文件；作者以外的智能体分别复审有界删除、元数据和序列。已修正：

1. 通用参数绑定把日期转换成毫秒整数，可能放大 DELETE LIMIT；嵌入式和远程均在转换前验证原 CLR 类型，真实两种传输补拒绝回归。
2. 主键重命名新增入站外键 schema 克隆须继承子表长度和注释，根组合测试覆盖成功与发布失败补偿。
3. 序列持久化失败不能继续用旧内存状态发号；序列锁须独立于 schema 锁，备份冻结须涵盖完整复制。
4. 有副作用的 nextval 需完整 AST 分类和写权限；REST 保留专属错误码，Frame 为取号 SELECT 明确检查 Write，Workbench 只读预览拒绝。
5. UPDATE、ON CONFLICT 和触发器不能从 catalog DEFAULT 隐式绕过首版取号限制，补齐实际默认值分类与最后执行防线；拒绝不得耗号。
6. 长度校验增加逐页、逐行及元数据发布前取消点，补 ASCII/中文注释 65535/65536 字节成功与拒绝后的重开。

初次根组合测试帮助函数返回类型写错导致一次编译失败；真实元数据测试曾错误期待 `DbException` 和标量 `DBNull`，分别按既有 `SndbServerException` 与标量 null 合同修正。扩大服务回归还发现新 UInt64 格式化使超 Int64 值异常类型发生变化，已保留原 `NotSupportedException` 拒绝合同。原始失败日志保留，不把失败运行计作通过。

## 剩余与下一批顺序

#226 仍缺存量主键类型/定义迁移；#243 仍缺无主键表路径，依赖 #253/#255。#231/#237/#240 的本地实现和测试通过不能替代合入、正式 NuGet/Server 版本、Chloe/XCode/FreeSql 消费者验证；本批没有关闭 Issue、发布包或部署。首批已有实现与公开包事实保持原交接记录，不重复实施。

下一批先核本地提交、工作树及线上变化，再冻结十项：优先 #226 剩余主键迁移与 #227 DDL 事务的共享恢复设计，随后 #232 schema、#233 锁定读、#236 DATETIME 精度和无主键写入依赖；#312/#313/#314 按既有队列保留。新批次需重新选取十个可验收产品切片，不能直接把设计、构建、mock 或测试另计任务。

完整 Parity/nightly、固定硬件、长期运行、实机安装和新 NativeAOT publish 未运行；生产程序集 Release 构建的 AOT/trim 分析结果与真实 NativeAOT 发布分别记录。
