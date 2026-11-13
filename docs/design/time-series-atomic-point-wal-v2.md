# T1：默认单 Point WAL v2 与目录升级合同

2026-11-13（Asia/Shanghai），本阶段基线 `main @ aa5e28fdfbcd71775562973686ee3cdf2e9af815`。本合同覆盖默认 `Tsdb` 接入和旧目录升级；上一轮独立底座的原始结果保留在[第二阶段记录](../audits/capability-phase2-20261112.md)，不作为本轮新源码验证。阶段完成条件见[完整退出合同](capability-code-stage-completion-20261112.md)。

## 选择与公开入口

采用单记录 envelope：一个 record header 和 payload CRC 覆盖一个 Point 的全部字段。相比 begin/commit marker，它不需要跨记录或跨文件的未提交字段缓存；截断或损坏的记录不返回其中任何字段。后继记录不能越过损坏记录继续被消费。整个 `WriteMany` 批次不在此原子单元内。

- `WalWriter.OpenAtomicPoints(path, startLsn, bufferSize, recoveryOptions, cancellationToken)` 显式选择 v2；`AppendAtomicPoint(seriesId, pointTimestamp, fields, cancellationToken)` 返回整点共享的 LSN。
- `WalReader.OpenAtomicPoints(path, recoveryOptions, cancellationToken).Replay()` 对 Point 返回 `AtomicPointRecord`，控制记录保留各自类型，不展开为逐字段提交。
- 原 `WalWriter.Open` / `WalReader.Open` 继续写读 v1，精确拒绝 v2；显式 v2 入口精确拒绝 v1。`WalSegmentSet.OpenAtomicPoints` 为默认 Tsdb 提供混合旧段恢复及新 v2 段写入，逐字段 `WritePoint` 不能写入 v2。
- v2 同时承载完整校验的 `CreateSeries`、`Checkpoint`、`Delete` 和 `Truncate` 控制记录。恢复保留整点记录边界，控制记录不把整个 `WriteMany` 变为一个原子批次。
- 公共 `Tsdb.Write` / `WriteMany` 在 schema 和 WAL 变更前复制并校验完整 Point；追加单记录后，在 MemTable 发布门内发布全部字段。读桶、聚合和元数据使用同一发布门；多个独立查询之间没有事务快照保证。

## 文件与载荷

文件头保持原 64 字节 unmanaged little-endian 布局，magic 仍为 `SDBWALv1`，`FormatVersion` 为 2；magic 本身不替代版本校验。record type 为 `AtomicPoint = 6`，原 32 字节 record header 的 checksum 保护类型、长度及 LSN，payload CRC 保护整点。旧 v1 reader/writer 因文件头版本不同而拒绝；真实历史程序集的来源、版本与 SHA、旧/新公共入口矩阵在本阶段审计中独立记录，不能用当前读取器改常量冒充历史端。

payload 为 Int32 字段数，随后每字段为 Int32 长度及既有 WritePoint 字段载荷。所有字段必须具有同一 SeriesId 和 Unix 毫秒时间戳，名称按 Ordinal 唯一且非空白。字段名及字符串值在写前拒绝非法 UTF-16，避免 UTF-8 替换使不同名称碰撞。读取同时校验字段类型、长度、padding、vector 维度及剩余字节。

单 Point 为 1～1024 字段、最多 8 MiB payload，完整编码及输入校验先于记录首字节写入。v2 起始 LSN 为 `1..Int64.MaxValue-1`，写完最大可用 LSN 后停止追加，不能回绕。时间戳仍为非负 Unix 毫秒，KEEP_ALL 重复点、全局 TTL 和无 measurement 分区保持现状。

## 失败、恢复和耐久

取消只在首次写入前检查；开始写入后的取消不撤回记录。append、Flush 或 Sync 异常封写，调用方不得根据异常自动补偿或盲重试，应关闭并重开对账。unknown append 后 Dispose 不使用旧字节计数截掉可能完整存在的记录，也不写虚假 footer；重开扫描完整 CRC/结构后从最后合法偏移追加。损坏/截断后缀在成功的 Writer 打开时被截除，因此修复前应保留原文件用于取证。

`AtomicPointWalRecoveryOptions` 默认最多 100000 条记录、256 MiB 文件字节及从 Open 开始的 10 秒墙钟期限，可指定预算和取消；触限/取消在 `SetLength` 前失败并保留原文件。每个 Point 的同步读取/解码最多 8 MiB，不能在内部抢占，预算与取消在前后检查。Reader 的期限包含调用方枚举间隔。默认 Tsdb 的 `WalSegmentSet` 在完整 Point 边界滚动，并使单段记录/字节预算可由一次默认恢复预算读取；超过显式恢复上限时仍必须提供合适预算。

默认 Tsdb 的 `WalRecovery` 为整个目录最多 10000000 条记录、4 GiB 文件字节与一分钟。发现阶段同时限制目录项数、期限和取消，在升级旧名、排序和 active 修尾前预检；它与直接使用 WalReader/WalWriter 的较小独立默认值分别记录。

Append 成功不等同 fsync；只有显式 Sync/Dispose 沿既有合同执行同步。进程强杀只证明已验证的进程恢复边界，不证明介质刷新、物理掉电零丢失或生产耐久。没有改变默认 fsync。

## 目录升级、备份和降级

- 打开旧 v1 目录时先在生命周期租约下校验全部旧段与尾部，再创建新的 v2 active 段；旧非空段字节不重编码，不能补造其历史整点原子性。旧段存在无法证明的损坏尾时 fail closed，原文件保留供修复，不越过后继记录。
- catalog 版本从 v1 原子升级为 v2，条目布局不变但旧版本读取器明确拒绝 v2。升级在恢复、Segment 校验和 WAL 对账完成后执行；catalog 保存、checkpoint、flush 和备份都保留 v2 屏障。备份读回必须验证升级后的 catalog 与完整 Point 数据。
- 已发布 Segment 或持久 checkpoint 存在但 catalog 丢失时，在 WAL mutation 前拒绝打开；完整 WAL-only 目录仍可从 CreateSeries 重建。旧 v1 无 sidecar 的兼容升级必须先持久化与真实已加载 Segment 绑定的边界，随后才能标记 catalog v2；v2 不套用旧 fallback。
- Windows 已有 checkpoint 使用原子 Replace、首次使用 Move，读句柄允许 Delete 共享且固定 64 字节复制后即释放。已有读视图保留完整旧 state，新路径读取完整新 state；异常仍封写，不增加重试。
- unknown append、Flush 或 Sync 后封写当前实例；不得依赖旧长度回退或盲重试。关闭并重开由 CRC/结构扫描确认完整 Point 后再决定是否可继续写入。旧 v1 已存在的部分提交事实不能被补造为原子提交。

T2 分区/TTL、T3 FIRST/LAST、us/ns 精度继续待决，原[时序草案](time-series-write-partition-duplicates-decisions.md)只在本合同明确的 T1 范围内推进。
