---
title: SonnetDB 性能与可靠性文章怎么写：把数字和证据放在一起
categories: SonnetDB,性能,可靠性,基准测试
draft: false
---

数据库性能文章最容易把一次开发机结果写成产品承诺。SonnetDB 当前的正确写法是把嵌入式引擎、Server/HTTP、客户端和固定硬件分开，并且把版本、提交、配置、语料、持久性和原始样本一起保存。

## 三类结果

1. **功能证据**：测试证明语义正确，例如 Segment v6 旧段回读、WAL replay、MemTable snapshot 或窗口函数边界。
2. **本机性能观察**：在明确机器和命令下的吞吐、P95/P99、分配和 GC；只能描述该配置。
3. **发布门禁证据**：固定硬件、真实语料、跨架构、崩溃恢复、长稳和生产客户端；没有这些材料就写 `NOT_RUN` 或 `NOT_READY`。

旧文章中的 1.83M 点/秒、6.71ms、百倍过滤等数字，如果没有对应版本和原始报告，应该降级为历史样例或删除。嵌入式结果不能与 Server HTTP 路径混成一个数字，合成向量也不能替代 Recall@K 或真实模型质量。

## 可靠性故事

Segment v6 的 extension section 和 mini-footer 用于识别尾部损坏；Flush/恢复依靠 WAL 与 pending/committed marker；查询热路径复用 reader/index snapshot；MemTable 通过增量统计和快照降低并发读写成本。每个故事都应同时写出失败路径、旧格式兼容、取消/超时和仍未覆盖的资源上限。

## 可复现实验清单

文章发布前至少保存：commit、OS/CPU/内存、数据库配置、数据生成方式、写入/查询命令、是否 flush/fsync、客户端路径、原始 CSV/日志和失败样本。基准数字发生变化时，更新文章的 `sourceCommit` 和 `lastReviewedAt`，不要静默覆盖旧结论。

参考：[`performance-reliability-updates.md`](../performance-reliability-updates.md)、[`m43-release-evidence.md`](../m43-release-evidence.md)、[`m19-regex-bulk-delete.md`](../m19-regex-bulk-delete.md)、[`releases/4.0.0.md`](../releases/4.0.0.md)。
