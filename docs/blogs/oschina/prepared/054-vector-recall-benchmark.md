---
title: '向量召回率基准测试：Recall@10 评估与 HNSW 参数影响'
categories: SonnetDB,数据库
draft: false
---

正式发行见 [4.0.0 Release](https://github.com/IoTSharp/SonnetDB/releases/tag/v4.0.0)。本文按 2026-10-07 当前 main 文档与源码修订旧稿；主线改动不自动代表全部 v4 发行物与客户端的已验收能力。以下 SQL/API 为复现示例，本次没有实际执行或性能测量。

Recall@10 用来衡量近似搜索找回多少精确前十名。SonnetDB 的 HNSW 调优应把召回、延迟、内存和恢复放在同一份可复现报告中。

## 建立精确基准

固定数据集、查询集合、模型版本、维度和 metric。先用精确扫描保存每个查询的 Top-10 ID，再运行实际 ANN 路径。每个查询的召回率为 `|ANN_IDs ∩ Exact_IDs| / 10`，最后报告均值与分布。

如果过滤后的真值少于十条，分母必须采用明确的有效 k 约定；同距离并列也要固定 ID 排序或采用等价集合规则，避免把合法并列结果误算成丢失。

## 一次只改变一类参数

先固定 m 和 ef_construction，比较多个 ef 搜索参数；再考虑重建不同密度的图。每一组都记录构建时间、索引大小、真实查询路径、P50/P95/P99 延迟、返回数量和召回。不能比较一组热缓存 ANN 与另一组冷磁盘扫描后称为算法加速。

## 防止把回退当作 ANN 成绩

SonnetDB 在度量、过滤或索引状态不匹配时可能精确回退或补偿。结果召回达到 1 并不能独立证明图检索质量，需要同时保留路径证据。

随机三维数组适合解释公式，不能代替真实语义模型数据。本文是测量方法，没有执行或生成性能报告，也不把仓库中的 benchmark 定义写成实测 PASS。固定目标硬件上的质量、容量和恢复证据应另行取得。

依据：[vector-search.md](https://github.com/IoTSharp/SonnetDB/blob/main/docs/vector-search.md)。
