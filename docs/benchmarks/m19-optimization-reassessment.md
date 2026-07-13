---
layout: default
title: "M19 #124~#126.1 优化项复核"
description: "基于当前代码重新评估 SegmentManager、大量 measurement、正则查询和关系表大批量删除。"
permalink: /benchmarks/m19-optimization-reassessment/
---

## 复核结论

本次复核基于 2026-07-13 `main` 的实际代码和 Git 历史，不再把旧 ROADMAP 文案当成尚未实现的事实。

| 项目 | 当前覆盖 | 必要性 | 调整后的方向 |
| --- | --- | --- | --- |
| #124 SegmentManager | 核心增量索引已由 #207 落地，但一换一 compaction 会遗留旧索引缓存，且发布仍重复排序；此前没有专项基准 | 高，先修正确性和可测性 | 修复缓存清理，发布从 O(N log N) 降为 O(N)，补 add/swap/drop + 并发查询基准；更复杂分层索引须由数据证明 |
| #125 大量 measurement / 长稳 | #121 的 quick/ci/soak 已扩展四个正交专项 profile，覆盖百万 series、万级小段、维护并发与随机重启 | 已完成，避免与 #121 重复 | 默认容量档在目标硬件归档报告；开发/CI 用缩规模参数做功能预检 |
| #126 SQL 正则 | 已有 `REGEX` / `NOT REGEX`、250 ms timeout 和 ADO 回归 | 中高，属于契约和资源治理，不是单纯性能优化 | 先统一 matcher、模式长度、受限缓存、`regexp_like`、EXPLAIN 和 EF 翻译；二阶段函数和前缀剪枝后置 |
| #126.1 关系批量删除 | table 已建在 KV tombstone 语义上，也有同步 `Compact()`；SQL 仍物化全部 mutation 并逐行删除索引和主键 | 高，是当前最需要深入设计的一项 | 不再引入第二套 row tombstone；先做整表 generation/truncate 和 KV 批量原语，再设计可恢复的异步谓词删除任务 |

#124 与 #125 已按复核方向收口。剩余优先级仍是 `#126.1 设计与基准 -> #126 契约补齐`；`#126` 可独立并行，但不应以正则函数数量挤占批量删除的存储设计工作。

## #124：当前实现与本次收口

提交 `9c9cc6c` 已通过 `_indexById` 复用未变化段的 `SegmentIndex`，消除了每次 flush 对所有 block 重建索引、连续 N 次 flush 趋近 O(N^2) 的主要成本。因此，“重新实现增量索引”已经过期。

复核仍发现三处有效改进：

1. 一换一 `SwapSegments` 前后段数量相等，原清理条件 `_indexById.Count > ordered.Count` 不成立，旧索引会随 compaction 次数无界累积。
2. `_readerById` 为普通字典，每次 add/swap/drop 都执行 `OrderBy().ToList()`；索引构建已增量化，但发布仍为 O(N log N)。
3. 纯 MemTable 切换声称 O(1)，实际会重新排序并复制全部 reader state，segment 多时会把 flush 密封阶段重新拉回 O(N)。

本次实现改为：

- 删除段时同步删除对应 `_indexById`，并增加一换一重复 swap 回归；
- `_readerById` 改为 `SortedDictionary`，发布顺序复制为 O(N)，不再重复排序；
- 无命中的 `DropSegments` 不再发布等价快照；
- 纯 MemTable 发布复用已有 index、reader state 和 reader 列表，恢复为 O(1)；
- 新增 `SegmentManagerMaintenanceBenchmark`，参数覆盖 16/256/1024 段与 0/4 个真实 `QueryEngine` worker。

基准中的映射为：

| 操作 | 代表路径 |
| --- | --- |
| `AddSegment` | flush 完成后的新段发布 |
| `SwapSegments` | compaction source -> target 原子替换 |
| `DropSegments` | retention 整段淘汰 |
| `FullIndexRebuildReference` | #207 前对全部存活段重新执行 `SegmentIndex.Build` 的参考成本 |

运行命令：

```bash
dotnet run -c Release --project tests/SonnetDB.Benchmarks -- --filter *SegmentManagerMaintenance*
```

BenchmarkDotNet 输出维护线程的 Median、P90 和 managed allocation；`QueryWorkers=4` 使发布与真实点查询租约、block 解码和合并并发。该基准用于判断 O(N) 快照复制是否已成为实际瓶颈，不把一次开发机结果解释成服务端 SLA。

暂不继续引入树状/分层持久索引。当前发布仍需构造不可变的 O(N) 段列表，但读路径获得简单、连续、无锁的一致快照。只有 1024+ 段基准显示发布 P90 或分配超出目标预算时，才值得用更复杂的数据结构交换读路径复杂度。

## #125：已作为 #121 的专项扩展交付

`tests/SonnetDB.EcosystemSoak` 保留 quick/ci/soak 三档组合验收，并在同一个 runner 中交付四个正交专项 profile，没有新增第二套长稳框架：

1. `high-cardinality`：默认 1M series、每 series 1 点，测 catalog、tag index、采样点完整性、启动、内存和查询分位数。
2. `small-segments`：固定总点数，默认主动 flush 10k segment，做全量 series/time/value 摘要并测查询和恢复。
3. `maintenance-chaos`：后台 flush/compaction/retention 开启，以固定随机种子执行 20 轮真子进程 kill/reopen，按已确认序列统计缺失、重复、额外点和值差异。
4. `many-measurements`：默认 10k measurement，覆盖目录枚举、备份扫描、drop、retention、冷启动和查询。

统一 JSON/Markdown 报告现在记录阶段 working set/托管内存峰值、恢复与查询 nearest-rank P50/P95/P99、结构化完整性摘要，并按 profile 写明“能验证什么”和“不能证明什么”。`Ecosystem Soak` workflow 可手动选择四个专项档并归档证据。

容量边界保持明确：#124 只能降低内存索引发布成本，不能减少 segment 文件数量、解码成本或 compaction I/O；`maintenance-chaos` 的 `Process.Kill` 也不等价于内核崩溃或整机掉电。默认百万/万级容量档不进入普通 PR 主 CI，必须在固定规格目标硬件运行并保留报告，缩规模 PASS 不能替代发布容量结论。

## #126：已有半套能力，重点应转为治理

当前 parser 已接受 `expr REGEX pattern` 和 `expr NOT REGEX pattern`，关系表、measurement、document、join、hybrid 等执行器都调用 `RegexPatternMatcher`。matcher 使用 `Regex.IsMatch(..., timeout: 250ms)`，因此“从 LIKE 之后首次引入正则”已经过期。

仍缺少的契约是：

- `regexp_like(input, pattern[, flags])` 在 WHERE 和 SELECT 中的统一标量函数；
- `REGEXP` / `RLIKE` 常用别名及明确的大小写、culture、NULL 语义；
- 模式长度上限、输入长度预算、允许 flags 白名单和稳定错误码；
- 明确受容量限制的缓存，而不是依赖 `Regex` 静态 API 的进程级默认缓存行为；
- EF provider 对 `Regex.IsMatch` 的翻译；当前 provider 只翻译 string 的 StartsWith/EndsWith/Contains/ToLower/ToUpper；
- EXPLAIN 中明确 `scan filter: regex`，防止用户误认为已命中索引；
- 跨 table/measurement/document 的一致回归。目前显式 ADO 测试只有 `REGEX` 和 `NOT REGEX` 两项。

建议第一阶段只交付以上契约，不立即做 `regexp_substr/replace/instr`。`^literal` 前缀剪枝也应后置：只有对应模型存在可用的有序字符串索引，并能证明从正则提取前缀不会改变 culture/escape 语义时才有价值。

## #126.1：必要，但旧方案需要改写

当前关系表不是原地 row file：`TableStore` 建在 `KvKeyspace` 上，`KvKeyspace.Delete` 已写 delete record，并对磁盘驻留 key 保留内存 tombstone；`TableStore.Compact()` 也已调用 KV compact。因此再增加一套“rowstore tombstone 格式”会重复已有机制。

真正的问题位于上层批处理与可见性：

- 非主键 `DELETE` 先扫描并解码候选行，再把所有匹配项物化为 `List<TableRowMutation>`；
- `TableManager.ApplyTransaction` 会扩展级联、校验外键并准备回滚信息；
- 每行删除要逐个删除全部二级索引 key 和主键 key，每个 key 都进入 KV WAL/overlay；
- 整个过程同步占用调用线程和 table/manager 锁，大表请求会直接拉长 HTTP/Kestrel 请求；
- compact 入口存在但同步执行，没有可取消任务、资源节流、进度、待回收字节或重启恢复状态；
- 没有 `TRUNCATE TABLE`，整表清空只能走逐行 `DELETE`。

建议先完成存储设计再写大范围代码：

1. **P0 基线**：增加 3k 设备、10k/100k 行、0/2/5 二级索引下的 delete/truncate 基准，记录扫描、WAL bytes、锁持有、可见性延迟和回收耗时。
2. **P1 整表快路径**：`TRUNCATE TABLE` 使用 table/keyspace generation 原子切换，使新读立即看见空表；旧 generation 作为后台可回收对象。权限和审计由 Server 层显式保护。
3. **P2 KV 批量原语**：一次锁和受控 WAL batch 提交多 key tombstone，避免每行每索引重复进入完整调用链；仍保持事务、唯一索引和 FK 语义。
4. **P3 谓词删除任务**：把 scan、visibility publish、index cleanup 和 physical reclaim 分阶段写入 manifest。若不能一次原子发布全部主键的不可见集合，就不能对客户端宣称删除已经完成。
5. **P4 资源感知 worker**：按 CPU、IO、working set、活跃查询和业务窗口节流，公开 pending rows/bytes、rate、throttle reason、checkpoint 和 last error。

不建议先实现一个简单的 `Task.Run(() => DELETE...)`。它只会把阻塞从 Kestrel 线程搬到线程池，既没有立即可见性，也没有崩溃恢复和资源治理。

## #124 热路径扫描记录

扫描范围：`SegmentManager.cs`、`SegmentManagerSnapshot.cs`、`MultiSegmentIndex.cs`、`SegmentIndex.cs`、`QueryEngine.cs`。

- `IndexOf`/`Substring`/缺少 `StringComparison` 的 StartsWith/EndsWith/Contains：0/0/0/0
- `ToLower/ToUpper`、三段 Replace 链、`params`、char LINQ：0/0/0/0
- static Dictionary/FrozenDictionary：0/0；方法内 `new List` 21、`new Dictionary` 13
- Select/Where/Cast/Take/Aggregate 命中 4，其中 3 个是冷启动或返回值转换，1 个是 SIMD aggregate 调用而非 LINQ
- `HttpClient`、`JsonSerializerOptions`、Regex、`async void`、sync-over-async：全部 0
- 叶类密封比例：7/7，未密封 0

### 发现

#### Moderate 1. 一换一 swap 遗留段索引缓存
**Impact:** 长期 compaction 可让缓存按 swap 次数增长，而活动段数保持不变。
**Files:** `src/SonnetDB.Core/Engine/SegmentManager.cs`
**Fix:** 删除 reader 时同步删除同 ID index，并以缓存数等于活动段数做回归。

#### Moderate 2. 每次段发布重复排序和中间集合分配
**Impact:** 大量小段下 add/swap/drop 仍承担 O(N log N) 排序和多份短命集合。
**Files:** `src/SonnetDB.Core/Engine/SegmentManager.cs`
**Fix:** 活动 reader 改用 `SortedDictionary`，直接填充定长快照数组。

#### Moderate 3. 纯 MemTable 发布并非 O(1)
**Impact:** 每次 seal/release 都复制全部 segment reader 状态，segment 多时会放大 flush 前台暂停。
**Files:** `src/SonnetDB.Core/Engine/SegmentManager.cs`, `src/SonnetDB.Core/Engine/SegmentManagerSnapshot.cs`
**Fix:** 纯 MemTable 快照复用不可变的 index、reader state 和 reader 列表。

| Severity | Count | Top issue |
| --- | ---: | --- |
| Critical | 0 | 无 |
| Moderate | 3 | 一换一 swap 的索引缓存增长 |
| Info | 0 | 剩余 O(N) 不可变快照复制由基准决定是否继续优化 |

> 扫描结果由 AI 辅助生成，可能包含误判或遗漏。生产优化应以固定硬件上的基准、trace 和人工审查为准。
