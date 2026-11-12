# T1：显式单 Point WAL v2 底层合同

2026-11-12（Asia/Shanghai），基线 `main @ 93cd7807fd727c474ff119e1690b940e40f0ecda`。本轮只完成底层独立 WAL 切片，验证见[第二阶段记录](../audits/capability-phase2-20261112.md)。Tsdb 默认写入、WalReplay、catalog、WalSegmentSet、rotation、checkpoint 与 flush 发布尚未接入，不能称整个时序引擎已实现整点原子恢复。

## 选择与公开入口

采用单记录 envelope：一个 record header 和 payload CRC 覆盖一个 Point 的全部字段。相比 begin/commit marker，它不需要跨记录或跨文件的未提交字段缓存；截断或损坏的记录不返回其中任何字段。后继记录不能越过损坏记录继续被消费。整个 `WriteMany` 批次不在此原子单元内。

- `WalWriter.OpenAtomicPoints(path, startLsn, bufferSize, recoveryOptions, cancellationToken)` 显式选择 v2；`AppendAtomicPoint(seriesId, pointTimestamp, fields, cancellationToken)` 返回整点共享的 LSN。
- `WalReader.OpenAtomicPoints(path, recoveryOptions, cancellationToken).Replay()` 返回一个 `AtomicPointRecord`，不展开为逐字段提交。
- 原 `Open` API 继续写读 v1。默认 Tsdb 保持 v1；v2 当前拒绝逐字段、CreateSeries、Checkpoint 和 Delete。`WalFileHeader.IsValid()` 保持精确匹配 v1，显式入口精确匹配 v2，双向拒绝且不自动升级旧文件。

## 文件与载荷

文件头保持原 64 字节 unmanaged little-endian 布局，magic 仍为 `SDBWALv1`，`FormatVersion` 为 2；magic 本身不替代版本校验。record type 为 `AtomicPoint = 6`，原 32 字节 record header 的 checksum 保护类型、长度及 LSN，payload CRC 保护整点。旧 v1 reader/writer 因文件头版本不同而拒绝；本轮验证了当前源码的 v1 路径，没有运行历史发行二进制。

payload 为 Int32 字段数，随后每字段为 Int32 长度及既有 WritePoint 字段载荷。所有字段必须具有同一 SeriesId 和 Unix 毫秒时间戳，名称按 Ordinal 唯一且非空白。字段名及字符串值在写前拒绝非法 UTF-16，避免 UTF-8 替换使不同名称碰撞。读取同时校验字段类型、长度、padding、vector 维度及剩余字节。

单 Point 为 1～1024 字段、最多 8 MiB payload，完整编码及输入校验先于记录首字节写入。v2 起始 LSN 为 `1..Int64.MaxValue-1`，写完最大可用 LSN 后停止追加，不能回绕。时间戳仍为非负 Unix 毫秒，KEEP_ALL 重复点、全局 TTL 和无 measurement 分区保持现状。

## 失败、恢复和耐久

取消只在首次写入前检查；开始写入后的取消不撤回记录。append、Flush 或 Sync 异常封写，调用方不得根据异常自动补偿或盲重试，应关闭并重开对账。unknown append 后 Dispose 不使用旧字节计数截掉可能完整存在的记录，也不写虚假 footer；重开扫描完整 CRC/结构后从最后合法偏移追加。损坏/截断后缀在成功的 Writer 打开时被截除，因此修复前应保留原文件用于取证。

`AtomicPointWalRecoveryOptions` 默认最多 100000 条记录、256 MiB 文件字节及从 Open 开始的 10 秒墙钟期限，可指定预算和取消；触限/取消在 `SetLength` 前失败并保留原文件。每个 Point 的同步读取/解码最多 8 MiB，不能在内部抢占，预算与取消在前后检查。Reader 的期限包含调用方枚举间隔。追加上限不自动限制文件总大小，超过默认恢复上限时必须显式提供合适预算；本切片没有自动滚动。

Append 成功不等同 fsync；只有显式 Sync/Dispose 沿既有合同执行同步。进程强杀只证明已验证的进程恢复边界，不证明介质刷新、物理掉电零丢失或生产耐久。没有改变默认 fsync。

## 接续门禁

后续接入 Tsdb 前必须单独冻结 catalog 与字段编号、滚动时的整点边界、内存发布与封写、checkpoint/flush 回收、旧目录迁移/备份/降级和 unknown 提交查询方式。旧 v1 已存在的部分提交事实不能被补造为原子提交。T2 分区/TTL、T3 FIRST/LAST、us/ns 精度继续待决，原[时序草案](time-series-write-partition-duplicates-decisions.md)只在本合同明确的 T1 底层范围内推进。
