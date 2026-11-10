# KV-LSM K7 后续代码与本机验证

本报告记录从干净的 `main @ f71e8a80eeb08eade3080b44bb7d3fa9a5ccff96` 开始的本地代码、验证与文档阶段；该阶段没有执行提交、推送、发布或部署。随后用户明确授权提交并推送本批代码，精确提交、门禁与双端读回见根 HANDOFF 和 `artifacts/kv-lsm-followup-20261110/submission/`，不能用 Git 交付替代发行或生产验收。准确命令、源码身份、TRX 和独立进程测量放在 `artifacts/kv-lsm-followup-20261110/`；前一批测量和失败记录仍保留在原证据目录，不改写为本轮结果。

## 代码交付

实现与兼容合同见[设计 §7.7](../design/kv-lsm-storage-engine.md#77-k7-后续实现2026-11-10本地源码)。本轮包含同共享实例多表原子批次、`0|maint|id` 维护令牌、与业务写同批的持久主行计数、带排他租约的离线回滚 CLI、持久回滚意图与可重入恢复。legacy／独立 LSM／共享表混合事务仍保留原恢复日志。

共享 SST 新写使用 v3，旧读取器明确拒绝，新读取器继续支持 v1/v2；独立 SST 仍使用原格式。Version 备份固定 SST 集合、独立 manifest 快照及 WAL 字节边界；复制期间 compaction 可以继续。后台压力规则仅暂停可延后的 compaction，flush 与 L0 停写水位的必要推进不被阻塞。大行恢复在快照内按 Span 投影未选中的载荷。

`RowCount` 的内存读取与共享表重开计数恢复为 O(1)；旧目录缺失计数或旧写者推进 sequence 后，只在打开阶段重算。任意 KV 前缀和 TTL 计数仍保留原扫描合同，未把它们宣称为 O(1)。当前 Version 备份使用文件复制，没有硬链接优化。

## 有界复现与回归

| 项目 | 状态 | 收据及限制 |
|---|---|---|
| 原 CURRENT 改名偶发 | UNKNOWN | 修改前随机重开 3/3 PASS，未复现原 Windows Access denied；没有加盲重试、跳过或放宽断言 |
| 原 mid_compaction 窗口 | PASS（本轮单项） | `crash-mid-compaction-baseline.trx`，1/1；不能单独解释历史整套超时 |
| 中间源码聚焦回归 | PASS | `k7-focused-04.trx`，76/76；包括共享事务、回滚、SST v3、Version 备份和压力调度 |
| 中间源码完整 CrashTests | PASS | `crash-full-01.trx`，122/122，零跳过；测试约 5.2 分钟，含编译总计约 7.6 分钟 |
| LSM 相关回归 | PASS | 先 `k7-focused-final.trx` 755/755；恢复顺序、generation、空表与并发夹具修正后 `k7-recovery-final.trx` 762/762。阶段源码身份保留，不将前轮结果倒改为最终树全量门禁 |
| 同场 Modbus 有限复核 | PASS（根因 UNKNOWN） | 聚焦回归内一次、`modbus-repeat-02/03.trx` 各一次，随后合计 3/3；没有修改 Modbus 源码或放宽等待断言 |
| 完整 Core LSM 首轮 | FAIL | `core-lsm-final-01.trx`，8249/8254；首次五个失败保留，四项已经由源码／测试入口修正并通过聚焦回归，另一个为 Modbus 调度超时 |
| 完整 Core LSM 再跑 | UNKNOWN | `core-lsm-final-02.json`：900 秒上限，无完整 TRX；中止前 stderr 未记录失败。没有继续扩张整套预算 |
| Legacy 相关回归 | PASS | `k7-legacy-final.trx` 861/861；恢复顺序／generation／空表修正后的增量 `k7-legacy-recovery-final.trx` 105/105；完整 Legacy Core 本轮 NOT_RUN |
| 完整 CrashTests 接续验证 | PASS | `crash-full-final.trx` 126/126，零跳过；共享迁移真实强杀包含非零 generation，新增空表中断后回滚及 v4 屏障两种目录类型 |
| 备份句柄最终增量 | PASS | 先用独占文件复现 1/1 FAIL，修正备份在引擎关闭后释放的读取器回收，`backup-disposal-final.trx` 40/40。此微调晚于上面的完整 CrashTests，按增量范围单列 |
| 旧空表记录最终兼容增量 | PASS | `old-empty-compat-final.trx` 31/31。旧 `importedSequence=0` 只在当前逻辑 sequence 等于已提交创建 sequence 时允许回滚；有新写入则拒绝。此兼容微调晚于性能采样 |
| Benchmarks.Tests | PASS | 原 v4 协调日志假设首次 83/84；更新 v5 的实际布局／提交协议／SST 字节合同后 `benchmark-tests-final-02.trx` 90/90。其后只有备份生命周期增量及测试空白修正，最终基准程序重新构建通过 |
| Core／CLI AOT 与 Trim 分析 | PASS | 生产项目默认 `IsAotCompatible=true`、warnings-as-errors；本轮构建无 IL/AOT 警告。NativeAOT 发布、运行、安装为 NOT_RUN |
| 最终全仓格式检查 | PASS | 完整 restore；`format-final-04.json` 为最后交付源码树上的规定只读格式命令，退出 0。`.dcproj` 非语言项目提示在诊断日志中保留；CrashTests 新文件另行通过 whitespace 验证 |

完整 CrashTests 的详细计时显示 EF 迁移强杀的单项耗时约 13～33 秒，多项累加可超过先前包含构建的 360 秒预算。本轮没有观察到永久挂起；历史没有完整 TRX 的超时仍保留 UNKNOWN，不能倒改为 PASS。额外的 KV／表／SQL 大范围首轮在 240 秒预算被回收，缺完整 TRX，保留 UNKNOWN；三个聚焦构建阶段的编译失败及修复日志保留。最终树的回归另行记录。

Core 首轮的四项可确认原因：三个 `SqlRoutineProductionTests.Journal_*` 场景直接修改 `transaction.sdbtxn`，现明确选择保留日志合同的 legacy 引擎，全部恢复和损坏断言保留，未标记跳过。共享原子事务由独立故障／强杀用例覆盖。新增 5 MiB 大行测试首次分配 10,508,272 字节；索引计数扫描越过索引前缀时也读入相邻大行块，随后主行修复再次读取。现将索引计数和主行 Span 投影合并为同一个稳定扫描；原“分配低于 payload + 1 MiB”的断言已通过，没有放宽阈值。

离线回滚另增加 v4 临时降级屏障，覆盖“屏障已持久化、意图尚未写出”的窗口。该窗口中新版普通打开明确失败，CLI 可接续；已有意图则打开前补完恢复。普通目录继续为 v3 布局标记，共享 SST 新写为 v3，三者不是同一个格式版本字段。

后续审查修正了旧多表日志恢复与自动迁移的顺序，并保留共享导入的 generation；空表使用创建时已经提交的导入 sequence。强同步下的维护元数据与共享回滚 DROP 去掉重复 fsync，缓冲模式仍补一次。memtable 并发测试原先可能让写者先于四个线程池读者结束，导致扫描次数为零；现采用五个明确启动的 actor 与首次观察握手，保持排序、完整值及 150,000 条最终计数断言，并增加时间和循环上限。首次 FAIL 仍在原始 TRX 中。

所有重负载串行使用 `sonnetdb-heavy-validation.lock` 的 `FileShare.None` 租约、`DOTNET_PROCESSOR_COUNT=2`、单 MSBuild 节点与禁持久 build server；记录 PID、创建时间、父进程和原始 argv。运行日志实时写盘；外层 Cordonix Windows Job 负责回收所属后代。失败、超时和未运行层级不由其他 PASS 替代。

## 重复测量与剩余门禁

最终测量源码为 `f71e8a80+kv-lsm-followup-c171dd713cd8`。精确文件及实际 Core／Benchmarks 程序集 SHA 见 `source-measurement-final.json`，源码归档 `measured-source-c171dd713cd8.zip`（SHA-256 `af3bcc249c3e159b1c0d15e91da34f12f84444ed5e509022758025df745c4b93`）。较早 `source-before-benchmarks.json` 与 c79b 归档属于备份句柄微调前的准备快照，没有改写为这组测量的来源。

第一组 12 次独立进程测量全部 PASS，另有 64 条小语料试跑 PASS；完整读回与数据目录独立读回清理均通过。每个引擎、每种负载重复三次，种子 42，500 次请求样本。下表给出三轮中位数（括号为三轮范围），单位均明确；不代表生产性能。

| 负载 | 引擎 | 写入 ops/s | 正向 scan100 P99 µs | 反向 scan100 P99 µs | 写后快照 P99 µs |
|---|---|---:|---:|---:|---:|
| 1 万条／100 B／逐次 fsync／顺序 | Legacy | 2844（2809～2958） | 633.1（578.6～635.7） | 808.8（771.9～875.6） | 8143.1（7824.9～9290.0） |
| 同上 | LSM | 2763（2717～2791） | 192.6（152.3～212.3） | 312.3（288.4～322.7） | 8.6（8.3～9.1） |
| 10 万条／1 KiB／缓冲 WAL／随机 | Legacy | 63917（62977～64557） | 1575.5（1393.2～2099.6） | 1875.0（1438.2～2430.3） | 4627.6（4592.2～4843.4） |
| 同上 | LSM | 46825（46422～50384） | 1693.0（1615.6～2033.1） | 1489.8（1391.8～1745.8） | 2.5（1.2～3.0） |

分配记录（计时操作线程、校验在计时与分配采样外）：10 万条／1 KiB 缓冲写每操作中位分配 Legacy 1241 B、LSM 3537 B；正向 scan100 每请求分别约 253281／292286 B；写后快照分别约 164074／344 B。写入分配与部分尾延迟仍有明确差距，不能把快照收益包装为全性能门禁通过。没有可用的 `dotnet-trace`，本轮未生成 CPU 调用栈；已确认的修复仅包括实际重复同步与大行重复读取／复制，不推断其他 CPU 热点。

为核对原 10 万条逐次 fsync 现象，另以同规模、100 B、200 样本补三轮／引擎的六次测量，也全部 PASS：Legacy 写入中位 2478 ops/s（2313～2729）、LSM 2407 ops/s（2396～2639）。本轮差约 2.9%，三轮范围重叠，没有复现旧单轮约 22.8% 的差幅；不能据此声称所有 fsync 退化已经修复。同负载正／反向 scan100 P99 中位数分别为 Legacy 526.7／602.7 µs、LSM 200.7／453.2 µs。

合计 18 次正式测量、1 次小语料试跑；每组完整读回、所有权清理标志和目录不存在读回均通过。原始报告与两个独立汇总为 `performance-summary.json`、`performance-matched-summary.json`，前 12 份报告没有覆盖。最终交付源码在采样后仅增加旧空表零导入 sequence 的兼容回滚，身份为 `6f8e1695af92`，见 `source-final-delivery.json`；不能把这些 `c171dd713cd8` 的测量改标成新身份的 PASS。

百万／千万条全性能门禁、完整 1000 表 × 1 万行、随机强杀 200 轮、真实旧版本二进制、一亿条／168 小时固定硬件和生产迁移仍独立验收。时序重复时间戳与分区语义保持原合同，未在本轮自行决定。
