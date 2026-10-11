---
title: 向量基准通稿：Brute-force 与 HNSW 在时序数据库内的召回对比
categories: SonnetDB,向量,HNSW
draft: false
---

# 向量基准通稿：Brute-force 与 HNSW 在时序数据库内的召回对比

标题保留历史选题，但要先明确测量边界：仓库中的 `VectorRecallBenchmark` 直接测试 `HnswIndex<int>`，并不是 SQL measurement 或 HTTP 服务端的完整写入、索引和查询流程。本次未取得旧稿召回和耗时数字对应的完整原始报告，没有重新运行基准，因此不发布这些旧数字。

## 当前基准如何构造

源码生成 384 维、归一化的随机向量，使用固定随机种子 20260422 和 cosine 距离。默认规模为一万和十万向量，百万规模需要显式启用。HNSW 配置为 `M=16`、`EfConstruction=200`、`EfSearch=200`，准备阶段构图并计算精确 Top10。

每批包含十个查询，查询向量从语料本身抽取。这会引入自身匹配，对结果解释有影响；它不是独立留出的查询集，更不是工业文本、图片或用户意图的语义质量评估。

Brute-force 全扫描作为精确近邻基准，HNSW 给出近似近邻。它们可以检验索引算法在这组向量上的速度与集合重合程度，不能直接证明嵌入模型能够理解设备故障。

## Recall@10 和计时表是两种数据

Recall@10 按精确 Top10 与近似 Top10 的交集计算。例如下面是一个独立的手工数据示例：

```python
exact = {1, 2, 3, 4, 5, 6, 7, 8, 9, 10}
approx = {1, 2, 3, 4, 5, 6, 7, 20, 21, 22}
recall_at_10 = len(exact & approx) / 10
print(recall_at_10)  # 此人工例子为 0.7，不是 SonnetDB 实测值
```

示例未在本次准备中运行。正式评估还要保存每个查询的 ID、距离、精确答案和近似答案，解释重复 ID、并列距离和自身匹配如何处理。

`Hnsw_RecallAt10` 虽然返回召回率，BenchmarkDotNet 表中的 `Mean` 仍然是执行该方法的时间，不能把它抄成召回率。方法设置 `OperationsPerInvoke=10`，报告解释耗时时应核对每次调用和每次查询的归一化关系。实际召回值需要另外保存返回值或探针输出。

小规模单元测试的召回下界只证明对应种子、数据规模与参数的测试结果，不能外推为十万向量的保证。旧稿没有可复核的大规模输出时，应保留这个证据缺口。

## 可复现入口和下一层评估

```bash
dotnet run -c Release --project tests/SonnetDB.Benchmarks -- --filter '*Vector*'
```

运行应记录提交号、硬件、维度、metric、种子、图参数、构建时间、峰值内存、查询时间和原始召回输出，并设置总超时和取消方式。只回收本次创建的进程与临时目录。

若评估数据库工作流，还需补充 measurement/SQL 或 HTTP 的写入、索引持久化、重开、更新、删除和过滤场景。若评估真实语义，还要使用明确模型和真实任务集，测质量、延迟与成本。随机向量索引测试不能代替这两类证据。

参考：[向量召回基准源码](https://github.com/IoTSharp/SonnetDB/blob/main/tests/SonnetDB.Benchmarks/Benchmarks/VectorRecallBenchmark.cs)、[基准测试说明](https://github.com/IoTSharp/SonnetDB/blob/main/tests/SonnetDB.Benchmarks/README.md)。
