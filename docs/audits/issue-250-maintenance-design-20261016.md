# #250 维护合同实施与验证

日期：2026-10-16。实施基线 `aa4fbffba0caf7d400cb10e7ebb1d45cc99ff5c5`，接续 #252 DURATION 和 #251 DATETIMEOFFSET。当前是未提交的源码候选；监督者负责最终全 solution 门禁、提交、批次推送与 issue 回复。本文件不证明正式 NuGet/Server 发布或 SqlSugar 上游合并，#250 仍须保持 OPEN。

## 当前交付

- PK 的真实名称持久保存；原生 `information_schema.table_constraints` 返回实际 PK/FK/CHECK 名称。旧带主键目录在开放表存储前迁移名称；同表名称冲突确定性避让。表/列重命名不重命名约束，普通/双引号名称分别按既有大小写合同绑定。
- `DROP CONSTRAINT` 按保存的 PK 名称调用既有 #226 主键迁移并进入 #243 稳定无主键行身份。入站外键依赖显式拒绝；不重写已有 #226/#243 为新功能。
- 索引键列真实 ASC/DESC 编码，INCLUDE 使用含主行身份与 TableRowCodec 类型化投影的值 envelope。唯一性只比较主行身份；DML、主键迁移、索引修复、重开与在线续建都维护载荷。源代码审查发现的迁移 raw-PK 值遗漏已经修复。
- 普通覆盖 SELECT 只接受安全的直接列/常量投影及简单 ORDER 标识符，残余谓词必须完整受计划支持。有界 Top-N 优先沿用原计划。无 INCLUDE 的旧索引值和无新选项的在线 v1 指纹保持原编码；扩展在线状态使用 v2。
- 原生目录/ADO 索引 schema 返回方向与包含列标志，Server/REST/Frame source-generated DTO 同时传递这些声明与真实 PK 名。
- `ALTER TABLE <table> AUTO_INCREMENT = <positive Int64>` 设置下一值；同锁区间验证历史高水位并同步维护 WAL，不写假行。覆盖删除最大行、冲突拒绝、并发与最大值溢出。活动轻事务 DDL 仍拒绝。
- 目录永久 **v16** 屏障先于新编码/断点建立，最后新特性删除后仍保持。DURATION/DTO 独立屏障测试改用无主键夹具，继续实际验证 v13→v14/v15，避免新命名 PK 的 v16 遮蔽它们。
- `information_schema` 增加单一 COUNT(*) 投影以支撑真实 `IsAnyConstraint`，WHERE 先过滤、空结果元数据仍为 Int64；不新增通用聚合/HAVING 能力。

用户合同见 [SQL 维护](../sql-maintenance.md)。可应用的三文件 SqlSugar 补丁和探针见 [复现说明](../../eng/probes/issue250-maintenance/README.md)。

## 当前源码与消费者证据

全部原始日志、TRX、源快照、PID/启动时间及 SHA 清单保留在 `artifacts/issue-supervision-20261016/issue250-work/`。下表各行是独立验证窗口，有重叠，不能累加称为唯一测试总数。

| 验证窗口 | 实际结果 | 边界 |
| --- | --- | --- |
| core06 定向原生合同/类型/目录 | 238/238，零跳过 | 含 44 个本任务维护/编码用例及既有回归 |
| storage02 Tables/Protocol/Data | 337/337，零跳过 | 未改动生产后不重复执行 |
| SQL 最终三组 final01 | 1250/1250、1003/1003、911/911 | 全部顶级 Sql 测试类按排序模 3 分组，过滤清单保存；三个组互不重叠 |
| remote03 整类真实 ADO | 153/153，零跳过 | 原 147 与新增维护 6；嵌入式/REST/Frame HTTP/2、同步异步与宿主重开 |
| crash02 identity | 5/5，零跳过 | 普通写同步关闭，维护确认后真实终止进程：空/存量/删最大/拒绝/最大值 |
| crash03 扩展恢复 | 31/31，零跳过 | 新 PK 删除+DESC/INCLUDE 7，既有 PK 迁移 10，Duration 7，DTO 7；含恢复再次终止 |
| 原消费者 + 当前 Core/Data | 原生 10/10 | 保持同一原生消费者程序集，只替换源运行时 |
| 原 SqlSugar + 当前 Core/Data | 9 PASS / 3 FAIL | CodeFirst #227 与原适配器两个 identity 入口抛出仍在 |
| 本地 SqlSugar 候选嵌入式 | 11 PASS / 1 FAIL | 唯一失败为标准 CodeFirst，未绕过事务 |
| 同一候选消费者 REST 与 HTTP/2 | 每协议 11 PASS / 1 FAIL | 唯一失败同上；以实际服务端请求日志确认 HTTP/1.1 与 HTTP/2 Frame |
| 候选远程宿主终止并重开 | 每协议独立新消费者 5/5 | 实际名称、包含列、重复行、原数据、自增继续与拒绝后状态 |
| Data/Core 显式 AOT/trim | 0 警告 / 0 错误 | `contracts-data-aot01`，IsAotCompatible/EnableAotAnalyzer/EnableTrimAnalyzer 全部开启 |
| Server 原项目配置 Release Rebuild | 0 警告 / 0 错误 | `contracts-server-release01`，Core/Server 继承原项目 AOT 配置 |

本轮生产程序集保持 `impl02`：Core SHA256 `482EEA5CA5AC4C491C1FB6647065C3417A6D56D5A8FC2ADBBCFCFA426D2F69E9`，Data SHA256 `11AA7DAC77D1D138B68B3E21D764D3BBDB6703FD9C2F990F36F39E86C0138D14`。其 informational version 含基线 aa4fbffb，但不代表工作区变更已提交；最终源码/运行时清单单列，不能据 informational version 推断完整源码版本。

真实 public 4.0.0 生成的 v10 原目录始终保留，只在新副本升级。原 FK=`pk_Legacy`、CHECK=`PK_Legacy_1`，升级实际保存 PK=`pk_Legacy_2`，并通过改名、删除、无主键重复行及重开。原 schema SHA `DB5B59DD18FCD051E6DAE87F8492D7767B9B9AB0624749B4516BF624E6472F81`；升级后 schema SHA `DB8CAA54C246A1DA290A79B181284D028988178BC5E73803004F03DE8A247202`。

新选项目录 v16 的 schema SHA `BE8ECEA459C68585E5C35027BDD95E26730FAAA1EDA47707071E0E1DB76DBE77`。移除最后表/索引后仍为 v16，SHA `188FF28F8FD1903EF0D57BB44363AA86E3F3C0E7625E58C7573C5F3FA06A7A65`。实际 public 4.0.0 在删除前后均因 unsupported format 16 拒绝，schema SHA 不变。该探针只比较 schema 文件，未宣称目录所有文件零写入。

## 未抹去的失败与修正

- 原基线、准备 Core 1/28、远程 0/6 均保留在下方历史记录。最早 inline-PK 错误探针不能计数据库缺口。
- 实施首次构建的 TokenKind 拼写错误已修复；core01 26/28 暴露主键迁移漏写 INCLUDE envelope 与普通 SELECT 未走覆盖路径，修正后 core02 28/28。core03 38/38。
- regression01 超时且未取得 TRX，不能计为运行通过或失败用例数。runner 输出排空后来增加有界超时，原进程回执保留。
- storage01 333/337：三项旧格式夹具只是篡改新目录版本号，现改为冻结的旧布局序列化器；一项故意损坏的派生索引头导致恢复中止，现只将无效派生索引头归入清理，主数据/WAL/catalog 错误仍抛出。
- core04 231/232 与 lock-retry01 0/1 是已有锁重试测试在 CPU2 的线程池调度问题。监督者在旧/新 DLL 独立复现并提交专用线程修正 `9b3b983304d249fafe485235761c1f8a70671cab`；本树只同步该方法，交付整合不能重复覆盖独立提交。
- core05 42/44 的两项屏障失败注入收到目录 `.tmp` 的 UnauthorizedAccessException；测试改精确接受 IOException 或 UnauthorizedAccessException，仍核目录/内存/索引均未发布。
- remote01 0/6 抵达重开后的原生目录 COUNT 才失败，随后实现实际 SqlSugar 所需的 COUNT(*)，remote02 6/6；最终整类 remote03 153/153。
- crash01 0/5 是自定义 artifacts 输出未部署子进程程序集，没有进入场景。核对 Core SHA 后仅复制 Child.*，crash02 才实际执行并 5/5。
- SQL 第一轮三组分别 1249/1250、1000/1003、905/911。自定义 PK 拒绝已变为真实名称持久/重开/DROP 正向；旧主键目录的 13 预期更新为16；无主键永久13在新建PK前仍有断言。两项旧回表 hook 断言改为新覆盖路径的完整3候选、零回表、原排序分页结果，未削弱候选截断要求。最终三组全部通过。
- 补丁首次核验在父仓库的忽略子目录运行 git apply，Git 跳过相对路径，SHA 校验及时失败。第二次独立 git 目录实际 check/apply，三文件 SHA 与候选完全相同，`sqlsugar-patch-verify02.json` 保存结果。
- 重开探针直接 ProjectReference 固定上游时，启用审计与 TreatWarningsAsErrors 导致 4 个 NU1902/NU1903 错误（Azure.Identity、Microsoft.Identity.Client、System.Security.Cryptography.Pkcs）。未关闭审计、未降警告、未升级固定上游。后续探针直接引用已冻结完整运行时；首次误包含原消费者类型导致 CS0436，排除原消费者 DLL 后零警告/错误。此编译不等于上游依赖审计通过。
- Server 显式全依赖图强制 AOT 的首次尝试成功但有 4 条 NETSDK1210，原因是全局参数传到两个 netstandard source generator。该日志保留；未修改第三方或压制警告。后续按各项目原声明重新完整 Rebuild 为零警告/错误。源 Server 自身继承 IsAotCompatible，不需要向不受支持框架的工具项目强制传参。

## SqlSugar 与发布边界

原固定上游 603 文件逐个 SHA 验证后复制成本地候选。只改 3 文件：DbMaintenance 正式 identity 语句与真实命名 PK/DROP；基类 CodeFirst 的名称查询虚拟扩展点；SonnetDB CodeFirst 查询真实目录并明确拒绝 identity 声明切换。基类默认行为保留。未改 `InitTables` 事务，没有通过移出事务掩盖 #227。名称 hook 仅源码审查，完整 CodeFirst 在事务 DDL 之前失败，不能宣称该分支实跑。

候选构建保留 13 项上游源码/NuGet 警告；源 SonnetDB Core/Server 构建与 Data 显式 AOT/trim 分析的零警告/错误结果按上表单列，不与上游构建混计。未进行 NativeAOT 发布运行、物理断电或固定硬件性能验收。最早历史消费者项目曾关闭 NuGetAudit，其快照保留且只作功能基线；最终探针项目已移除此设置，不能把旧基线构建称为审计通过。

关闭条件仍包括正式 NuGet/Server 发行、SqlSugar 上游交付，以及 #227 完成后标准 CodeFirst 真实路径验证；本地补丁和源运行时 PASS 不能替代这些条件。

## 历史准备记录（以下为实施前冻结状态）

以下完整保留早期计划、基线和未运行情况，以便对照；不代表上方当前完成状态。准备时共享顺序为 #252 → #251 → #250，当时尚未预占目录格式版本。

## 已核实的来源和边界

- SonnetDB issue #250 当前 OPEN，无评论；要求真实约束目录、索引方向/包含列、自增维护语句三项全部交付。
- SqlSugar PR1457 当前 OPEN，固定 head `65e8226ce25a8e1cb9cca2590683f1fd6778cab3`，来自 IoTSharp/SqlSugar。原始 ZIP SHA256：`46615584F90E8E7857A91140130581F9E4C3D7E7AE20B48A9C9F05383633D766`。从 ZIP 精确提取 SqlSugar 与 SqlSugar.SonnetDBCore 两个目录，共 603 文件，逐文件清单保存在本任务 artifacts。
- 当前源码基线 `f9ee14b7aabbe7eb7a3c842319d12bcd402d3175`；没有改动共享生产文件。#226 主键迁移和 #243 无主键稳定身份已有实现，后续复用。
- SqlSugar 的 `IsAnyConstraint` 已直接查询 `information_schema.table_constraints`；该原生视图缺失。外键/CHECK 名称已有持久化，主键只有约定名称、没有持久化字段。
- SqlSugar 的 `CreateIndex` 省略 ASC 后调用基类。DESC 和 `{include:...}` 由基类实际发出；原生解析器均拒绝。`SetAutoIncrementInitialValue` 两个入口最终直接抛出不支持。
- SqlSugar 基类 `ChangeKey` 猜测 `PK_<table>_<column>`。后续适配器必须消费真实目录，不能通过伪造目录迎合猜测。
- 标准 `CodeFirst.InitTables` 使用事务执行 DDL，当前被 #227 拒绝。这一独立失败必须保留；不能移出或取消事务绕过，也不能把 `DbMaintenance` 验证冒称完整 SugarIndex/CodeFirst 流程通过。

## 原始基线

| 实际运行组合 | 通过 | 失败 | 说明 |
| --- | ---: | ---: | --- |
| 正式 SonnetDB/Core 4.0.0 原生嵌入式 | 3 | 7 | 原目录、建表/显式键推进高水位控制通过；三项新合同缺失 |
| 正式 SonnetDB/Core 4.5.0-preview.ef266.5 原生嵌入式 | 3 | 7 | 与 4.0.0 相同 |
| f9ee14b7 源 Core/Data 原生嵌入式 | 3 | 7 | 同消费程序，仅替换源 Core/Data |
| 固定原 SqlSugar + 正式 SonnetDB 4.0.0 | 3 | 9 | 原维护建表/ASC/删除索引通过，其它原生缺口与 #227 分开记录 |
| 同 SqlSugar 消费程序集 + f9ee14b7 源 Core/Data | 3 | 9 | 没有候选适配器或原生新实现 |

最初原生探针错误使用 inline PRIMARY KEY，0/10 的运行及源码保留；最终改用既有表级 PRIMARY KEY 后取得上述 3/7 结果。不能将首轮失败写成数据库缺口。

源 Data 和准备测试的第二次 Release 构建为零警告/错误。原 SqlSugar 构建保留 13 条上游源码/NuGet 警告，消费者构建保留其依赖的 8 条 NuGet 警告。它们不是 SonnetDB AOT 或最终门禁结果。

## 拟实施合同

### 真正的约束名称

1. `TableSchema` 增加真实主键名称，创建命名 PK 和 ALTER ADD/ALTER PRIMARY KEY 均保存；约束目录统一返回存储的 PK/FK/CHECK 名称、类型及原表名。
2. 旧 catalog 中已有主键的名称迁移需在开放维护目录前持久化。以既有 `pk_<table>` 约定为首选；若与已声明 FK/CHECK 名称碰撞，选择确定性的未占用名称并实际保存。不能展示一个未保存的推测名字。
3. 表/列重命名保留约束名字；显式新建名字在单表约束命名空间内按 OrdinalIgnoreCase 拒绝冲突。普通引用忽略大小写、双引号精确匹配；旧冲突不能静默选择。
4. `DROP CONSTRAINT` 删除 PK 通过 #226 迁移进入 #243 稳定无主键身份，重建索引并保留每行。入站 FK 仍依赖主键时拒绝，失败后行、目录和索引保持原状态。
5. SqlSugar 候选以原生目录返回的名称维护。CodeFirst 的原事务边界保持不变；#227 完成前不能宣称它已通过。

### 方向与 INCLUDE 的实际存储

1. 声明增加每个键列的方向和独立包含列集合，默认 ASC。INCLUDE 不能作为唯一键的一部分；键列/包含列的未知列、重复列和重叠列应拒绝。
2. DESC 必须改变实际键编码。保留既有 ASC 编码；按列反转该列的完整编码字节，并在查找、覆盖解码和 signed range 边界中应用同样的方向。逻辑上下界映射到相反的物理边界，游标方向为请求方向与声明方向的异或。
3. 既有数字/日期索引的正负分段仍必须符合逻辑排序；字符串、浮点和其它类型不得借此错误宣称已新增有序范围规划。它们的查询仍须按现有规划能力可靠排序，并验证混合方向/NULL/极值的结果。
4. INCLUDE 使用真实索引值载荷，建议给有包含列的项添加带版本和边界长度的 envelope：原主行 identity 与包含列的 typed payload 独立存放；无 INCLUDE 的旧值编码保持原样。覆盖投影读取包含列时不回表。
5. 所有唯一性判断比较主行 identity，不比较包含列载荷。UPDATE 只改变包含列时需更新载荷；事务/批量写入/修复/重开/主键迁移全部复用相同编码入口。
6. 在线构建断点需保存方向和包含列，升级其断点版本并兼容读取旧断点；schema fingerprint 必须覆盖新增选项。统计宽度计入真正索引值长度，修复扫描的列掩码计入 INCLUDE。
7. 新的 catalog 格式必须先持久建立永久屏障，才可写新索引编码或新在线断点；最后一个新选项被删除后也不能降回旧格式。具体版本接收 #251 最终基线后确定。旧引擎必须在打开目录时明确拒绝，不能读写其不理解的条目。
8. SQL `information_schema.indexes` 及 ADO `GetSchema("Indexes")` 返回键列方向与 `is_included`。REST schema DTO 需 source-generated 增量传递选项与真实主键名，防止远程快照重建时丢失信息。

### 自增下一值

1. 拟正式语法：`ALTER TABLE <table> AUTO_INCREMENT = <n>`，n 表示下一次自动分配值，范围 1..Int64.MaxValue。
2. n 必须严格大于已有持久化高水位；成功写入 n-1。n 等于当前高水位+1 可以幂等设置；删除最大行不降低高水位。显式插入已有最大键的既有行为不变。
3. schema/manager/store 采用已有锁序；校验和高水位写入在同一锁区间。拒绝设置不能修改状态、写假数据或影响现有行。成功命令同步其持久 WAL 状态，验证进程终止后恢复。
4. 设置 Int64.MaxValue 后允许最后一次分配；下一次自动分配明确溢出，不能插入记录或环绕。负数、零、超界、非自增表及有活动事务的未支持 DDL 都明确拒绝。
5. SQL 执行、直接管理入口及 SqlSugar string/Type 两个公开入口使用同一原生维护合同。上游候选仅翻译官方语句，不使用插入/删除占位行。

## 已准备与待完成

- 已准备 28 个 Core 测试用例和 6 个三协议同步/异步真实宿主重开用例。它们是待实现回归，不能作为通过证据。新增 Core 文件覆盖磁盘索引遍历方向、声明/请求方向四组合的跨零 Top-N、INCLUDE 事务与主键删除、在线断点取消/重开/续建及定义冲突。
- Core 第一版 2/15 PASS 是预期拒绝测试碰到了未支持语法；已调整为先成功建立原约束，再测试冲突，避免假阳性。13 个正向缺口失败的原 TRX 保留。
- 修订后 Core 构建零警告/错误；15 项实际运行 1 PASS/14 FAIL。通过项是已有外键与 CHECK 的大小写冲突拒绝，命名主键和其它新增合同仍失败。两版 TRX 分别保留，不合并计数。
- 扩展后的 Core 第五次准备构建零警告/错误，28 项实际 1 PASS/27 FAIL、零跳过；仍只有已有外键/CHECK 名称冲突控制通过。新增反向索引与 INCLUDE 测试都在成功前置条件上失败，没有把解析错误当作新选项正确拒绝。原 15 项结果是较早子集，不累计。对应源码原样保存于 `source-snapshots/core-contract-before-run03`。
- Server 第二次准备构建零警告/错误；6 项真实入口测试全部在命名主键 CREATE TABLE 处失败，尚未抵达后续重开断言。完整输出与 `maintenance-remote-before.trx` 保留。不能把宿主已启动写成维护功能或三协议恢复已通过。
- 已新增 5 个 identity 维护确认后真实 kill 用例，覆盖空表、存量表、删除最大行、冲突拒绝和 Int64.MaxValue。Crash 项目已零警告/错误编译；为避免共享测试入口冲突，`MaintenanceCrashScenario` 尚未接入 `Child/Program.cs` 的 dispatcher，5 项均未运行。实施阶段须接入 `crash_kill9_maintenance_identity_empty/existing/deleted/rejected/maximum` 后执行；不以正常 Dispose 替代 kill，也不宣称机器掉电证据。
- `MaintenanceCompatibility` 公开 4.0.0 消费者已零警告/错误编译并实际生成 v10 旧目录 `D:/sndb-t/i250-legacy4-original`，原 schema SHA256 为 `DB5B59DD18FCD051E6DAE87F8492D7767B9B9AB0624749B4516BF624E6472F81`。其真实 FK=`pk_Legacy`、CHECK=`PK_Legacy_1`，用来验证旧 PK 默认名冲突迁移；原目录只保留，在副本中执行后续升级。逐文件清单为 `legacy-public4-original-manifest.json`。同一消费者替换 f9ee14b7 Core/Data 后的升级前对照实际失败于缺少约束目录，未运行改名/删除阶段。
- 兼容探针另准备 `create-options`、`remove-options` 和 `reject-old`，分别验证真实新索引目录、删除最后特性后保留永久版本屏障、旧实际引擎因 unsupported format version 拒绝且 schema 不变。这三个模式等待最终实现，尚未运行；没有预占 #251 后的格式版本。
- Core 一次后续构建超 120 秒；Server 准备构建发生 CIM 观测错误且原 runner 清理路径再次失败。已按 PID、创建时间、父 PID 和完整命令复核专属遗留进程后终止，保留独立审计/清理回执。runner 增加启动即写回执、使用本次创建的 Process 句柄清理，观测错误单列；不宣称未观察的子进程已证明全部不存在。
- 接续顺序：接收 #252/#251 的最终 SHA 清单 → 重新核对类型及目录版本 → 实施共享核心 → Core 生命周期/编码/在线/恢复 → AOT 与真实 REST/h2c → 固定 SqlSugar 原版/候选同程序集对照 → 独立进程及宿主重开 → 冻结带 SHA 的交付清单。
- 正式 NuGet/Server 发布、SqlSugar 上游交付与 #227 标准 CodeFirst 事务依赖未完成时，issue 保持 OPEN。
