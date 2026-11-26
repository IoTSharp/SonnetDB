# 时序写入、分区和重复点合同

T1 的默认单 Point WAL 恢复已接入 Tsdb。各字段共享一条 WAL 记录和 LSN，批写入中的不同 Point 各自提交。参见[单 Point WAL v2 合同](time-series-atomic-point-wal-v2.md)和[代码阶段验收](../audits/capability-code-stage-20261112.md)。

## 当前合同与问题

时序默认使用 Unix 毫秒、KeepAll 和全局 TTL。调用方可通过 `TsdbOptions.DuplicateTimestampPolicy` 为打开的数据库指定 KeepFirst 或 KeepLast。该策略按 series、field、timestamp 作用于当前 MemTable 及其 WAL 回放。Flush 保存当前 MemTable 的结果，跨已落盘 Segment 的重复点仍可能存在。参见[策略实现](../../src/SonnetDB.Core/Engine/DuplicateTimestampPolicy.cs)与[回归用例](../../tests/SonnetDB.Core.Tests/Engine/DuplicateTimestampPolicyTests.cs)。

时间分区和 TTL 会改变段发布和删除范围。折叠重复点会改变 COUNT、SUM、MEAN 和 LAST 的结果。变更这些默认行为前需要明确查询、升级和恢复合同。

## 待决事项

| 决策 | 具体问题 | 影响与候选边界 |
|---|---|---|
| T1 批次扩展 | 上层是否需要多个 Point 同成同败？ | 当前合同的恢复单位为单个多字段 Point。整批原子性需要独立定义批次预算和失败结果。旧 WAL 保留历史部分提交结果。 |
| T2 分区与保留范围 | 分区是否按 measurement 独立？首个可配置时间窗覆盖小时／天／周中的哪些值？TTL 是否允许 measurement 覆盖全局值？ | 必须先冻结时间边界、乱序／补录进入旧窗的规则、TTL 缩短／延长对已有数据的影响及旧段归属。草案没有选择默认窗，也不改变当前时间戳单位；应与 M50 精度合同协同。 |
| T3 跨段重复点 | 是否允许 measurement 单独指定策略？如何确定跨段、迟到写入和重新打开后的 FIRST/LAST 顺序？ | 需要确定持久提交序号或显式版本的来源。旧段缺少全局序号，历史顺序的推断能力有限。 |

T3 的历史段改写与新写入策略需要分别定义，对聚合结果、备份恢复和回滚的影响各自验收。

## 设计与交付顺序

1. 确定 T2 的分区与 TTL 规则，以及 T3 的跨段顺序、配置范围和历史处理。
2. 冻结 schema／WAL／段元数据的新版本、旧端明确拒绝方式，以及旧目录迁移、备份与降级边界。分片写锁、字段编号与列式 MemTable 必须保持已选的提交及查询顺序合同。
3. 按独立交付实现分区/TTL 与跨段重复点。回归覆盖边界、非法输入、取消、提交未知、强杀重放、乱序、旧目录和恢复。
4. 只有新语义代码和恢复验证完成后，才运行有界吞吐／分配／空间放大基准。固定硬件与长稳另行记录，不由设计文档或单轮测量宣布通过。

## 验证记录

2026-11-12 至 2026-11-13 的确定性强杀覆盖独立 WAL v2 和公共 Tsdb 默认接入，命令和结果见[代码阶段审计](../audits/capability-code-stage-20261112.md)。重复点有定向回归覆盖默认与显式策略，并验证 Flush 后重开。T2、跨段和历史重写、固定硬件与生产验收仍待各自执行。
