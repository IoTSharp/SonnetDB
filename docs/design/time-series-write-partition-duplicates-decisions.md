# 时序 P0-3～P0-5：实现前的语义决策草案

状态：**待用户决策，未批准实现**。本轮只记录设计入口和具体问题，没有修改时序写入、WAL、段格式、retention 或重复时间戳行为。KV-LSM 的代码与验证不能替代本草案的批准和验收。

## 当前合同与问题

2026-11-10 按当前源码复核：[Tsdb.WritePointLocked](../../src/SonnetDB.Core/Engine/Tsdb.cs) 仍逐字段调用 `AppendWritePoint`；[CompactionPlanner](../../src/SonnetDB.Core/Engine/Compaction/CompactionPlanner.cs) 按文件大小分 tier；[RetentionPolicy](../../src/SonnetDB.Core/Engine/Retention/RetentionPolicy.cs) 只有全局 TTL；[MemTableSeries.StableSorted](../../src/SonnetDB.Core/Memory/MemTableSeries.cs) 和 `SegmentCompactorTests.Execute_SameTimestamp_AllPointsPreserved` 保留重复时间戳。历史审查入口见[竞品审查 §2](../audits/competitive-code-review-20261108.md#2-时序模型主场对标-influxdb-3--tdengine--iotdb--questdb--timescaledb)。

这些现状有独立的兼容性含义：减少 WAL 记录会改变失败与重放的最小单元；时间分区与 TTL 会改变段发布和删除的范围；折叠重复点会改变 COUNT、SUM、MEAN 和 LAST 的结果。不能把任何一种新的默认行为当成普通性能优化直接合入。

## 待决事项

| 决策 | 具体问题 | 影响与候选边界 |
|---|---|---|
| T1 写入原子单元 | 新 WAL 以单个多字段 Point、一次调用的整个 batch，还是现有单字段作为最小原子恢复单元？ | Point 原子需要各字段同成同败；batch 原子需要明确大批次预算和失败结果；保留字段边界只能在编码／组提交上优化，不能宣称整点原子。旧 WAL 的既有部分提交事实不能被重放工具补造。 |
| T2 分区与保留范围 | 分区是否按 measurement 独立？首个可配置时间窗覆盖小时／天／周中的哪些值？TTL 是否允许 measurement 覆盖全局值？ | 必须先冻结时间边界、乱序／补录进入旧窗的规则、TTL 缩短／延长对已有数据的影响及旧段归属。草案没有选择默认窗，也不改变当前时间戳单位；应与 M50 精度合同协同。 |
| T3 重复点 | 保持现有 KEEP_ALL，还是允许 measurement 显式选择 KEEP_ALL／FIRST／LAST？如果选 LAST，由服务器持久提交序号还是客户端显式版本决定先后？ | 重复键应明确为 series＋field＋timestamp。跨字段合并、同批重复、迟到写入及重开后的顺序都须有统一规则。旧段缺少新规则所需的全局序号时，不能推断真实的“最后一次写入”。 |

T3 还需明确是否追溯处理历史重复点。仅对启用策略后的写入生效与重写历史段是不同的迁移合同；两者对聚合结果、备份恢复和回滚都有不同影响。本草案没有替用户选择覆盖规则。

## 设计与交付顺序

1. 用户确认 T1～T3 的具体语义；只把确认过的部分写成独立设计，未决项继续保持现状。
2. 冻结 schema／WAL／段元数据的新版本、旧端明确拒绝方式，以及旧目录迁移、备份与降级边界。分片写锁、字段编号与列式 MemTable 必须保持已选的提交及查询顺序合同。
3. 分别实现写入、分区／TTL、重复策略，避免在一次变更中同时改三个语义层。每项以正常、非法输入、取消、未知提交、强杀重放、乱序、多版本目录与恢复回归为门禁。
4. 只有新语义代码和恢复验证完成后，才运行有界吞吐／分配／空间放大基准。固定硬件与长稳另行记录，不由设计文档或单轮测量宣布通过。

## 本轮状态

设计入口与问题整理：PASS。时序实现、格式升级、迁移、性能、固定硬件和生产操作：NOT_RUN。此文件不授权提交、推送、发布或修改运行中的数据库。
