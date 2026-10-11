---
title: SonnetDB 性能与可靠性文章怎么写：把数字和证据放在一起
categories: SonnetDB,性能,可靠性,基准测试
draft: false
---

数据库性能文章最容易把一次开发机结果写成产品承诺。SonnetDB 的性能记录需要把嵌入式引擎、Server/HTTP、客户端和固定硬件分开，并且把版本、提交、配置、语料、持久性和原始样本一起保存。本文介绍证据写法，没有在本次准备中重新执行基准、崩溃测试或发布门禁。

## 三类结果

1. **功能证据**：测试证明语义正确，例如 Segment v6 旧段回读、WAL replay、MemTable snapshot 或窗口函数边界。
2. **本机性能观察**：在明确机器和命令下的吞吐、P95/P99、分配和 GC；只能描述该配置。
3. **发布门禁证据**：固定硬件、真实语料、跨架构、崩溃恢复、长稳和生产客户端；没有这些材料就写 `NOT_RUN` 或 `NOT_READY`。

历史稿中的高吞吐、极低延迟或加速倍数，如果没有对应版本和完整原始报告，应删除结果数值，或明确保留为有证据引用的历史观察。嵌入式结果不能与 Server HTTP 路径混成一个数字。随机向量可以计算索引算法 Recall@K，但这种集合召回不能证明真实模型在业务语料上的语义质量。

## 可靠性故事

Segment v6 将派生索引内容放入 extension section，mini-footer 提供尾部损坏的诊断与受控 fallback 信息；它不代表任意损坏都能修复。WAL 与 checkpoint 约束恢复边界；compaction 段替换清单的 pending/committed 状态控制未提交目标和已替换源段是否加载，不能笼统称为所有 flush 共用同一标记流程。

查询热路径复用 reader/index snapshot，MemTable 通过增量统计和快照减少重复工作。这些实现变化可以解释优化方向，但速度和内存收益仍要由对应报告证明。每个故事还应写出失败路径、旧格式兼容、取消/超时与未覆盖的资源上限；单元测试通过不能升级为硬件掉电或长期稳定性结论。

## 可复现实验清单

文章发布前至少保存：commit、OS/CPU/内存、数据库配置、数据生成方式、写入/查询命令、是否 flush/fsync、客户端路径、原始 CSV/日志和失败样本。基准数字发生变化时，更新文章的 `sourceCommit` 和 `lastReviewedAt`，不要静默覆盖旧结论。

例如，下面的元数据模板明确记录实验尚未运行，不能在发布时仅把状态改成 PASS：

```json
{
  "sourceCommit": "<实际完整提交号>",
  "lastReviewedAt": "<实际复核时间>",
  "path": "embedded",
  "scenario": "range-query",
  "dataset": "<规模、series、字段和分布>",
  "hardware": "<OS、CPU、内存与磁盘>",
  "durability": "<WAL、flush与fsync配置>",
  "status": "NOT_RUN",
  "rawReports": [],
  "limitations": ["未执行，尚无延迟或吞吐结论"]
}
```

这是记录示例，不是 SonnetDB 发布 verifier 的输入 schema。实际执行要设置总超时、取消和任务归属，保存完整输出与失败，结束后只清理本实验启动的进程和临时目录。计量单位也要说明：行、点和字段值不能混用，客户端托管分配不能替代服务端进程 RSS。

M43 汇总工具会调用固定 verifier，模板中的显式延期得到 DEFERRED，并不代表现场已经执行；合成 bundle 和 mock 调度测试也不能当作生产证据。版本发布事实与门禁状态应分别引用。4.0.0 已有正式 GitHub Release，而仓库的候选发行说明仍含候选时期文字，不能据此声称正式版尚未发布，也不能反向推断所有延期现场门禁已经通过。

参考：[`performance-reliability-updates.md`](https://github.com/IoTSharp/SonnetDB/blob/main/docs/performance-reliability-updates.md)、[`m43-release-evidence.md`](https://github.com/IoTSharp/SonnetDB/blob/main/docs/m43-release-evidence.md)、[`m19-regex-bulk-delete.md`](https://github.com/IoTSharp/SonnetDB/blob/main/docs/m19-regex-bulk-delete.md)、[4.0.0 正式 Release](https://github.com/IoTSharp/SonnetDB/releases/tag/v4.0.0)、[候选时期发行说明](https://github.com/IoTSharp/SonnetDB/blob/main/docs/releases/4.0.0.md)。
