# M42 直接表值函数累计物化准入

Core `SqlExecutionOptions.MaxMaterializedRows` / `MaxMaterializedBytes` 新增三个直接来源：measurement `knn(...)`、文档 `vector_search(...)` 和 JSON 文件 `json_each/json_table(...)`。
两项仍为 opt-in；成功返回完整结果，超限整条调用拒绝，不返回静默截断。默认查询语义不变。

## 累计计费

候选实际进入保留集合之前、快照/排序阶段以及结果投影使用根 `SqlRowRetentionBudget`。
同一记录在不同阶段可能重复计费，Top-K 的每次实际替换也消耗累计配额，不退还已消费预算。
这限制本条语句的累计物化量，不是存活行数、CLR heap 或 NDJSON 编码字节的硬上限。
行估算沿用 `64 + 8 * 列数 + 标量载荷`；字符串为 `24 + 2 * UTF-16 长度`。

过滤、OFFSET 和小 LIMIT 不免除已经读取并保留的候选载荷。
EXPLAIN 的返回行与 ANALYZE 的实际执行和解释输出使用同一根预算，文件估算路径同样避免先全集物化。
文档向量 EXPLAIN 不读取集合计数，显示 `estimate_source=budgeted_estimate_omitted`；扫描行数的零值表示未采样，不能推断集合为空。

## 支持边界

| 来源 | 预算范围 | 预检拒绝的主要路径 |
| --- | --- | --- |
| measurement KNN | 既有 O(K) 候选保留与替换、标量字段回填、最终投影及分页 | 自定义排序、残余 WHERE、向量/空间结果、复杂来源 |
| 文档 vector_search | 单行文档游标或 ANN hit 到 SQL 候选、元数据过滤、距离升序、标量投影与分页 | 距离/分数或一般函数残余 WHERE、自定义/多列排序、复杂来源 |
| JSON 文件 | 逐记录读取、候选与投影保留 | 阻塞排序、复杂来源；文件读界见实现与本轮验证报告 |

三个来源共同拒绝用户函数、JOIN、CTE、派生来源、集合运算、聚合、窗口和 DISTINCT；注册同名内建 TVF 也不能绕过预算。
未支持路径在读取文件或打开候选扫描前拒绝。
沿用现有 TVF 文本语法，不提供来源别名；结果列别名仍可使用，measurement KNN 的限定符使用来源 measurement 名称。

存储快照/页、ANN 图工作集、单条记录和向量解析、参数绑定、表达式瞬时分配仍有独立边界。
JSON 预算读取仅支持 UTF-8（含 BOM），文件最多 256 MiB、单条记录最多 4 MiB、候选最多 1,000,000，另有五分钟与根取消边界；文件在读取期间须由宿主管理一致性。
预算 `auto` 在识别格式前跳过 UTF-8 BOM；现有默认 `auto` 不跳过 BOM，可能把单行数组当作一条 JSONL 记录。预算模式会正确按数组元素读取此输入，默认实现保持原行为；需要两种路径一致时显式指定 `array` 或 `lines`。
JSON `LIMIT` 可在完整消费当前记录后停止，只验证已读取的前缀；未读取尾部的语法错误由完整读取报告，不能把成功分页用作全文件校验。
文档向量预算路径对 TTL 集合使用固定查询时刻的只读游标；非 TTL ANN 的内部搜索工作集不纳入 SQL 候选估算。
该选择基于绑定时的 schema；宿主须保持查询期间 schema 稳定。并发新增 TTL/索引、索引初始化或维护可能使用存储层自身的回收/工作集，不在此 SQL 读取预算保证内。
本轮没有扩展 REST/Frame 的请求合同，没有取得远程 parity、固定硬件、首行/heap、跨架构或长期门禁。
该增量不表示 M42 或 SQL-002 整体完成。

## 可运行组合入口

```
dotnet run --project samples/SonnetDB.CdcStreamingJourney -c Release -- --budgets
```

入口实际读取三条 JSON 文件记录，保存至文档和 measurement，再运行三种预算拒绝、正常查询、flush 和数据库重开核对。
输入为本地合同语料，不作模型质量或性能证据。
分工、交叉审查、Release/AOT 和资源回收结果见[本轮报告](../audits/roadmap-parallel-next-20261003.md)。
