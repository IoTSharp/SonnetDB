# 核心能力事实核查：存储、恢复、并发与九模型

核查日期：2026-11-11（Asia/Shanghai）。源码基线：`main @ 92d760077ce0e38525e41e8cfd2a8694e786f029`。本报告是本轮静态核查，引用的测试名称表示覆盖入口，历史 TRX/基准结果保留其原提交与规模；本轮没有重新运行产品构建、单元测试、故障实验、基准或固定硬件门禁。没有修改产品源码、默认配置或持久化格式。

深查范围为 KV-LSM、时序写入与时间语义、关系事务、迁移/回滚/备份；对 Document、全文、向量、对象、MQ 和 Graph 检查现存入口与主要证据边界。后六项属于源码与合同交叉检查，不表示所有算子、全部并发交错或所有损坏组合已被穷尽验证。

主审本轮实时核对远端 `main @ 9d022d866d643e6a19990bf7f320267d7a2675dc`：CI `42954879045`、CodeQL `42954878806`、Connectors `42954878803` 均 success；与本报告基线的 Git 差异只有 HANDOFF 12 行，产品代码相同。因此，下面的历史完整 Core FAIL/超时 UNKNOWN 不代表当前 CI 仍失败；本轮未在本机重复执行产品测试。对应 CI 只证明实际作业范围，不替代 G1～G12 的未运行场景。

## 结论

1. 九模型都有真实产品实现，不能称为九个空壳；也没有证据可以把九模型整体升为全部生产场景完成。统一宿主不等于统一事务、确认写持久性或恢复范围。
2. KV-LSM K1～K6、K8 的代码已接入默认产品路径。K7 后续的共享多表原子批次、持久行数、维护元数据、回滚 CLI、Version 备份及压力调度已有源码和对应测试，不应重新开发。完整 G1～G12 仍未闭环，且一部分缺口是计量/负载工具未补齐，不能都写成“已有工具，只差执行”。
3. 明确的文档滞后包括十四能力索引仍称共享多表事务/持久计数/维护元数据未实现，以及旧九模型报告仍描述只有进程内补偿、对象全桶分页、MQ 无实例快照。旧报告应保留为历史，当前入口必须以新证据纠正。
4. LSM 的快照和部分规模表现显著改善，但现有匹配测量包含缓冲写吞吐/分配退化，不能发布“全面性能提升”。真实高并发的下一步是冻结相同持久性与负载，测量共享锁、冷热表竞争、尾延迟、背压和资源，而不是直接增加并行度。
5. 不丢数据应首先落实为每模型的 ACK/提交边界、失败结果和恢复演练。时序默认 OS flush、KV 默认每写 fsync、MQ 默认 OS flush；默认确认强度不同。进程强杀、操作系统崩溃与物理断电不能互相替代。

## 已完成实现与剩余边界

| 范围 / 路线 | 当前源码事实 | 可信完成口径 | 剩余工作 |
| --- | --- | --- | --- |
| KV-LSM K1～K5c | SST/manifest/版本快照/迭代器、WAL 批次、flush/compaction、range tombstone、块缓存和共享后台调度已接线 | 这些组件及产品入口已实现；历史定向回归存在 | 原规模写放大、冷读、随机故障、资源峰值与长稳分别验收 |
| K6 共享表存储 | `KvLsmStore` + `TableManager.SharedStorage`；新表和允许迁移的 legacy 表使用共享实例 | 实现完成；不能再按“一表一 legacy rowstore”描述当前默认行为 | 1000 表 × 1 万行、冷热表公平性、跨路径恢复 |
| K7 同实例多表事务 | 主行/索引/表序列/行数进入一个 WAL batch；失败后未知提交进入恢复保护 | 同一 `KvLsmStore` 的多表原子提交已实现 | legacy / 独立 LSM / shared 混合路径仍走撤销日志，不能扩为任意 keyspace 或跨模型事务 |
| K7 迁移/回滚 | 默认打开迁移，generation 保留；无新写入才回滚；持久意图、v4 临时屏障与重入恢复 | 代码及阶段强杀测试已实现 | 真实旧版二进制无副作用拒绝、原规模/满盘/随机 200 轮等门禁 |
| K7 备份 | 固定 Version、manifest 与 WAL 长度，复制时后台维护可继续 | LSM 文件集一致性实现存在，附并发 compaction 与 late disposal 测试 | 数据库/实例/外部卷分开演练；大规模备份恢复与业务停顿预算 |
| K7 持久行数/大行恢复 | 行数与业务同 batch；元数据陈旧时恢复；大 BLOB 按 Span 投影 | 已实现，不应继续标“尚无持久计数/仍全行重复复制” | 通用 KV/TTL 计数仍扫描，冷 SST 仍读完整 block；value 分离未实现 |
| K8 默认切换 | `KvOptions.StorageEngine=Lsm`、`LegacyMigration=Auto` | 默认切换已经发生，且有既有用户决策 | 不擅自倒退默认；发行/迁移门禁与配置告知继续补证 |
| 时序现有底座 / M19、M42 | 全局写入锁、逐字段 WAL、已有 group commit、size-tier compaction、全局 TTL | 现有 ms 合同可用；不能说“没有 WAL/没有 group commit” | 原子单元、分区/TTL、重复点策略先决策；高基数/恢复/性能分项验收 |
| M50 | 输入 `us/ns` 会换算截断至毫秒，未看到库级持久精度实现 | M50-T01～T30 仍 planned，关系 DATETIME2 的 100ns 不能替代 | 冻结新旧协议/格式/精度合同后分批实现 |
| M45 | 既有聚合、sketch、窗口、部分分组和状态守卫已实现 | 局部完成 | 通用 merge/retract、更新删除修正、增量物化与批流等价不完整 |
| M46 | 旧可选 codec、Int64 v7 RLE/raw 回退、部分统计 guard 已实现 | 局部完成 | 完整自适应选择、Gorilla window reuse、String/Boolean/Timestamp 深化、范围解码和统一门禁未完成 |

关键源码与测试定位：

- `src/SonnetDB.Core/Kv/KvOptions.cs:13` 默认 LSM，`:20` 自动迁移，`:32` KV 默认强同步。
- `src/SonnetDB.Core/Tables/TableManager.SharedStorage.cs:42` 先补回滚恢复，`:55` 恢复旧事务期间禁迁移，`:60` 导入共享实例。
- `src/SonnetDB.Core/Tables/TableManager.cs:1399` 检查同一个 shared store；`:1416` 统一提交；`:1427` 起未知提交保护；`:1438` 起保留混合布局 journal 路径。
- `tests/SonnetDB.Core.Tests/Tables/SharedLsmTableTransactionTests.cs:17` 单 WAL/单 sync/索引/重开；`:64` 丢确认不补偿；`:79` WAL 故障只读；`:96` 行数；`:134` 旧 journal 先恢复再迁移。
- `tests/SonnetDB.CrashTests/KvLsmMigrationCrashTests.cs:15` 与 `:61` 是独立 KV 7 阶段和共享迁移 6 阶段真实子进程强杀；`KvLsmFollowupCrashTests.cs:36` 包含事务提交前/WAL 后/确认后/缓冲普通写配置下的强杀；`:55` 包含回滚持久阶段。
- `tests/SonnetDB.Core.Tests/Kv/Lsm/KvLsmFollowupTests.cs:19` SST v3/范围墓碑；`:49` 固定备份不包含后续 WAL；`:74` 压力暂停；`:119` 引擎先关闭后的句柄回收；`:133` 真实 BackupService 与 raw KV compaction 并发。
- `tests/SonnetDB.Core.Tests/Tables/TableStoreRecoveryProjectionTests.cs:24` 大行分配断言，`:65` 索引修复。

## 数据安全与恢复：已经确认和仍需证实的事

### 确认强度不能合并表述

`src/SonnetDB.Core/Engine/TsdbOptions.cs:27`～`:53` 明确区分 BufferedStream、OS flush 和 fsync；默认 `SyncWalOnEveryWrite=false`、`FlushWalToOsOnWrite=true`。`src/SonnetDB.Core/Mq/SonnetMqOptions.cs:26`～`:47` 默认 OS flush、未默认 fsync，同时已有发布 group commit。`KvOptions.cs:32` 默认每写 fsync。

因此，“默认已确认写保证物理掉电不丢”没有统一代码前提，也没有本轮硬件证据。实际强同步仍依赖文件系统与存储设备兑现刷新。建议增加统一可检查的 durability profile/运维矩阵，逐模型标明确认点、调用配置、重开对账方法和允许损失窗口；本轮只调整路线，不擅改已发布默认配置。

时序 `src/SonnetDB.Core/Engine/Tsdb.cs:914` 先取得 schema/write 锁，`:925` 将同步请求交给已有 `_walGroupCommit`，`:930` 在锁外等待；`:2308`、`:2316` 仍每个 FIELD 单独写 WAL 和 MemTable。若多字段 Point 写入中途失败，不能由“每条记录有 CRC”推断整点原子。这是需要冻结 T1 并覆盖的合同边界，本轮未注入该故障，不把风险推导写成已复现的数据丢失。

### 默认迁移的门禁应靠前

K8 默认切换已存在，且设计 §7.6.6 记录用户曾将切换准入限定为代码/本机功能回归。本轮不能擅改该决定。但自动迁移会触及每个旧库首次打开，故真实旧引擎拒绝、迁移/回滚中断、磁盘不足、失败后目录可再次打开和最新源码双引擎回归应排在新增模型/压缩策略之前。

现有阶段强杀只证明相应阶段与语料，不等价于随机 200 轮、全数据规模或物理掉电。原 Windows CURRENT 改名 Access denied 未稳定复现，根因仍 UNKNOWN；不能写“已修复”。完整 Core 的历史 FAIL/超时 UNKNOWN 与后续定向 PASS 应保留来源，不能相加成最新完整 Core 通过。

### 恢复已有实现，范围仍需拆开

`Tsdb.CreateConsistentBackup`（`src/SonnetDB.Core/Engine/Tsdb.cs:1529`）协调数据库内 schema、时序、表、文档、Graph、序列和 KV；`:1537`～`:1566` 的数据库级锁覆盖复制回调。LSM Version pin 允许维护继续，不意味着全部业务写入都已成为无停顿在线备份。

Server MQ 的 `DataRoot/.system/mq` 已有独立快照/恢复，不应重做；`src/SonnetDB.Core/Mq/SonnetMqStore.Snapshot.cs:16` 是实际入口，`docs/sonnetmq-instance-recovery.md:3` 明确它不包含数据库、外部对象卷、用户/Token/grant 和 Server 配置。`tests/SonnetDB.Core.Tests/BackupServiceTests.cs:31` 已覆盖源目录离线后分别恢复对象正文和 MQ 位点的旅程。下一步应补完整实例恢复清单及故障/容量演练，不能把“单库备份成功”写成“Server 全部恢复”。

## 性能与高并发核查

### 现有证据同时存在收益与代价

`docs/benchmarks/kv-lsm-followup-20261110.md:45`～`:61` 绑定测量源码 `c171dd713cd8`；最终交付源码 `6f8e1695af92` 在其后增加旧空表兼容，不能把测量改标为本轮 HEAD 的 PASS。

| 匹配负载 | Legacy | LSM | 可得结论 |
| --- | ---: | ---: | --- |
| 10 万条 / 1 KiB / 缓冲随机写，3 轮中位 ops/s | 63917 | 46825 | 此规模约低 26.7%，不能全称写吞吐提升 |
| 同负载每写分配 | 1241 B | 3537 B | 约 2.85 倍，值得先定位分配来源 |
| 同负载正向 scan100 P99 | 1575.5 µs | 1693.0 µs | 约高 7.5%，需看尾延迟/分配 |
| 同负载写后快照 P99 | 4627.6 µs | 2.5 µs | 对快照路径有明显收益，不代表所有负载 |
| 10 万条 / 100 B / 逐次 fsync，3 轮中位 ops/s | 2478 | 2407 | 约低 2.9%，范围重叠；不支持沿用旧单轮 22.8% 退化，也不证明所有退化消失 |

这些是历史本机受限结果，不是生产性能。本轮没有重跑，不将差值解释成根因。先保留 fsync/非 fsync 两套等价配置，绑定语料/采样/机器/源码，再优化热点；不能为吞吐改弱确认语义。

### 验收工具仍有实现缺口

`tests/SonnetDB.Benchmarks/Benchmarks/KvLsmCapacityBenchmark.cs:45` 仅分派 `kv/tables/migration/memory` 四类场景；`:456`～`:475` 枚举的是当前目录文件长度，不能推出累计物理写字节。`:530` 的 limitations 已如实声明写放大、真正冷缓存 I/O、多表 fsync 计量未跑，当前工具本身没有冒称完整 gate。

要兑现 `docs/design/kv-lsm-storage-engine.md:584`～`:595` 的 G3/G5/G10/G11，需先补对应的累计 I/O/compaction 字节、冷读条件、2/5/10 表事务、TTL churn、TRUNCATE/DROP 等负载与独立 oracle，再执行冻结规模；仅把 `--entries` 放大不能自动补齐这些测量。RSS 的十秒窗口也不等于 G7 长期上界。

### 共享调度与资源上界

`src/SonnetDB.Core/Kv/Lsm/KvWriteBufferManager.cs:16`～`:26` 直接声明 256 MiB 是软预算，超过时异步请求 flush，单引擎由 immutable memtable 背压约束。它不是进程总内存硬上限；解码块、索引/filter、结果物化、大值和其他模型仍有独立成本。

`KvLsmEngine.Apply` 在 `KvLsmEngine.cs:768` 进入写锁；共享实例将关系表写入聚到同一路径。把锁分片/增加后台线程作为既定方案缺乏当前剖析证据。优先测 1/4/16 等受控客户端数、冷热表竞争、读写 P99、lock/permit 等待、flush/compaction 积压、实际工作集和取消恢复；只有确认瓶颈后才选择 group commit、编码减少分配或更细分片。

## 其他模型交叉核查

| 模型 | 当前源码/测试入口 | 事实与限制 |
| --- | --- | --- |
| Document | `DocumentCollectionStore.cs:114`、`DocumentCollectionStore.Bulk.cs:21`；`DocumentAtomicMultikeyWildcardTests`；`docs/document-mongodb-gap.json` | 主文档、索引、feed 的事务切片已有实现；MongoDB-like 子集不等于 wire/官方 Driver。百万/千万、索引重建/TTL/恢复的原规模证据仍独立。 |
| 全文 | `FullText/Storage/PersistentFullTextIndex.cs:184`；`DocumentCollectionStore.cs:1082`；`PersistentFullTextIndexTests` | 持久索引、搜索、派生重建并非空壳。中英文真实语料质量、merge I/O、更新 churn 与长稳未由本轮证明；不承诺 Elasticsearch 等价。 |
| 向量 | `Documents/Vector/DocumentVectorIndexStore.cs:263`；`Sql/Execution/DocumentVectorSearchExecutor.cs:186`、`:223`、`:300` | 已有 index / budgeted scan / Top-K，不能说所有路径都只会全物化；普通 bounded scan 在 `:523` 仍调用 `store.Scan()`，通用过滤 ANN、全路径内存、Recall@K 与规模未完成。 |
| 对象 | `ObjectStorage/SndbObjectStore.Listing.cs:27`、`:79`；`BackupServiceTests.cs:31` | 已有前缀派生索引、物理候选预算、取消/时限，旧“每页全桶扫描”不应继续当现状；旧桶首用回填、冷打开、大文件/高 churn/外部卷完整恢复另验。 |
| MQ | `Mq/SonnetMqStore.cs:117`、`:136`、`:376`；`SonnetMqStore.Snapshot.cs:16`；实例恢复合同 | publish/batch/ack/snapshot 已实现，旧“没有实例快照”已过时。默认 OS flush；至少一次与外部副作用 exactly-once 不等价。 |
| Graph | `Graphs/GraphStore.cs:192`；`GraphPreviewPhase1Tests`；`docs/m40-graph-367-production-gate.md:3`～`:17` | 原生事务/邻接/索引/遍历与本地 evaluator 已实现；Graph Beta 保持，正式外部对拍、固定容量、Couplet 联合、AOT/168 小时不能由 quick 代替。 |

上述路径相对于 `src/SonnetDB.Core/`，测试类型位于 `tests/SonnetDB.Core.Tests/`；单列的 docs/ 路径相对于仓库根。

## 需要修正的路线表述

| 当前表述或风险 | 核查判定 | 建议修订 |
| --- | --- | --- |
| 十四能力索引 relational.boundary：共享单批事务、持久计数、维护元数据 unfinished | 文档滞后，源码与测试已反证 | 改为已实现同 shared store 切片，保留混合路径、规模、发行/现场独立门禁 |
| 旧九模型报告：跨表仅进程内回滚、MQ 无实例快照、对象每页全桶扫描 | 历史报告当时事实，现状已改变 | 保留原历史证据，当前路线引用本轮结论和后续切片，不重复派单 |
| K7“源码功能补齐”被理解为 G1～G12 工具与性能已全完成 | 超出证据 | 分列引擎功能、验收计量、原规模执行、固定硬件四类 |
| K6 行仍写“前缀布隆未做”，影响表仍写“Lsm 不再使用事务日志” | 阶段历史与当前状态容易混淆 | 标为阶段时点说明；当前以 §7.7 为准，混合布局日志仍保留 |
| M46 总览 planned，而 S06 和 Int64 RLE 已实做 | 汇总状态失真 | 总览部分实现，完整自适应 S02/S09 仍未完；不得从一个 codec 关闭 M46 |
| 物理掉电安全由本地强杀或 fsync 调用推导 | 证据层级越界 | 本轮 NOT_RUN，明确 ACK/durability 配置与真实介质门禁 |
| 时序并发改进被写成“补 group commit” | 已有实现的重复立项 | 先看单写锁/逐字段 WAL/分配及读写竞争，按 T1 合同设计 |

## 推荐推进顺序与退出条件

| 顺序 | 工作 | 退出条件 |
| --- | --- | --- |
| 1 | 默认迁移/回滚与确认写可靠性收口，合并现存失败清单 | 复用最新成功 CI 并核对实际覆盖，不因历史 UNKNOWN 重跑已满足范围；补真实旧二进制拒绝且源目录无副作用、随机故障对账、未知提交不盲重试；首次 FAIL/UNKNOWN 保留 |
| 2 | 恢复范围与最小完整演练 | 数据库、实例 MQ、身份/配置、外部对象有明确清单；源离线后读回业务数据/索引/消息/位点；恢复中断与磁盘满按阶段验证 |
| 3 | 验收计量与高并发性能 | 先补 G3/G5/G10/G11 工具；相同 durability、冻结语料下定位写分配/锁等待/冷热表竞争；达不到阈值保持 FAIL/未达标，不降门槛 |
| 4 | 时序 T1～T3 与 M50-T01 共同冻结语义、版本和兼容入口 | 原子恢复单元、精度、分区/TTL、重复策略各自有决定；不把 KEEP_ALL 改为覆盖写，不把旧 ms 输入静默改义 |
| 5 | M42/V45 资源闭环及 M45 首批增量 | 全查询路径物化/预算/取消/恢复有真实入口；批/流/回填对同日志等价；不可逆聚合明确重算/拒绝 |
| 6 | M46 按真实收益推进 | S01/S09 先冻结基线与兼容；小切片证明磁盘/CPU/尾延迟/恢复收益，再决定 Auto 默认与后续 codec |
| 7 | 真实业务驱动的 M44/M48 与更大模型/算法矩阵 | 复用已完成底座；新增功能有用户旅程、质量或运营收益证据，不用算法数量替代可靠性 |

这里的顺序不将所有固定硬件/168 小时结果强制变为普通代码 issue 的关闭前置条件。代码完整实现与必要代码验证可以单独完成；环境/发行问题按其原合同继续开放。对现已授权的 K8 默认策略保持事实描述，未来变更须有独立兼容决定。

## 本轮验证与资源

- PASS：上述具名代码/测试/设计/历史收据入口的有界静态交叉核查；报告 UTF-8 落盘。
- NOT_RUN：本轮编译、测试、benchmark、真实强杀、旧二进制运行、NativeAOT 发布、固定硬件、物理掉电、生产恢复。
- UNKNOWN：未复现的历史 CURRENT 改名与部分完整 Core 超时根因；不把后续定向 PASS 改写成根因已修复，也不把历史 UNKNOWN 冒充本轮最新 CI 状态。
- 所有外部进程均为有 10 秒截止的 `rg` / `git` 只读查询，由 Cordonix 返回 PID 与子树清理结果；没有创建常驻进程、产品数据目录或临时文件。唯一写入是本报告。

## 补审：历史完成里程碑的名称、范围与后续归属

本节只核对历史归档范围、当前代码入口和明确剩余边界，不重跑测试，也不声称逐行复核全部早期实现。`CHANGELOG.md:956`～`:970` 已明确将归档限定为代码、入口、自动化回归和本地门禁，生产/远程/容量/长稳另列，因此不能仅凭这些里程碑仍有后续就反向判定历史“代码完成”全部虚假。需防止的是摘掉边界后将归档标题用作今天的整体成熟度。

原标题取证：M0～M16 见 `docs/roadmap-history.md` 对应标题；M17/M18/M19/M24/M25/M29/M30/M31/M33 见 Git 对象 `83968c81407d0d803fc31cbd97280e71252ce92d^:ROADMAP.md`；M23 取该对象总览行 23；M32/M34/M37/M38/M39 见 `3b5ff768adf946b09a3be11ed58a83cacb3c1696:ROADMAP.md` 的对应标题。Git 对象只用于准确命名，不能用其中旧状态替代当前验收。

| 里程碑 | 准确主题 / 已完成范围 | 当前证据入口与剩余归属 |
| --- | --- | --- |
| M0～M2 | 项目脚手架；内存与二进制基础设施（Safe-only）；逻辑模型与目录 | `roadmap-history.md:13`、`:23`、`:33`；属于基础实现归档，新格式/兼容仍按具体增量负责 |
| M3～M6 | 写入路径；查询路径；稳定性与性能（写入侧）；SQL 前端 + Tag 倒排索引 | `roadmap-history.md:43`～`:107`；现存 `Tsdb`/WAL/Query/SQL 入口；高基数/并发/恢复/查询预算续归 M19/M41/M42、KV-LSM 与时序决策 |
| M7 | 压缩编码 | `roadmap-history.md:109`；旧可选编码交付不等于默认压缩/完整自适应，后续 M46 |
| M8～M11 | 服务器模式；性能与发布；批量入库快路径；写入快路径（PR #45 瓶颈收收尾，原题） | `roadmap-history.md:121`、`:150`、`:161`、`:173`；早期 Server/发布/基准不替代今天候选、M49 SDK 或安装验收；历史吞吐数字不能外推 |
| M12～M13 | 函数与算子扩展（PID / Forecast / UDF）；向量类型与嵌入式向量索引（Copilot 知识库底座） | 函数/向量入口已有；真实预测质量与向量 Recall/规模分别续归 M44、M35/M36/M42 |
| M15～M16 | 地理空间类型与轨迹分析；Copilot 产品化升级（嵌入式 AI 助手 UX） | `roadmap-history.md:377`、`:450`；空间真实旅程/规模归 M36/M42，真实 provider/双网/宿主续归 M27/M44/M47 |
| M17～M18 | 可观测性与运行时可见性（Observability & Runtime Visibility）；VS Code 数据库扩展（SonnetDB for VS Code） | 原有观测与扩展交付不等于全模型 SLO 或新管理面完整；续归 M41/M42/V45、M47-U07 |
| M19 | 生态适配底座能力（关系 + KV/缓存 + 对象桶 + 大量 measurement） | 已有底座与 runner；固定四档容量仍归 M19 #125，不能再只写整体完成 |
| M21 | Document Store 单机能力升级（MongoDB-like，不做协议兼容） | `roadmap-history.md:597`、`:649`；该历史合同已把 Studio/参考 parity/长稳/发行明确分到 M24/M25；不能按缺官方 Driver 判定未完成 |
| M23 | 搜索与向量引擎合并（DotSearch / DotVector 收编） | `docs/search-vector-engine-consolidation-roadmap.md:5`、`:22`；算法已迁入 Core，VectorData 适配到 Data；质量/容量续归 M35/M36/M42 |
| M24 | SonnetDB Studio 管理体验升级（Document 管理面） | 原 Explorer/validator/import-export/workbench 已有；三宿主统一管理新合同归 M47，不能从 M24 完成推导 M47 完成 |
| M25 | Document Store 验收、文档与发布治理 | 固定百万/千万、恢复与真实硬件证明仍待补，当前应保留验收任务 |
| M26 | 连接器路线独立化（C ABI + 多模型 API） | `roadmap-history.md:669`；旧多语言/ABI 基线已交付，新原生 HTTP/2 与 embedded 拆分归 M49；不得将旧 smoke 等同新矩阵完成 |
| M28 | 可靠性、并发正确性与热路径加固（Reliability / Concurrency / Performance Hardening） | `roadmap-history.md:698`、`:728`；已修复具名缺陷和旧帧/SDK范围，历史“消除整类问题”不能当未来实现永不丢数的保证；新 LSM/新格式仍独立验收 |
| M29 | 多模型统一管理工作台（Multi-Model Management Workbench） | 旧三面工作台底座已交付，干净 OS/安装/升级/卸载仍归 M29 #258，后续统一管理面归 M47 |
| M30 | 多协议设备接入扩展（Multi-Protocol Device Ingestion） | 总览具体为 Sparkplug B / CoAP / Line Protocol UDP；被动接入不能替代 M34 的主动 Modbus source/endpoint，也不替代真实设备验收 |
| M31 | 时序聚合类型语义增强（Selector / Categorical Aggregates） | `IAggregateFunction.cs:24`、`FunctionRegistry.cs:114`、`:1012`；selector/category 类型路径已有，通用 state/retract、完整空集/溢出矩阵续归 M45 |
| M32 | Document MongoDB-like 易用性 | `DocumentCollectionStore`、Bulk/FindAndModify、`docs/document-mongodb-gap.json`；常用查询/更新、multikey/wildcard、mixed Bulk 与迁移切片已交付，完整 MongoDB 语义明确排除，容量归 M25 |
| M33 | 时序聚合执行与下推优化（Aggregate Execution & Pushdown Optimization） | `docs/benchmarks/m33-aggregate-execution-pushdown.md:1` 与 `M33PushdownBenchmark`；Geo/多聚合/残差、count 与 LIMIT/latest-N 指定快路径已交付，冷读/跨模型/生产尾延迟续归 M41/M42/M46 |
| M34 | Modbus TCP 内建映射表 | CHANGELOG 的 #288～#296 分阶段明细；DDL/catalog、master/slave、受限写、审批/审计已有；生产设备/通讯与恢复验收独立，不扩大为任意工业协议平台 |
| M37 | 视图与物化视图 | `src/SonnetDB.Core/Views/ViewManager.cs`、`MaterializedViewManager.cs`；`SqlExecutorViewTests`/`SqlExecutorMaterializedViewTests`；逻辑视图与显式全量 refresh 已交付，增量/调度刷新属于 M45-C04，不可把 M37 直接称为增量物化已完成 |
| M38 | SQL 存储过程与触发器 | `src/SonnetDB.Core/Routines/SqlRoutineRuntime.cs`、`RoutineManager.cs`；`SqlRoutineTests`；V1 `LANGUAGE SQL` 与关系 AFTER ROW 子集完成，外部脚本语言并非该片承诺，V2 归 M39 |
| M39 | SQL 触发器第二版 | `docs/audits/m39-production-20260906.md:11` 明确编码/本地完成、生产混合负载/SLO另验；`m39-338-20260908.md:100` 外部投递测试目标为文件，不是生产 MQ；`m39-339-admission-20260908.md:3` 仅 Document/measurement 原生事件与批次身份准入，不实现其 SQL 触发器运行时 |

补审未发现足以把上述历史代码归档整体撤销的证据；明确应修正的是把“旧代码完成”转述为“当前全部生产/容量/新范围完成”。特别防止 M7→完整 M46、M26→完整 M49、M37→增量物化、M39 原生事件准入→Document/measurement SQL 触发器的越界推论。M22 完整原标题未在本补审中确认，交给主审从权威索引补核，不猜测填入。
