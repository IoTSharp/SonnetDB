# KV 存储内核 LSM 化设计（KV-LSM）

> 状态：**设计草案**（2026-11-08，Asia/Shanghai）。未实现、未评审。
> 来源：[竞品对照代码审查 P0-1 / P0-2](../audits/competitive-code-review-20261108.md)。
> 证据层级：现状分析 = 源码阅读 PASS；本文所有性能目标 = NOT_RUN，需由第 9 节基准给出收据。

### 已定决策（2026-11-08，用户确认）

| # | 问题 | 决策 | 落点 |
|---|---|---|---|
| D1 | 新建数据库何时默认 `Lsm` | 本机门禁（G1～G8、G10～G12）PASS 即切换；G9 固定硬件单独记录，不阻塞 | §10 K8 |
| D2 | 旧格式 keyspace 迁移方式 | **打开时自动迁移**（崩溃可续、失败保留旧数据） | §7.4 |
| D3 | range tombstone 阶段 | **提前到 K5**，与 compaction 一起交付 | §6.5 |
| D4 | 数千张小表各自一个 keyspace | **纳入本设计**：同一数据库的关系表共享一个 LSM 实例，按稳定存储 ID 分区 | §5.7 |

## 1. 背景：现状与问题

`KvKeyspace` 是关系表、JSON 文档、原生图、对象元数据、Sequence、RAG/人脸存储、数据库代际目录的共同底座（`src/SonnetDB.Core` 中 `Kv/` 目录外有 25 个文件直接引用 `KvKeyspace`）。路线图历史 #146 的“磁盘有序 KV”以**单一有序状态文件 + 稀疏目录**交付，解决了“全部常驻内存”问题，但留下了伸缩性上限。

### 1.1 现状结构（源码核对）

| 组件 | 实现 | 位置 |
|---|---|---|
| 内存写缓冲 | `KvOrderedOverlay`：`Dictionary<byte[],KvValueEntry>` + 可选 `SortedSet<byte[]>` | `Kv/KvOrderedOverlay.cs` |
| 冻结缓冲 | checkpoint 期间 `_frozenValues`（旧 overlay） | `KvKeyspace.FreezeCheckpointLocked` |
| 磁盘状态 | **一个** `*.SDBKVSNP` 或 `*.SDBKVSEG` 文件，v5：每 16 条 restart 的前缀压缩、逐条 CRC | `Kv/KvStateFile.cs` |
| 磁盘目录 | 文件内**无索引块**；打开时顺序遍历全部条目，在内存中每 16 条建 1 个 fence（含 key 副本） | `KvStateFile.OpenDiskState` |
| 当前状态发现 | 无 manifest；取文件名中序号最大且 generation 匹配者 | `KvKeyspace.LoadLatestState` |
| WAL | `active.SDBKVWAL` v3，checkpoint 时密封为 sealed WAL | `Kv/KvWalFile.cs` |
| checkpoint | overlay ≥ `MaxOverlayEntries`(100k) 或 WAL ≥ 256 MiB 时触发 | `KvKeyspace.IsWriteBudgetExhaustedLocked` |
| 快照 | 首次读复制 overlay 为数组并排序，按 sequence 缓存；单许可门闩 | `KvKeyspace.AcquireReadSnapshot` |
| TTL 回收 | 后台按游标在写锁内扫描全部可见条目，每轮 ≤4096 个/50 ms | `KvKeyspace.CleanExpiredMaintenance` |

### 1.2 问题量化（复杂度推导，非实测）

| 问题 | 机制 | 量级 |
|---|---|---|
| **写放大随数据量线性增长** | 每次 checkpoint 把 *磁盘全部条目 + overlay* 合并重写为新文件，再 `ValidateAllEntries` 全量读回 | 顺序插入 N 条：总写入 ≈ N²/(2·100k)，写放大 ≈ N/200k（10M→约 50×，100M→约 500×）；更新型负载每 10 万次更新重写一次全量，10M 表约 100× |
| **写后快照读 O(M log M)** | 任意写入使快照缓存失效，下一位读者在门闩内 `ToArray()` + `Array.Sort` 整个 overlay（M ≤ 10 万，索引重建 ≤ 100 万） | M=10 万约 1.7M 次 key 比较，量级 10～20 ms/次，读者串行 |
| **冷打开 O(N)** | 没有持久索引，打开时逐条读 prefix/key/CRC | 10M 条约读 10M 次 prefix；fence 常驻 N/16 个 key 副本 |
| **有序扫描逐元素重新定位** | `KvOrderedOverlay.Scan` 每产出一个 key 都 `GetViewBetween(...).GetEnumerator()` | O(k log n) + k 次堆分配 |
| **单文件上限** | header `count` 为 `int` | 单 keyspace ≤ 2³¹−1 个条目 |
| **TTL 回收持写锁扫描** | 过期判断需要逐条访问 | 大 keyspace 下回收延迟与写入抢锁 |

## 2. 目标与非目标

### 目标

1. 写放大与总数据量**解耦**：10M 键随机写入的写放大 ≤ 15（leveled 理论值约 10～12）。
2. 快照获取 **O(1)**：只记录 sequence 与文件集合引用，不复制、不排序。
3. 冷打开时间与条目数无关：只读 manifest 与各 SST 尾部，索引块按需加载。
4. 范围扫描使用合并迭代器，每元素摊销 O(1)、无逐元素分配，支持正向与反向。
5. **保持现有全部公开语义**（第 3 节），现有 23 个 `tests/SonnetDB.Core.Tests/Kv/*` 测试文件在新引擎上不改断言通过。
6. Safe-only、零新增第三方运行时依赖、NativeAOT 0 警告。

### 非目标（本设计不做）

- 任意 keyspace 之间的通用事务。例外：同一数据库的关系表共享一个 LSM 实例后（§5.7），多表事务自然成为单 batch 原子提交；文档、图、KV 与表之间的跨模型事务仍不在范围内。
- 改变时序 `.SDBSEG`/`.SDBWAL` 格式。
- Redis wire protocol、KV SQL 语法。
- 分布式/复制。

## 3. 必须保持的合同

| 合同 | 现状来源 | 新引擎实现方式 |
|---|---|---|
| 每次写返回单调递增版本；batch 共享一个 commit 版本 | `docs/kv-keyspace.md` | internal key 携带 sequence；batch 在 WAL 中一条记录，memtable 中同 sequence |
| 单 keyspace mixed put/delete 原子 batch，恢复时整批应用或忽略 | KV WAL v3 `MutationBatch` | WAL 格式不变 |
| NX/XX/CAS/GetAndSet/GetAndDelete/Increment 原子 | `docs/kv-atomic-contract.md` | 仍在 keyspace 写锁内读最新值后提交 |
| TTL：到期按不存在处理，绝对 UTC 100ns ticks；快照固定 `ReadTimestampUtc` | 同上 | value 元数据保存 `ExpiresAtUtcTicks`；compaction 过滤遵守快照时刻（6.4 节） |
| 快照租约与游标持有独立租约，释放外层快照不影响游标 | `KvReadSnapshot` | 快照引用 `Version`（SST 集合）+ memtable 引用计数 |
| `Clear()` 推进 generation，WAL `ClearGeneration` 记录 | `KvKeyspace.Clear` | manifest 写入“空文件集 + 新 generation”的 VersionEdit；旧文件交给 cleanup |
| WAL 提交结果未知时 fail closed | `IsWriteCommitOutcomeUnknown` | 不变 |
| `KvDiskReadBudget` 跨 keyspace 共享并发读许可 | `docs/kv-keyspace.md` | SST 块读取复用同一预算 |
| `PageSize` / `MaxPageBytes` 硬上限、`MaxSnapshotOverlayEntries` 有界读 | `KvKeyspace.BoundedReads` | 合并迭代器按页产出；快照不再受 overlay 大小限制，保留选项但语义变为“不触发拒绝” |
| 背压：写入预算耗尽时有界等待后抛出可重试分类异常 | `KvAtomicBatchErrors` | 预算改为“immutable memtable 数 + L0 文件数”，异常类型与分类标记不变 |
| 公开 `CreateSnapshot()` / `Compact()` | `docs/kv-keyspace.md` | 见 7.3 节（**语义有调整，需 CHANGELOG**） |
| 后台 cleanup 尊重查询活跃/CPU/内存暂停规则 | `KvOptions.Cleanup*` | compaction 调度复用同一组暂停条件 |

## 4. 方案选择：LSM vs B+Tree 页存储

| 维度 | Leveled LSM（推荐） | B+Tree 页存储（LMDB/SQLite 式） |
|---|---|---|
| 与现有代码的契合 | 复用 WAL、不可变文件、CRC、密封/清理、v5 前缀压缩块编码 | 需要全新的页分配器、空闲页表、页级 WAL 或 COW |
| 写负载（二级索引、图边、文档索引维护） | 顺序写，写放大可控 | 随机页写；小 keyspace 尚可，大表写放大来自整页回写 |
| 崩溃一致性 | 文件不可变 + manifest 原子切换，实现面小 | 需处理撕裂页（double-write 或 COW） |
| 读放大 | 需布隆过滤器 + 块缓存补偿 | 天然低 |
| Safe-only 实现难度 | 低（`RandomAccess` + `Span`） | 中（mmap 写入在 safe 代码下需复制） |

结论：采用 **L0 分层（tiered）+ L1..Ln leveled** 的 LSM，与 RocksDB 默认结构同构，但只实现 SonnetDB 需要的子集。

## 5. 架构

```text
写入 ─► KV WAL v3（不变） ─► MutableMemTable（并发跳表）
                               │ 达到 WriteBufferSize
                               ▼
                         ImmutableMemTable[] ─► Flush ─► L0 SST（可重叠）
                                                          │ L0 文件数 ≥ 4
                                                          ▼
                                       L1（不重叠，64 MiB）─► L2（×10）─► … ─► Ln
读取 ─► Mutable → Immutable[] → L0（新→旧）→ L1..Ln（二分定位文件 → 布隆 → 索引块 → 数据块缓存）
元数据 ─► CURRENT ─► MANIFEST-<n>（VersionEdit 日志）
```

### 5.1 Internal key 与条目

```text
InternalKey = UserKey | Trailer(8B, BigEndian 以便按字节比较时 sequence 降序)
Trailer     = (~sequence << 8) | kind        // kind: 1=Put, 2=Delete
Value 元数据 = Version(=sequence, 隐含) | ExpiresAtUtcTicks(varint, 0=无) | ValueLength
```

- 同一 user key 多个版本按 sequence 降序排列；读者取第一个 `sequence ≤ snapshotSeq` 的版本。
- `Delete` 为墓碑；`IsDeleted` 语义与现有 `KvValueEntry.Deleted` 一致。
- 现有 `KvKeyComparer`（无符号字节序）保持为 user key 比较器。

### 5.2 MemTable：并发跳表

- 单写者（沿用 keyspace `_sync` 写锁，WAL 组提交顺序不变），多读者无锁。
- Safe-only 实现：节点存放在分块数组（arena，`int` 下标作指针），key/value 字节写入按 1 MiB 分块的 `byte[]` arena，`Volatile.Write` 发布 next 下标；避免每条目一个 `byte[]` + 节点对象的 GC 压力。
- `WriteBufferSize` 默认 16 MiB/keyspace，受全局 `KvWriteBufferManager`（默认 256 MiB/进程）约束：超出时优先 flush 最大的 memtable。这对“一张表一个 keyspace”的部署（成百上千个 keyspace）是必须的。
- 写满后切换为 immutable，并同时滚动 WAL（沿用现有 `Seal` 逻辑，每个 memtable 对应 WAL 段的 `[minSeq, maxSeq]`）。

#### 5.2.1 K3 实现说明

- `Kv/Lsm/KvMemTable.cs`：最大高度 12、晋升概率 1/4 的跳表。节点记录（40 B）、next 槽位和 key/value 字节分别存放在 4096 节点/16K 槽位/1 MiB 的分块数组中，≥256 KiB 的条目单独占一块。节点互指使用 `int` 下标（0 既是头节点也是空指针），没有 unsafe，每个条目也没有额外的托管对象。
- 发布顺序：写好节点记录、数据和自身 next 槽位后，才从第 0 层开始用 `Volatile.Write` 挂到前驱上；需要扩容的分块目录先发布再链接节点。读者全程用 `Volatile.Read`，不加锁。
- 同一 internal key 只能插入一次，重复插入抛出 `ArgumentException`。因此 K4 必须在写入前把 batch 内的重复 user key 规范化（每个 batch 共享一个 commit 版本）。
- 反向移动 `MovePrev` 通过 `FindLessThan` 重新从头查找，代价 O(log n)，与 LevelDB 相同。

### 5.3 SST 文件格式 `*.SDBKVSST` v1（K1 已实现）

实现：`src/SonnetDB.Core/Kv/Lsm/`（`KvSstWriter`、`KvSstReader`、`KvSstBlock`、`KvBloomFilter`、`KvSstFormat`、`KvInternalKey`、`KvLsmVarint`）。

```text
[DataBlock 0..k]   每块目标 16 KiB；LevelDB 式前缀压缩条目，restart 间隔 16；块尾 restart 偏移数组
[FilterBlock]      全文件 user key 布隆过滤器，10 bits/key、7 次探测（实测 1 万键假阳性 < 3% 门槛）
[IndexBlock]       每个数据块一条：该块最后一个 internal key + BlockHandle(varint offset, varint length)
[PropertiesBlock]  条目数、墓碑数、数据块数、min/max sequence、最早到期 ticks、generation、创建时间、
                   原始 key/value 字节数、最小/最大 internal key
[Footer]           固定 64 字节
```

- 块内条目：`varint shared | varint nonShared | varint valueLength | keyDelta | value`；restart 点 `shared` 必须为 0；块尾为 `restart(uint32 LE)* | restartCount(uint32 LE)`。与 v5 状态文件的定长前缀不同，改用 varint 以减小条目开销。
- 条目 value：`Put` 为 `varint expiresAtUtcTicks(0=无 TTL) | value`；`Delete` 为空，读取时带值即判损坏。
- 每个块：`payload | compressionType(1B) | CRC32(4B, LE)`，CRC 覆盖 payload 与类型字节。**CRC 采用与现有 KV 文件一致的 `System.IO.Hashing.Crc32`（IEEE）**，不另引入 CRC32C。v1 只接受 `None`，类型字节为日后 M46 二级压缩预留。
- 索引分隔 key 暂用“块最后一个 key”而非最短分隔串，结构简单、索引稍大；是否优化留到 K5 基准后决定。
- 文件布局严格连续：数据区 → 过滤器 → 索引 → 属性 → footer，读取方校验各段首尾相接、索引 handle 连续覆盖数据区、块数与属性一致，任何偏差即判损坏。
- Footer 为 unmanaged 结构，遵守 AGENTS 4.1，显式按 little-endian 读写，并以 `MemoryMarshal` 往返测试证明布局：

```csharp
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct KvSstFooter            // 64 字节
{
    public InlineBytes8 Magic;         //  0 "SDBKVSST"
    public int FormatVersion;          //  8 = 1
    public int FilterLength;           // 12
    public long FilterOffset;          // 16
    public long IndexOffset;           // 24
    public int IndexLength;            // 32
    public int PropertiesLength;       // 36
    public long PropertiesOffset;      // 40
    public long RangeDelOffset;        // 48  v1 必须为 0，K5 启用
    public int RangeDelLength;         // 56  v1 必须为 0，K5 启用
    public uint FooterCrc;             // 60  覆盖前 60 字节
}
```

- internal key trailer 是 key 字节的一部分，按大端编码；比较器先比 user key、再比 trailer，避免 user key 互为前缀时的错序。
- 写入：`<path>.tmp` → fsync → 改名（拒绝覆盖已有文件）→ 目录 fsync；未完成即释放时删除临时文件。
- 读取：打开时只读 footer、属性、索引、过滤器（4 次读取）；数据块用 `RandomAccess.Read` 按需读取，全部经过 `KvDiskReadBudget`；不使用 unsafe 或 mmap。所有损坏抛出带文件路径的 `InvalidDataException`。

### 5.4 Manifest 与版本（K2 已实现）

实现：`Kv/Lsm/KvVersionSet.cs`、`KvVersion.cs`、`KvVersionEdit.cs`、`KvManifestFile.cs`、`KvLsmFileMetadata.cs`。

```text
<root>/CURRENT                      "MANIFEST-<12 位文件号>
"
<root>/MANIFEST-<12 位文件号>        header(16B) | record*
<root>/sst/<12 位文件号>.SDBKVSST
```

- 文件号：manifest 与 SST 共用一个单调递增空间，由 `NewFileNumber()` 分配，同时登记为“进行中输出”，清理时不会被删除。
- `MANIFEST` header：`"SDBKVMAN" | version(int32 LE)=1 | CRC32(前 12 字节)`；记录：`length(uint32 LE) | CRC32(payload) | payload`，单条上限 64 MiB。
- `VersionEdit` 采用 varint tag 编码：1 Generation、2 LogNumber、3 LastSequence、4 NextFileNumber、5 DeletedFile(level, number)、6 AddedFile(level, number, size, smallest, largest, minSeq, maxSeq)。未知 tag、重复的计数器字段一律拒绝；新增字段时升级 manifest 版本。每个 manifest 的首条记录必须是包含全部四个计数器的完整快照。
- **提交点**：普通编辑以记录 fsync 完成为准；manifest 超过 `MaxManifestBytes`（默认 4 MiB）时，把新版本写成新的快照 manifest，fsync + 目录 fsync，再写 `CURRENT.tmp` → fsync → 改名 → 目录 fsync，以改名完成为提交点。改名一旦返回就绝不删除新 manifest。
- **fail closed**：写 manifest 或切换 CURRENT 期间的任何 I/O 失败都会让版本集进入 faulted，拒绝后续 `LogAndApply` 和 `NewFileNumber`，必须重新打开。只做了校验、还没发生 I/O 的失败（删除不存在的文件、文件号重复、L1+ 重叠、计数器回退、文件 sequence 超过 LastSequence、使用未分配文件号）抛 `ArgumentException`，状态和 manifest 字节都不变。
- **撕裂判定**：只有文件**最后一条**记录不完整或 CRC 不符时，才视为崩溃导致的未提交尾部并忽略；损坏记录后面还有字节时，说明已提交的历史被破坏，拒绝打开。
- **打开**：读 CURRENT（格式严格校验）→ 回放 manifest，计数器必须单调 → 核对每个被引用的 SST 存在且大小一致（缺失或大小不符即拒绝打开）→ 把当前状态重写成新的快照 manifest 并切换 CURRENT，同时截掉撕裂尾部、限制 manifest 体积 → 清理孤儿。
- **版本与引用计数**：`KvVersion` 不可变；L0 按文件号降序，L1 及以上按最小 key 排序且不能重叠。版本集自己持有当前版本的一个引用，快照和迭代器通过 `AcquireCurrent()`/`Release()` 持有；引用归零时从存活集合中移除。
- **清理**：`DeleteObsoleteFiles()` 全程持锁，删除以下文件：不被任何存活版本或进行中输出引用的 `.SDBKVSST`；非进行中输出的 `.SDBKVSST.tmp`；非当前的 `MANIFEST-*`；根目录下的 `CURRENT*.tmp`、`MANIFEST-*.tmp`。删除失败（例如 Windows 上文件仍被占用）的文件计入 `SkippedFiles`，留到下一轮。不认识的文件一律不动。K4 接入引擎时，再把它挂到现有 `CleanupPendingFiles` 的节流调度上。

### 5.5 读路径

- **点查**：memtable → immutables → L0（按文件号新到旧，先比 min/max key，再查布隆）→ 每层二分定位唯一文件 → 布隆 → 索引块 → 数据块。
- **块缓存**：进程级 `KvBlockCache`（分片 LRU，默认 128 MiB，按字节计费），缓存解码后的块字节数组；索引块与布隆块也计入同一预算，L0/L1 的索引和布隆可常驻。取代现在的 `WeakReference` 页缓存。
- **范围扫描**：`MergingIterator`（最小堆）合并所有来源，跳过被更高 sequence 覆盖或不可见的版本；块内顺序解码，块间 seek 一次。`KvRangeCursor` 的正向/反向、`AfterKey`、`EndExclusive`、页字节上限都在该迭代器上实现。
- 反向迭代需要块内 restart 点回退（与 LevelDB `Prev` 实现相同）。

#### 5.5.1 K3 实现说明（`Kv/Lsm/KvLsmIterators.cs`）

- `IKvInternalIterator`：memtable、SST 和合并结果共用的双向接口（`SeekToFirst/SeekToLast/Seek/SeekForPrev/MoveNext/MovePrev`）。`Value` 是去掉 TTL 前缀后的用户值，墓碑为空。SST 块迭代器的反向移动从前一个 restart 点重新解码，代价 O(restart 间隔)。
- `KvMergingIterator`：子迭代器按“越新越靠前”的顺序排列（活跃 memtable、不可变 memtable、L0 新→旧、L1…）。相同 internal key 只输出一次，取最新的数据源，覆盖 WAL 回放与已刷盘 SST 重叠的情况。方向切换时按 LevelDB 的方式重新定位其余子迭代器。子迭代器很少，选最小/最大值时线性扫描；改成堆留到 K5 基准之后再定。
- `KvSnapshotIterator`：与 LevelDB DBIter 等价，按 `(snapshotSequence, readTimestampUtcTicks)` 把 internal key 流还原成用户视图。每个 user key 只取快照可见的最新版本；墓碑和已到期的值会遮蔽更旧的版本。支持 `[lower, upper)` 范围和正反向切换。
- 已知性能边界（K5 解决）：还没有块缓存，迭代器每次跨块都要重新读盘并校验。差分测试每个种子要 3～5 s，主要耗在这里。

### 5.6 快照（解决 P0-2）

```text
AcquireReadSnapshot():
  lock(_sync) 约数百纳秒: seq = _lastSequence; mem = mutable.AddRef(); imm = immutables.AddRef(); ver = currentVersion.AddRef()
  return new KvReadSnapshot(seq, readTimestampUtc, mem, imm, ver)
```

不复制、不排序，没有门闩。`SnapshotOverlayCache`、`_snapshotOverlayBuildGate`、`MaxSnapshotOverlayEntries` 拒绝路径随旧引擎一同下线（选项保留但不再生效，文档注明）。

### 5.7 共享 LSM 实例与逻辑 keyspace（决策 D4）

#### 5.7.1 结构

```text
<db>/tables/lsm/                  ← 每个数据库一个 KvLsmStore（关系表专用）
  FORMAT  CURRENT  MANIFEST-<n>
  wal/active.SDBKVWAL  wal/<seq>.SDBKVWAL
  sst/<fileNo>.SDBKVSST
```

- `KvLsmStore`：一个 WAL、一组 memtable、一套 manifest/SST，内部承载多个**逻辑 keyspace**（RocksDB column family 的简化版：共享 memtable 和 SST，只按 key 前缀区分）。
- 物理 key = `StorageId(8B, 大端) | UserKey`。`StorageId` 单调分配，**永不复用**。
- `KvKeyspace` 公开类型保持不变；内部改为“绑定 (store, StorageId) 的句柄”。`TableStore` 用到的约 40 个 `_keyspace.*` 成员都在句柄上实现，自动加前缀和去前缀（与现有 `KvNamespace` 的前缀视图同一模式）。
- 每个 SST 的 properties 块记录所含 `StorageId` 区间；前缀布隆以 8 字节 `StorageId` 为提取器，按表点查和扫描时可跳过无关文件。
- 本设计首先用于关系表。文档集合、文档向量索引、图存储也是“一对象一 keyspace”，是否接入同一机制在 K9 之后单独评估，本设计不改变它们的布局（它们仍使用独立 LSM 实例）。

#### 5.7.2 逻辑 keyspace 元数据

保存在同一 store 的保留前缀 `StorageId = 0` 下（用户数据从 1 开始）：

| 键 | 值 |
|---|---|
| `0 \| "ks" \| StorageId` | 名称、状态（Active/Dropping）、Generation、创建 sequence |
| `0 \| "next-id"` | 下一个可分配 `StorageId` |
| `0 \| "maint" \| StorageId \| name` | 取代表目录下的 `TableStoreMaintenanceFile`（迁移完成标记、干净索引标记、统计） |

元数据与数据写在同一原子 batch 中，不再有“目录文件 + KV”两处状态需要对齐。

#### 5.7.3 由共享带来的语义变化

| 场景 | 现状 | 共享实例 |
|---|---|---|
| **多表事务** | `TableTransactionJournal`：同步 before-image → 各表 WAL 分别同步 → 同步完成标记；重启时撤销未完成事务 | **整个事务 = 一条 `MutationBatch` WAL 记录**，恢复时整批应用或整批忽略；Lsm 模式下不再写 before-image 日志，fsync 次数从 2+N 次降为 1 次 |
| fsync 合并 | 每张表一个 WAL，各自 fsync | 共享 WAL 组提交，多表并发写入合并 fsync |
| RENAME TABLE | 移动 rowstore 目录 | 只改 schema 中的名称，`StorageId` 不变，不移动数据 |
| TRUNCATE | `KvKeyspace.Clear()` 推进 generation | range tombstone `[id, id+1)` + 元数据 Generation+1，同一 batch |
| DROP TABLE | 删除目录 | range tombstone + 元数据标记 Dropping；compaction 回收后删除元数据 |
| CREATE TABLE AS | 临时目录写入后改名 | 在新 `StorageId` 下写入，提交时在同一 batch 中发布元数据；失败时 range tombstone |
| `KvKeyspace.LastSequence` | 本 keyspace 最后序列号 | 语义保持为“最后一次修改**本逻辑 keyspace** 的序列号”：内存中按 `StorageId` 维护，flush 时写入 VersionEdit，WAL 回放时更新 |
| 内存/文件数 | 每表一个 WAL + 状态文件 + 独立 overlay | 一个数据库一套；数千张表也只有一组 memtable、一个 WAL |

#### 5.7.4 稳定存储 ID 与 schema

- `TableSchema` 新增 `StorageId`，`TableSchemaCodec` 升版本；旧库迁移时分配。
- **前提核验（K6 前必须完成）**：旧版本二进制读到新 schema 版本时必须明确报错，而不是忽略未知字段；否则旧版本会把找不到 rowstore 的表当成空表打开。若现有 codec 不满足，迁移时同时升级 `tables.tblschema` 文件头版本并核对旧版本的拒绝行为。

#### 5.7.5 公平性风险与缓解

共享后，一张热表的写入会占满 memtable、产生 L0 背压，影响其他表：

- 背压阈值按整个 store 计算，但 flush 选择与 compaction 评分不区分表，热表数据自然下沉，不需要按表配额；
- 写锁持有时间只覆盖“分配 sequence + WAL 追加 + 跳表插入”，行级锁仍由 `TableRowLockManager` 负责，不扩大锁范围；
- 基准中新增“1 张热表 + 1000 张冷表”场景（§9.1），冷表读 P99 必须单列。

## 6. 写入、Flush 与 Compaction

### 6.1 写入

写锁内：分配 sequence → WAL append（v3 不变）→ 插入 mutable memtable → 发布 `_lastSequence`。原子条件操作（NX/XX/CAS/Increment）仍在同一写锁内读最新可见值后决策。

### 6.2 Flush

后台线程把最老的 immutable memtable 写成 L0 SST：写 `.tmp` → fsync → rename → 目录 fsync → 追加 `VersionEdit{AddFile, LogNumber}` → fsync manifest → 安装新 Version → 删除已覆盖的 sealed WAL。flush 只做顺序写，不触碰已有 SST。

### 6.3 Compaction

- 触发评分：`L0 文件数 / 4`，`Li 总字节 / 目标字节`；评分 > 1 的最高者优先。
- L1 目标 64 MiB，倍率 10；小 keyspace（< L1 目标）只在 L0 与 L1 之间工作，最多 1～2 个文件，避免大量表各自产生多层。
- 选择输入：L0→L1 取全部重叠 L0 + 重叠 L1；Li→Li+1 轮转选一个文件 + 下层重叠文件；输出按 64 MiB 切分。
- **全局调度**：进程级 `KvCompactionScheduler`（替代 `KvCheckpointScheduler`），并发度默认 `max(1, CPU/4)`，复用 `CleanupPauseWhenQueriesActive`、CPU/内存负载阈值；L0 文件数达到 `L0StopWritesTrigger`(20) 时写入背压，抛出现有可重试分类异常。

### 6.4 墓碑、TTL 与快照约束

- 墓碑只有在输出层为最底层、且没有任何存活快照 `seq < 墓碑 seq` 时才丢弃。
- 过期条目：当 `ExpiresAtUtc ≤ min(所有存活快照的 ReadTimestampUtc, now)` 且没有快照需要它时，compaction 把它转为墓碑或丢弃。这把 TTL 回收从“写锁内全表扫描”变成 compaction 的副产物；`CleanExpired` 公开 API 保留（显式触发 + 内存层扫描），后台 `KvExpirerWorker` 改为按 SST `PropertiesBlock.最早到期` 选择需要 TTL compaction 的文件。
- 多版本：同 key 旧版本若没有快照需要，在 compaction 中丢弃。

### 6.5 Range tombstone（决策 D3，K5 交付）

- 新 internal kind `3 = RangeDelete`；KV WAL 增加记录 `DeleteRange = 7`，WAL 格式升为 v4（v4 只出现在 Lsm 布局中，旧版本已被 §7.4 的降级拒绝挡住）。
- memtable 中 range tombstone 单独存放在有序列表中（数量少）；SST 中写入独立的 `RangeDelBlock`（已按 key 切分为互不重叠的片段，每片记录覆盖它的最大 sequence），footer 增加该块的偏移和长度（`KvSstFooter` 的 `Reserved` 字段改为该块的位置，或升 SST 格式到 v2；以 K1 定稿为准）。
- 读：点查时取覆盖该 key 的最大 tombstone sequence，低于它的版本不可见；迭代器合并时同步推进 tombstone 片段。
- compaction：被覆盖的条目直接丢弃；tombstone 在最底层且无更老快照时丢弃；文件完全被覆盖时整文件删除，不需要读取。
- API：
  - 新增 internal `DeleteRange(startInclusive, endExclusive)`，O(1) 写入，不返回删除条数；
  - 公开 `DeletePrefix(prefix, limit)` 保持现有语义（返回实际删除数，可带上限），仍逐键删除；
  - 公开 `Clear()`：Lsm 下写 range tombstone 并推进 generation；`KvClearResult.RemovedKeys` 为保持合同，先做一次只读 key 的计数扫描（读成本 O(N)，无写放大），表 TRUNCATE/DROP 走不计数的内部快速路径。

#### 6.5.1 K5b 实现说明（与上文设计的差异以本节为准）

- **WAL**：没有把 WAL 文件头升到 v4。原因是 legacy `KvKeyspace` 与 LSM 共用 `KvWalFile`，升级文件头会改变 legacy WAL 的写出格式。改为在 v3 文件里新增记录类型 `7 = MutationBatchWithRanges`：每条变更带一个操作字节（1=put、2=delete、3=范围删除，范围删除以 key 存起点、value 存终点），范围删除与点写入在同一条记录里原子提交。只有含范围删除的批次才写类型 7，K4a 产生的 WAL 字节不变。`KvWalFile` 遇到未知类型会当作撕裂尾部截断，所以解析端已同步放行类型 7。旧版本二进制本来就无法打开 LSM 目录（§7.4.4）。
- **语义**：tombstone 只遮蔽 sequence **严格小于**自身的条目。同一批次里范围删除先生效、点写入与它共享 sequence，所以同批点写入不受影响，可用于“TRUNCATE + 元数据更新”同批提交。范围必须有界（`end` 非空且大于 `start`，`start` 可以为空）；整库清空走 generation。
- **SST**：只含点条目的文件仍写 v1，字节与 K1 相同。含 tombstone 的文件写 v2：在属性块之后增加 `RangeDelBlock`（`varint count | (varint len | start | varint len | end | varint seq)*`，存原始 tombstone，按起点升序），属性块升到 v2 并增加 `RangeDeletionCount`。允许只含 tombstone、没有任何点条目的文件，此时数据块数为 0、索引块为空块。
- **文件边界**：tombstone 起点在文件边界中记为 `(start, MaxSequence, Put)`（trailer 为 1），终点哨兵记为 `(end, MaxSequence, SeekMarker)`（trailer 为 0）。终点哨兵小于 user key 为 `end` 的任何条目，因此相邻文件以 `end` 为界时，按 internal key 严格不重叠。相应地，L1+ 点查改为用 `EncodeSeekKey(key, snapshot)` 按 internal key 二分定位文件。
- **读**：点查从新到旧遍历数据源，每个数据源先累计覆盖该 key 的 tombstone 最大 sequence，第一个找到的点版本早于它就视为已删除。迭代器创建时收集快照可见的全部 tombstone，构建分段视图（`KvRangeTombstoneSet`，O(log 片段数) 查询），被覆盖的条目按删除处理。
- **compaction**：输入中被更新 tombstone 覆盖的点条目直接丢弃。输出层之下已无重叠文件时，tombstone 也丢弃；否则在输出切分点处裁剪成片段，写入对应输出文件；只有 tombstone、没有点条目时单独输出一个文件。
- **Clear**：与后台 flush/compaction 互斥。先创建新 WAL，再在同一次 VersionEdit 中提交 `Generation+1`、`LogNumber = 新 WAL`、`LastSequence`，并删除全部 SST；随后丢弃全部 memtable，删除旧 WAL。已有快照仍读取固定的旧状态。
- **已知边界**：分段视图在构建时为 O(边界数 × tombstone 数)，适合 TRUNCATE/DROP 这类低频大范围删除；大量细碎范围删除需要先改成扫描线算法。“文件完全被覆盖时整文件删除”尚未实现，目前仍通过合并来回收。

### 6.6 大 value（第二阶段）

value > 64 KiB（默认上限 16 MiB）时，第二阶段引入 value 分离（blob 文件 + 指针），避免 compaction 反复搬运大 value。第一阶段先在 SST 中原样存放，并在基准中单列大 value 场景。

### 6.7 K4a 实现说明（`Kv/Lsm/KvLsmEngine.cs`）

- **拆分原因**：`KvKeyspace` 中约 200 处直接操作 overlay、磁盘状态和 WAL，快照（`KvReadSnapshot`）、游标、命名空间和有界读取也都建立在 overlay 结构上。所以 K4 拆成两步：K4a 先做可以独立测试的引擎，K4b 再接到 `KvKeyspace` 上。拆分不改变产品行为和 D1～D4 决策。
- **目录**：`CURRENT`、`MANIFEST-*`、`sst/`、`wal/<文件号>.SDBKVWAL`。WAL 直接复用 KV WAL v3（每个 memtable 一个文件，每个批次一条 `MutationBatch` 记录），引擎不会写 v3 以外的记录类型；重放时遇到 Clear/DeleteBatch 等 legacy 记录会拒绝。
- **写入**：在写锁内依次完成规范化（同 key 取最后一次）、WAL 追加、可选 fsync、memtable 插入、发布 LastSequence。参数校验失败不进入 faulted；WAL 追加或插入失败则提交结果未知，引擎进入 faulted。活跃 memtable 实际占用（`KvMemTable.UsedBytes`，不含预分配的分块）达到 `WriteBufferBytes`（默认 16 MiB）时轮转：先建新 WAL 并对目录 fsync，再关闭（fsync）旧 WAL。
- **flush**：按从旧到新的顺序把不可变 memtable 写成 L0 SST，以 `LogNumber = 剩余最旧 memtable 的 WAL 号` 提交 manifest，然后在同一次读状态切换里“移除 memtable、加入 SST”，最后删除旧 WAL 并清理孤儿文件。后台 flush 失败时引擎进入 faulted（WAL 完好），需要重新打开。
- **快照**：先读 LastSequence 再读写时复制的读状态（memtable 集合 + 版本引用），在 `_stateLock` 内以 O(1) 完成；迭代器另外持有一份版本引用。
- **背压**：不可变 memtable 数达到 `MaxImmutableMemTables`（默认 2）时，写入最多等待 `WriteBackpressureTimeout`，超时抛出与现有 KV 相同分类的可重试 `IOException`。
- **恢复**：打开时重放 `LogNumber` 之后的全部 WAL（跳过 sequence ≤ manifest LastSequence 的记录，并截断撕裂尾部），恢复的数据直接写成一个 L0 SST；先创建新 WAL，再在同一次 VersionEdit 中提交新 LogNumber 与该 SST，最后删除旧 WAL。三个崩溃注入点（WAL 追加后、SST 已写但 manifest 未提交、manifest 已提交但旧 WAL 未删）重开后都恰好只有一个 L0，数据既不丢失也不重复。
- **已知边界**：
  - （K5a 已解决）当时没有 compaction，L0 会无限累积，随机负载测试单个约 6～10 s；K5a 之后同一测试约 1 s。
  - 写入是逐条串行 fsync，没有跨线程组提交。
  - 还没有 range tombstone（K5）。

### 6.8 K5a 实现说明（`KvCompaction.cs`、`KvBlockCache.cs`、`KvLevelIterator.cs`）

- **回收规则简化**：快照持有自己的版本引用，被替换的输入文件要等快照释放后才删除，已有快照读到的仍是旧文件。因此 compaction 不需要像 LevelDB 那样按“最小存活快照序号”保留旧版本：同一 user key 只保留最新版本；最新版本是墓碑、或带 TTL 且已到期时，如果输出层之下已没有该 key 就整体丢弃，否则写为墓碑，继续遮蔽下层的旧版本。
- **TTL 时钟前提**：compaction 用 `UtcNowTicks`（默认当前 UTC）判断到期。之后获取的快照，读取时刻不能早于这个时钟，否则已被物理回收的到期条目对它不可见。`KvKeyspace` 总是用当前时刻创建快照，满足这个前提；测试中使用过去时刻的快照时，需要把时钟固定。
- **选择**：L0 文件数 / `Level0CompactionTrigger`（默认 4）与 L1～L5 的“实际字节 / 目标字节”比较，取最高且 ≥1 的一层。L0 一次取全部文件，再加上 L1 中与之重叠的文件，保证留在 L0 的文件一定比 L1 新。L1+ 按轮转指针选一个文件；下层没有重叠时只做 trivial move（只在 manifest 里改层号，不重写数据）。
- **输出**：按 `TargetFileSizeBytes` 切分输出，但只在 user key 边界切。这样 L1+ 按 user key 严格不重叠，点查在每层二分到唯一文件即可。
- **背压**：L0 文件数达到 `Level0StopWritesTrigger`（默认 20）时写入等待，与不可变 memtable 上限共用同一个超时和可重试异常分类。
- **调度**：每个引擎同一时间只运行一个 compaction，flush 完成后触发；compaction 失败时引擎进入 faulted。（K5a 时每个引擎一条后台线程；K5c 起改为进程共享调度器，见 6.9。）
- **块缓存**：16 分片 LRU，按块字节计费，缓存键为 `(读取器唯一 ID, 块偏移)`，可在多个引擎间共享；未指定时每个引擎创建一个私有的 32 MiB 缓存。索引块和过滤器块常驻在读取器里，暂不计入缓存。
- **读路径**：L1+ 每层只建一个 `KvLevelIterator`，按需打开文件，合并迭代器的子迭代器数量与层数相关，而不是与文件数相关。
- **崩溃注入**：在“输出已写、manifest 未提交”和“manifest 已提交、输入未清理”两个点注入崩溃，重开后数据完整，磁盘上只剩版本引用的 SST。

### 6.9 K5c 实现说明（`KvLsmBackgroundScheduler.cs`、`KvWriteBufferManager.cs`）

- **后台调度**：不再为每个引擎启动 flush 与 compaction 两条专用线程。进程共享的 `KvLsmBackgroundScheduler` 有两条独立通道：flush 通道 `max(2, CPU/4)` 线程，compaction 通道 `max(1, CPU/4)` 线程；compaction 再长也不会阻塞 memtable 落盘。同一引擎在每条通道内最多排队一次；一次 compaction 作业最多执行 4 个 compaction 后重新排队，让其他引擎轮到执行。
- **关闭**：引擎关闭时先停止接收新作业，再等待本引擎正在执行的作业观察到取消后退出（上限为 `ShutdownTimeout`）；已排队的作业出队后发现引擎已关闭，直接返回。
- **写缓冲预算**：进程共享的 `KvWriteBufferManager`（默认 256 MiB）。引擎在 memtable 占用（按 arena 分块粒度）或刷盘请求状态变化时上报；超限时，扣除正在刷盘的不可变 memtable 与已有待处理请求的活跃 memtable 后仍超限，才请求活跃 memtable 最大且不小于 64 KiB 的引擎轮转刷盘。请求只设置标志并排队 flush 作业，不获取其他引擎的锁；同步模式下由下一次写入兑现。
- **块缓存**：`KvKeyspace` 的 LSM 引擎改用进程共享的 64 MiB 块缓存（每分片 4 MiB）。独立使用 `KvLsmEngine` 且未指定缓存时，仍创建私有缓存。
- **与设计差异**：`CleanupPauseWhenQueriesActive`、CPU/内存负载阈值尚未接入 compaction 调度（仍只作用于 legacy 清理任务）；预算是软上限，不对写入做全局停顿。

## 7. 恢复、兼容与公开 API

### 7.1 恢复顺序

1. 取得 `keyspace.lock` 生命周期租约（不变）。
2. 读 `CURRENT` → 回放 `MANIFEST`，得到文件集合、`LogNumber`、`LastSequence`、`Generation`；校验 generation 与 `KvGenerationFile` 一致。
3. 打开各 SST：只读 footer（64 B），properties/index/filter 按需读取。
4. 回放 `LogNumber` 之后的 sealed WAL 与 active WAL 到新 memtable（遇到截断/CRC 失败停在最后合法记录，规则不变）。
5. 删除 manifest 未引用的 `.SDBKVSST`/`.tmp`（孤儿文件）。

### 7.2 故障注入矩阵（必须全部有回归测试）

| 故障点 | 期望 |
|---|---|
| flush 写 SST 中途崩溃 | `.tmp` 被清理；数据从 WAL 回放；无丢失、无重复 |
| SST rename 后、manifest 追加前崩溃 | SST 为孤儿被删除；WAL 仍在；无丢失 |
| manifest 追加后、WAL 删除前崩溃 | WAL 回放与 SST 重叠部分按 sequence 去重（相同 sequence 视为同一版本） |
| compaction 输出完成、manifest 未提交 | 输出为孤儿；输入文件仍有效 |
| CURRENT 切换中途崩溃 | 旧 CURRENT 仍指向旧 manifest，旧 manifest 完整 |
| manifest 尾部撕裂 | 回放到最后一条合法 edit；后续由 WAL 补齐 |
| 数据块 CRC 错误 | 读取抛 `InvalidDataException`，不吞异常（AGENTS 5.1）；诊断标记损坏文件 |

现有 `KvCheckpointSafetyTests`、`KvWalRecoveryChainTests`、`KvWalGenerationRecoveryTests` 的测试钩子（`CheckpointTestHook` 各阶段）映射到上表各故障点。

### 7.3 公开 API 语义调整（需 CHANGELOG 记录）

| API | 现状 | 新引擎 |
|---|---|---|
| `CreateSnapshot()` | 写出完整快照文件并截断 WAL | flush 全部 memtable 并提交 manifest，形成一致恢复点；返回 sequence。**不再写单一完整文件** |
| `Compact()` | 写出不可变段文件并截断 WAL | flush + 手动全范围 compaction 到最底层 |
| 备份 | `BackupService` 先 checkpoint 再复制目录 | pin 一个 `Version`，复制 `CURRENT`/`MANIFEST`/引用的 SST/未持久化 WAL；能硬链接时用硬链接 |

### 7.4 格式迁移：打开时自动迁移（决策 D2，AGENTS 4.1）

#### 7.4.1 选项与触发

- 新布局目录写入 `FORMAT`（内容 `lsm-v1` + CRC）；没有该标记的目录视为 legacy（state v1～v5）。
- `KvOptions.StorageEngine = Legacy | Lsm`，`KvOptions.LegacyMigration = Auto | Disabled`（默认 `Auto`）。
- 有效引擎为 `Lsm` 且发现 legacy 布局时，**在打开过程中自动迁移**。K8 之前只有显式选择 `Lsm` 的数据库会触发；K8 切换默认值后，升级到新版本的数据库首次打开即迁移。
- 迁移在任何业务读写开放之前完成；`Disabled` 时以 legacy 引擎打开并输出诊断，作为运维逃生口。

#### 7.4.2 单个独立 keyspace 的迁移步骤（文档、图、KV 等）

| 步 | 动作 | 持久化点 |
|---|---|---|
| 0 | 前置检查：可用磁盘 ≥ legacy state 大小 × 1.2 + 64 MiB；不满足时**不迁移**，按 legacy 打开并报告诊断，不阻止数据库启动 | — |
| 1 | 按 legacy 方式打开，回放 WAL，执行一次 legacy checkpoint，使全部数据落在单一 state 文件 | legacy checkpoint |
| 2 | 写迁移日志 `migration.json`（源 state 路径、sequence、generation、源文件 SHA-256、状态 `planned`），fsync | 日志 |
| 3 | legacy state 本身按 key 有序：流式切成最底层（L6）SST，一次顺序读 + 一次顺序写；每个输出 fsync | SST |
| 4 | 写 MANIFEST + CURRENT（`LastSequence` = 源 sequence，`Generation` 不变）；`KvGenerationFile` 升到 v3（旧二进制对 >v2 抛 `InvalidDataException`，已核对 `KvGenerationFile.LoadMetadata`）；写 `FORMAT`；日志状态 `committed` | 布局切换点 |
| 5 | 把 legacy state 与已截断的 WAL 移入 `legacy-<sequence>/`，日志状态 `retired` | — |
| 6 | 后续正常打开成功 N 次（默认 1）且超过保留期（默认 7 天）后，由 cleanup 删除 `legacy-*` | — |

崩溃恢复：打开时若存在 `migration.json` 且未到 `committed`，删除已产出的 SST/manifest，回到步骤 1 重新开始（legacy 文件一直未改动）；已 `committed` 但未 `retired`，补做步骤 5。迁移结果与源数据做抽样对拍（每个 SST 随机 1024 键 + 全量条目数 + 键序校验），不一致时回到 legacy 并报错。

#### 7.4.3 关系表并入共享实例的迁移

1. 按 7.4.2 步骤 0～1 处理每张表的 legacy keyspace，并且所有表的多表事务日志必须先完成（`TableTransactionJournal` 未完成事务先按现有规则撤销）。
2. 为每张表分配 `StorageId`，在 `migration.json` 中记录“表名 → StorageId → 源目录 SHA”。
3. 逐表流式读取 legacy state，加前缀写入共享 store 的 SST。表之间按 `StorageId` 升序，输出天然有序，仍是一次顺序写。
4. 在同一个 VersionEdit 中提交全部 SST 和 `0|ks|*` 元数据，然后写入带 `StorageId` 的新 schema 文件（tmp + rename），最后写 `FORMAT`。**新 schema 文件落盘是布局切换点**。
5. 旧 `rowstore/<table>/` 目录整体移入 `rowstore.legacy-<sequence>/`。
6. 任何一步失败：在切换点之前，删除共享 store 目录，旧 schema 与 rowstore 不受影响。

#### 7.4.4 回滚与降级

- 新版本内回滚：提供回滚命令（拟定为 `sndb kv rollback-migration`，名称在 K7 定稿），在保留期内把 `legacy-*` 移回并删除 Lsm 文件。只有迁移后**没有新写入**（`LastSequence` 等于迁移时的 sequence）时允许执行，否则拒绝并提示先导出数据。
- 旧版本打开：`KvGenerationFile` v3 与新 schema 版本让旧二进制明确报错，不会把数据当作空库打开。CHANGELOG 必须写明“升级后首次打开会自动迁移；降级前需先导出或在保留期内回滚”。
- 备份：迁移前若已配置自动备份，打开流程不额外做全量备份（避免启动时间翻倍）；该决定写入发布说明，由运维决定是否先手动备份。

#### 7.4.5 启动时间

迁移耗时与数据量线性相关（顺序读写，估计受磁盘带宽约束）。打开流程按 keyspace 输出进度日志与 `SonnetDbMeter` 指标；Server 健康检查在迁移期间返回 `starting` 而不是失败。基准（§9.1 `migrate-open`）给出每 GiB 耗时。

### 7.5 K4b 实现说明（`KvKeyspace` 接线，与上文设计的差异以本节为准）

K4b 把 `KvKeyspace` 的全部公开与内部入口接到单个 keyspace 独占的 `KvLsmEngine`（`KvLsmKeyspaceBackend` + `KvKeyspace.Lsm.cs`）。默认引擎仍是 legacy，K4b 不改变任何未显式选择 `Lsm` 的数据库。

#### 7.5.1 引擎选择与布局

- `KvOptions.StorageEngine = Legacy | Lsm`，默认 `Legacy`。
- 打开规则：目录已有 `CURRENT` 时一律按 LSM 打开；选择 `Lsm` 且目录中没有 legacy 数据（`generation.meta`、`wal/active.SDBKVWAL`、`sealed-*` WAL、非空 `snapshots/` 或 `segments/`）时新建 LSM 布局；其余情况按 legacy 打开。
- K4b 阶段不迁移：已有 legacy 数据的目录即使选择 `Lsm` 也继续用 legacy 打开（2026-11-09 用户确认）。K7 起按 `LegacyMigration`（默认 `Auto`）在打开时迁移，见 §7.6；布局标记改为 v3 `generation.meta`（取代 §7.4.1 的 `FORMAT` 文件）。

#### 7.5.2 合同差异

| 行为 | legacy | LSM（K4b） |
|---|---|---|
| 读到已过期 key | 读取时惰性删除（会写 WAL） | **读取不写入**：已过期 key 视为不存在，由 `CleanExpired`、后台清理或 compaction 回收（2026-11-09 用户确认） |
| WAL 写入失败 | 批量、条件、CAS 等 fail-closed 路径置只读；`Put`/`Increment` 只抛出本次异常，不记录写故障 | **所有写入（含 Put/Increment）统一进入只读**：`IOException`（内层为原始故障），需重新打开；`IsWriteCommitOutcomeUnknown` 按故障对象引用判定（2026-11-09 用户确认） |
| `Clear()` | 删除状态文件并递增 generation | 引擎 `Clear` 消耗一个 sequence（`LastSequence + 1`，写入 VersionEdit），generation + 1，新 WAL 从下一个 sequence 开始；旧文件随过期文件清理立即删除，`GetCleanupStatus` 恒为 `(generation, 0, 0)` |
| `Count` | 含已过期未回收的 key | 相同合同；实现为零复制全量遍历（O(n)），K6 持久化计数后改为 O(1) |
| 候选页物理预算（`ScanCandidatePage`、对象列表） | 删除标记计入物理候选数，超预算拒绝 | 快照迭代器在引擎内跳过墓碑，墓碑**不计入**候选预算；跳过成本由 compaction 回收墓碑来限制 |
| `MaxSnapshotOverlayEntries` | 限制快照复制的 overlay 条目 | 只校验 > 0；LSM 快照 O(1)，不复制 overlay |
| `CreateSnapshot()` / `Compact()` | checkpoint / 段文件 | 见 §7.3：`Flush` / `CompactAll` |
| `ReadStablePrefixPages` | 持锁读取稳定视图 | 基于引擎快照，回调期间不阻塞写入 |
| 有界读取 | 磁盘读取前可按长度拒绝 | memtable 先检查长度再复制；冷 SST 只能在读入数据块后才知道 value 长度（块已读，value 不复制） |
| 超大批次 | 按 overlay/WAL 预算拒绝 | WAL 记录超过 `MaxWalBytes - HeaderSize`（索引重建作用域内用 `IndexRebuildMaxWalBytes`）返回 TooLarge；索引重建批次遇 TooLarge 二分重试 |
| 快照生命周期 | 磁盘租约保护在途读取 | 快照持有读取预算的 state 引用与 SST 读取器，keyspace 释放后仍可读完 |
| 打开 | 无待恢复数据时不改写文件 | 每次打开都会轮换 WAL、把重放内容刷入 L0 并滚动 MANIFEST（逻辑内容不变）；因此被拒绝的打开（例如主键迁移日志 generation 错配）不保证文件逐字节不变 |
| 诊断 | overlay/检查点计数 | overlay 条目映射为 memtable/不可变 memtable 条目数，`ActiveWalLength` 为活跃 LSM WAL 字节数，checkpoint 专属计数为 0 |
| 物理读取指标 | 每次行 payload 读取计一次 KV state 读取，并归属到 SQL `PhysicalReads` | 每次 SST 块读取（块缓存未命中，含打开文件时的元数据读取）计一次；粒度为块，同一块内多行只计一次 |
| `Clear` 与回收指标 | 记录 generation/clear 指标，旧文件由后台回收并记录回收指标 | 记录相同的 generation/clear 指标；旧文件立即删除，不产生待回收文件与回收任务指标 |

#### 7.5.3 读取预算通道与内存

- 用户读取（点查、扫描、快照、游标）占用 `KvDiskReadBudget` 的请求通道；flush/compaction 产出的新表在提交 manifest 前以**维护通道**打开并校验（footer、索引、过滤器），compaction 输入块也走维护通道。请求过载（队列满或等待超时）因此不会让后台维护失败、把 keyspace 置为只读。
- 引擎关闭时版本集释放自己持有的当前版本，没有被快照固定的 SST 读取器与 manifest 句柄立即关闭；关闭时仍存活的快照可以读完，释放后句柄随之关闭。表 RENAME、DDL 发布与备份恢复需要在关闭后立即移动目录（Windows 下打开的句柄会阻止目录改名）。
- SST 块读取一次物理读入 `payload | trailer` 并直接持有，不再二次复制；索引与过滤器常驻读取器，同样不复制。
- SST 写入时单条 ≥ 块大小的条目独占数据块（文件格式不变，只是块边界不同）：只读小条目的扫描（例如覆盖索引）不会因为相邻大行而读入整个大块，块缓存也不会被大块挤出小块。
- memtable 分块保持在 LOH 阈值以下：节点块 256 条（约 10KB）、next 槽位块 8KB、数据块从 4KB 按 2 倍增长到 64KB，超过 16KB 的条目独占精确大小的分块。空 memtable 固定占用约 18KB（原约 224KB，首次写入再加 1MB）。
- 单块大于块缓存分片容量（默认 32MB / 16 = 2MB）时不缓存。3MB 级大行在表恢复中约为原始行的 4 倍分配（主行扫描与索引核对各一次块读取加一次值复制），需 K7 的 span 读取或后续 value 分离解决。

#### 7.5.4 双引擎测试矩阵

- 测试程序集的模块初始化器读取环境变量 `SONNETDB_KV_ENGINE`（`Legacy`/`Lsm`），作为 `KvOptions.StorageEngine` 的默认值。只影响 Core 测试程序集：未设置时 Core 测试仍以 legacy 运行（K8 起产品默认是 LSM），CI 的 core/all 分片另跑一轮 LSM。
- 观察 legacy 内部状态的测试（检查点钩子与调度、overlay 预算、legacy WAL 路径、legacy 磁盘状态钩子、legacy 内存画像、删除标记物理预算）标记 `[Trait("KvEngine", "LegacyOnly")]`；其余测试两轮都运行。读取 WAL 记录的测试改用 `KvKeyspace.CurrentWalPath`，双引擎都校验“一次批量写只发布一条 WAL 记录”。
- 运行方式：

~~~powershell
# legacy 轮（默认）
dotnet test tests/SonnetDB.Core.Tests/SonnetDB.Core.Tests.csproj
# LSM 轮
$env:SONNETDB_KV_ENGINE = 'Lsm'
dotnet test tests/SonnetDB.Core.Tests/SonnetDB.Core.Tests.csproj --filter "KvEngine!=LegacyOnly"
~~~

### 7.6 K6～K8 实现说明（与上文设计的差异以本节为准）

#### 7.6.1 布局标记与降级防线

- LSM 目录不写 §7.4.1 的 `FORMAT` 文件，而是在 `generation.meta` 中写 v3 记录（魔数不变，含布局种类与 `StorageId`）。旧版本二进制只接受 v1/v2，读到 v3 即抛出 `InvalidDataException`，不会把 LSM 目录当成空 keyspace 打开。
- 独立 LSM 目录（`kind = Standalone`）在打开时补写标记；共享实例的附属目录（`kind = Shared`）在绑定时写入。按独立目录打开带共享标记的目录会被拒绝。

#### 7.6.2 共享实例（K6，`KvLsmStore`）

- 每个数据库的关系表共用 `tables/lsm/` 下的一个引擎；物理 key 为 8 字节大端 `StorageId` + 用户 key。`StorageId = 0` 是元数据：`ks|id`（名称、generation、创建 sequence）、`sq|id`（该逻辑 keyspace 最后一次提交的 sequence）、`next-id`。编号单调分配、永不复用。
- 逻辑 keyspace 的每次提交在同一批次中写入 `sq|id`，所以 `LastSequence` 只随本表的提交变化，重开与 WAL 重放后保持；`Clear` 在同一批次中写入覆盖本前缀的 range tombstone 与 generation + 1；`Drop` 写 range tombstone 并删除元数据。
- flush、compaction、WAL 同步、诊断计数与故障（只读）都作用于整个共享实例；`Count` 为本前缀的零复制遍历。
- **与设计差异**：没有给 `TableSchema` 增加 `StorageId`、也没有升级 schema codec。表目录（`tables/rowstore/...`）保留，作为附属目录保存租约、维护标记、统计等文件，以及指向编号的 v3 标记。因此 RENAME、CTAS 暂存目录改名、DDL 事务的目录切换都随目录携带编号而不移动数据，不需要改写这些 DDL 流程。前缀布隆与 SST 的 `StorageId` 区间属性未实现。

#### 7.6.3 关系表接入（K7）

- 选择 LSM 时新表进入共享实例；已有 legacy 数据的表按迁移规则处理；独立 LSM 布局的表（K4b 期间创建）保持独立。
- DROP 先删表目录、再删编号数据：崩溃时只留下孤立编号。管理器打开时先补完导入后中断的迁移，再回收所有没有被任何表目录标记引用的编号（DROP/CTAS 失败、DDL 事务切换后被替换的旧数据）。
- DDL 发布前与表存储一起关闭共享实例；DDL 事务的撤销副本、工作区切换与回滚都同时复制/移动 `tables/lsm`。
- **未做（仍按现状工作）**：多表事务仍使用 `TableTransactionJournal`（没有改为单批次）；`TableStoreMaintenanceFile` 仍在表目录；没有持久化行数。

#### 7.6.4 打开时迁移（K7，决策 D2，`KvLegacyMigration`）

- 条件：`StorageEngine = Lsm` 且 `LegacyMigration = Auto`（默认）。可用空间不足 legacy 大小 × 1.2 + 64 MiB 时跳过并继续以 legacy 打开。
- 独立 keyspace：在 `.lsm-migrating` 中把 legacy 全部可见条目按 key 顺序直接写成最底层 SST，**保留每条记录的版本与 TTL、LastSequence 与 generation**；写入 `switching` 标记后切换：legacy 文件移入 `legacy-<sequence>`，LSM 文件移入根目录（CURRENT 最后），写 v3 标记。`switching` 之前崩溃则丢弃暂存结果、legacy 不变；之后崩溃则下次打开补完。
- 关系表：legacy 条目分批导入共享实例的新编号（新提交 sequence，行版本随之改变），同步 WAL 后写 `lsm-migration.pending`，再移动 legacy 文件并写共享标记。日志之前崩溃，导入的编号作为孤立数据回收；之后崩溃则补完。
- 只移动 legacy KV 自身文件（generation、WAL、快照、段、清理清单），表与文档集合目录中的其他附属文件保持原位。
- 保留目录超过 `LegacyMigrationRetention`（默认 7 天）后在下一次打开时删除。回滚：`KvLegacyMigration.RollbackStandalone` / `RollbackShared`（内部 API），只在保留目录仍在且迁移后没有新写入时允许；命令行入口尚未提供。

#### 7.6.5 备份一致性（K7）

- 备份在 checkpoint 之后、复制文件期间，暂停数据库根目录下所有已打开 LSM 引擎的 flush 与 compaction（取得各引擎的 flush/compaction 门），复制期间不会产生或删除 SST/MANIFEST；WAL 仍可追加，复制到的撕裂尾部由恢复截断。
- 共享调度线程不会阻塞在被暂停引擎的门上：作业取不到门就登记重试并返回，持有者释放后重新排队，其他数据库的引擎不受影响。

#### 7.6.6 默认切换（K8，决策 D1）

- `KvOptions.StorageEngine` 默认改为 `Lsm`：新建数据库使用 LSM，已有 legacy 数据在首次打开时自动迁移。Server 配置新增 `Kv:StorageEngine`（默认 `Lsm`）与 `Kv:LegacyMigration`（默认 `Auto`），并在配置快照中显示。
- 降级：迁移后的目录带 v3 标记，旧版本二进制明确拒绝打开；降级前需在保留期内回滚或先导出数据。
- 用户确认“代码实现即可”：§9 的性能、容量与固定硬件门禁未执行（NOT_RUN），默认切换只基于本机功能回归。

## 8. 影响面

| 调用方 | 影响 |
|---|---|
| `TableStore` | 改用绑定 `StorageId` 的 keyspace 句柄；`EnableOrderedOverlayScans` 变为空操作；`TableStoreMaintenanceFile` 改存到 `0\|maint\|*` 元数据；打开时 `CountPrefix` 全量计数仍为 O(N)，在 K7 中改为持久化到元数据的行数计数 |
| `TableManager` | 打开一个共享 `KvLsmStore`；CREATE/DROP/RENAME/TRUNCATE/CREATE TABLE AS 按 §5.7.3 改写；`TableManager.DdlTransactions` 中移动 rowstore 目录的撤销工作区在 Lsm 模式下改为元数据 batch |
| `TableTransactionJournal` | Lsm 模式下不再使用（整个事务 = 一条原子 batch）；legacy 模式保留；迁移前必须先处理完未完成事务 |
| `TableManager.SchemaMigrationRecovery` / `TableSchemaMigrationJournal` | 撤销逻辑按表名打开目录，需改为按 `StorageId` 打开逻辑 keyspace |
| `TableSchema` / `TableSchemaCodec` | 新增 `StorageId`，codec 升版本（§5.7.4） |
| Graph（`GraphReadSession`、`GraphIndexRepair`、`GraphMaintenanceRunner`） | 快照获取变为 O(1)，邻接前缀扫描走合并迭代器 |
| Documents、ObjectStorage、Sequences、SemanticContent | 仅通过公开/内部 API 使用，无格式耦合 |
| `BackupService`、`DatabaseGenerationManager` | 需按 7.3 改为 pin Version 复制；generation 资源清理改为删除整个 manifest 文件集合 |
| `KvLargeKeyspace`（`SonnetDB.Data`）| 需回归 |

`KvStateFile` 在 `Kv/` 目录外无直接引用，格式耦合面小。

## 9. 基准与验收门禁

### 9.1 新增基准 `KvLsmCapacityBenchmark`

参数：`Engine ∈ {Legacy, Lsm}`、`N ∈ {1e5, 1e6, 1e7}`（本机），`1e8`（固定硬件）；key 16 B（随机/顺序两种分布），value 100 B / 1 KiB / 64 KiB；`SyncWalOnEveryWrite ∈ {true, false}`。

| 负载 | 说明 | 主要指标 |
|---|---|---|
| fillseq / fillrandom | 空库写入 N 条 | ops/s、**写放大 = 磁盘写入字节 / 用户字节**、P99 |
| overwrite | 对已有 N 条随机覆盖 N 次 | 写放大、空间放大、compaction 字节 |
| readrandom（冷/热） | 随机点查 | P50/P99、读放大（块读次数/查询）、块缓存命中率 |
| readwhilewriting | 1 写者 + 4 读者 | 读 P99、写吞吐 |
| **snapshot-after-write** | 每次写后 `AcquireReadSnapshot` + 点查（直接针对 P0-2） | 快照获取 P50/P99 |
| seekrandom + scan100 | 随机定位后正/反向扫 100 条 | P99、每元素分配字节 |
| ttl-churn | 50% 写入带 1～60 s TTL | 空间回收延迟、写 P99 |
| cold-open / kill-reopen | 填充后强杀并重开 | 打开耗时、WAL 回放耗时、RSS |
| **many-tables** | 1000 张表 × 1 万行（共享实例 vs 一表一 keyspace），1 张热表持续写 + 冷表随机读 | 文件数、RSS、冷表读 P99、热表写吞吐 |
| **multi-table-txn** | 2/5/10 表事务，`SyncWalOnEveryWrite=true` | 提交 P50/P99、每事务 fsync 次数（legacy 日志 vs 单 batch） |
| **truncate / drop** | 对 100 万行表执行 TRUNCATE/DROP，期间并发读其他表 | 命令耗时（应与行数无关）、空间回收耗时 |
| **migrate-open** | legacy 库（1e6/1e7 条、100 张表）首次以 Lsm 打开；含迁移中途强杀 | 每 GiB 迁移耗时、峰值额外磁盘、强杀后续跑结果 |

同时记录：版本 SHA、机器、CPU、磁盘、.NET 版本、RSS 峰值、GC 次数、Gen2 暂停。参照基准（可选，仅作机制对照）：RocksDB `db_bench` 同负载。

### 9.2 门禁（按对应负载独立验收）

10 万／100 万条有界本机基线、1000 冷表的缩减语料和迁移阶段真实进程强杀见 [2026-11-10 本机证据](../benchmarks/kv-lsm-capacity-20261110.md)。缩减规模、单轮测量及确定性阶段强杀不替代下表中的完整规模、随机 200 轮或固定硬件门禁。

| 门禁 | 阈值 | 层级 |
|---|---|---|
| G1 正确性 | 现有 23 个 KV 测试文件在 `Lsm` 下零修改通过；基于模型的随机差分测试（oracle = `SortedDictionary` + 快照副本）≥ 1e6 次操作零差异 | 单元测试 |
| G2 崩溃 | 7.2 矩阵全部有测试；随机强杀 ≥ 200 轮无丢失/复活/重复 | 本机 |
| G3 写放大 | fillrandom N=1e7 时 ≤ 15；Legacy 同负载数值并列记录 | 本机 |
| G4 快照 | snapshot-after-write P99 ≤ 50 µs，且与 N、memtable 大小无关 | 本机 |
| G5 点查 | readrandom 热缓存 P99 不比 Legacy 差超过 20%；冷缓存单次查询数据块读 ≤ 1 + L0 文件数 | 本机 |
| G6 冷打开 | N=1e7 打开（不含 WAL 回放）≤ 1 s | 本机 |
| G7 内存 | N=1e7 稳态 RSS ≤ 块缓存 + 写缓冲预算 + 64 MiB | 本机 |
| G8 AOT/格式 | NativeAOT 0 IL/AOT 警告；`dotnet format` PASS；旧版本打开 `lsm-v1` 被明确拒绝 | CI |
| G10 共享实例 | many-tables 场景下冷表读 P99 不比“无热表”基线差超过 2 倍；多表事务每次提交 fsync = 1 | 本机 |
| G11 range tombstone | TRUNCATE/DROP 耗时与行数无关（1 万行与 100 万行相差 < 2 倍）；compaction 后空间回收完成；差分测试覆盖 range delete 与快照交错 | 本机 |
| G12 自动迁移 | 迁移后全量对拍零差异；§7.4.2/7.4.3 每个持久化点强杀后重开结果正确；磁盘不足时按 legacy 打开；旧版本二进制打开迁移后的目录明确报错 | 本机 + 旧版本二进制 |
| G9 固定硬件 | N=1e8 与 168 h 混合负载 | 固定硬件（独立记录，不以本机替代；**不阻塞 K8**，见 D1） |

## 10. 分阶段实施（每阶段一个 PR，均带 CHANGELOG）

| 阶段 | 交付 | 可独立验证 |
|---|---|---|
| K1 ✅ 源码 | SST writer/reader、布隆、块编码、footer、CRC；round-trip 与损坏测试（2026-11-08：`Kv/Lsm` 44 项测试本机 PASS；未接入引擎，不改变任何现有行为） | 单元测试 |
| K2 ✅ 源码 | Manifest / VersionEdit / Version 引用计数 / 孤儿清理（2026-11-08：`Kv/Lsm` 共 70 项测试本机 PASS，含追加、滚动、打开重写三类流程共 9 个故障注入点；未接入引擎） | 故障注入测试 |
| K3 ✅ 源码 | 并发跳表 memtable + 基于 sequence 的快照 + `MergingIterator`（正/反向）（2026-11-08：`Kv/Lsm` 共 83 项测试本机 PASS，含 5 个数据源 × 4 个种子 × 60 次快照试验的差分对拍，以及 1 写 4 读的并发测试；未接入引擎） | 差分测试 |
| K4a ✅ 源码 | 独立 `KvLsmEngine`：WAL（复用 v3）、原子批量写、memtable 轮转、后台/同步 flush 到 L0、O(1) 快照、点查、双向范围迭代、背压、WAL 重放恢复（2026-11-08：引擎 15 项测试 + `Kv/Lsm` 共 98 项本机 PASS；尚未接入 `KvKeyspace`） | G4 前置 |
| K4b ✅ 源码 | `KvKeyspace` 接线：`StorageEngine` 选项，CAS/NX/XX/TTL/Increment/条件批/有界读/游标/命名空间/Clear/清理等全部 API 路由到引擎；双引擎测试矩阵（2026-11-10：`SONNETDB_KV_ENGINE=Lsm` 全量 Core 8206/8206、legacy 全量 8315/8315 本机 PASS；合同差异见 §7.5） | G1、G4 |
| K5a ✅ 源码 | Leveled compaction（L0 按文件数、L1+ 按字节评分、轮转指针、trivial move、L0 停写背压）、版本/墓碑/TTL 回收、手动全量 `CompactAll`、`KvBlockCache`、层内拼接迭代器（2026-11-08：compaction 10 项测试 + `Kv/Lsm` 共 108 项本机 PASS；随机负载测试单个由 6～10 s 降至约 1 s） | G3、G5 前置 |
| K5b ✅ 源码 | **range tombstone（WAL 记录类型 7、SST v2 `RangeDelBlock`、`DeleteRange`、引擎 `Clear`）**（2026-11-09：范围删除 11 项测试 + `Kv/Lsm` 共 119 项本机 PASS；尚未接入 `KvKeyspace`） | G11 前置 |
| K5c ✅ 源码 | 进程共享后台调度器（flush/compaction 两条通道）、`KvWriteBufferManager`、共享块缓存（2026-11-10：调度与预算 6 项测试本机 PASS，全量结果同 K4b；G7 内存基准 NOT_RUN；见 §6.9） | G7 |
| K6 ✅ 源码 | `KvLsmStore` 多逻辑 keyspace：`StorageId` 前缀、`0\|*` 元数据、按 keyspace 的 `LastSequence`/generation、v3 布局标记（2026-11-10：共享实例 5 项测试本机 PASS；`StorageId` 由表目录标记承载而非 schema codec，前缀布隆未做，见 §7.6.2） | G1（表测试全集双引擎）、G10 |
| K7 ✅ 源码（部分） | 关系表接入共享实例、DDL 事务携带共享目录、孤立编号回收；**打开时自动迁移**（独立 keyspace 保留版本、表并入共享实例，含崩溃补完与回滚 API）、v3 降级拒绝、备份期间暂停维护（2026-11-10：迁移 11 项测试本机 PASS；多表单批次、维护文件迁入元数据、持久化行数、回滚命令行未做，见 §7.6.3～7.6.5） | G2、G8、G12 |
| K8 ✅ 源码 | 默认 `Lsm`、Server `Kv:StorageEngine`/`Kv:LegacyMigration`、CI 增加 Core LSM 轮次（2026-11-10：用户确认以代码实现为准，性能与固定硬件门禁 NOT_RUN，见 §7.6.6） | 本机功能回归 |
| 后续 | value 分离、块压缩、文档/图存储是否并入共享实例（单独评估） | 独立立项 |

K1～K3 互不依赖格式决策，可先行；K6 依赖 K4 的引擎接线，K7 依赖 K5（TRUNCATE/DROP 需要 range tombstone）与 K6。

## 11. 剩余待办与风险

| 项 | 说明 | 在哪个阶段解决 |
|---|---|---|
| 布局降级拒绝 | K6 实际使用 v3 `generation.meta`，未升级 TableSchema codec；旧二进制拒绝标记的合同见 §7.6.1，真实旧版本消费者仍需单独验证 | G8/G12 |
| 自动迁移的启动时间 | 大库首次打开时间与数据量线性相关，需在发布说明中给出每 GiB 耗时 | K7 基准 |
| 共享 WAL 的单写锁 | 所有表写入共用一把 store 写锁；锁内只做 sequence 分配 + WAL 追加 + 跳表插入，需用 many-tables 基准确认它不是新瓶颈；若是，再考虑按 `StorageId` 分片 memtable | K6/K7 基准 |
| legacy 保留期占用磁盘 | 迁移后 7 天内同时保留新旧两份数据 | 发布说明；保留期可配置 |
| 文档/图是否并入共享实例 | 本设计只覆盖关系表（D4 的范围） | K8 之后单独设计 |
| Version 级备份 | K7 已在复制期间暂停 LSM flush/compaction（§7.6.5）；§7.3 规划的 pin Version、按引用文件复制与硬链接尚未实现。当前默认引擎是 LSM | 后续备份优化 |
| K7 表元数据与事务 | 多表事务仍用 TableTransactionJournal；维护文件仍在表目录；Count 仍为 O(n)，未持久化计数；回滚只有内部 API | K7 后续 |
| 共享 SST 与维护调度 | 前缀布隆、StorageId 区间属性，以及 compaction CPU/内存负载阈值尚未实现 | 独立改进 |
| 大行恢复分配 | 3MB 级大行在表恢复中约为原始行 4 倍分配（§7.5.3） | K7 span 读取或后续 value 分离 |
