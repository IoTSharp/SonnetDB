# 大结果 ORDER BY 的资源和传输合同

2026-11-27（Asia/Shanghai）核查 `8cea0680` 的排序调用链。首个实现范围为直接 measurement raw SELECT 的 `ORDER BY time [ASC|DESC]`，涵盖稳定排序、分页和完整结果交付。关系表、复杂聚合和连接各自保留原有执行入口。[GH-Issue #440](https://github.com/IoTSharp/SonnetDB/issues/440)

首个代码切片针对无分页的单序列、单字段流直接查询，要求投影中第一个名为 `time` 的列确为时间列。查询复用升序点流，降序原地反转最终列表，整份结果仍通过 `Rows` 返回。显式排序预算、预览和复杂查询沿用原路径。源码回归覆盖重复点末值、升降序、强制外排结果和预览额度拒绝。[执行器](../../src/SonnetDB.Core/Sql/Execution/SelectExecutor.cs) · [定向回归](../../tests/SonnetDB.Core.Tests/Sql/SqlExecutorSelectTests.cs)

## 当前调用链

| 路径 | 当前实现与边界 |
| --- | --- |
| 普通 measurement | 简单带 LIMIT 的查询已有分页下推。各 series 按时间生成行，优先队列归并后跳过 OFFSET，收集所需页。末位相同时间戳按 series 顺序；单 series 内同一字段和时间戳遵循现有 raw SQL 选值规则。其他适用路径先执行 raw 或聚合生成 `SelectExecutionResult.Rows`，再把候选传给 `TopN`。该调用返回数组，即使外排生效，排序前候选和最终输出仍可能整体驻留。[SelectExecutor](../../src/SonnetDB.Core/Sql/Execution/SelectExecutor.cs) |
| 算子 | `TopN` 在可表示且预算允许的排序前缀使用稳定有界堆。SQL 根作用域启用预算并且提供 codec 时，它调用 `SqlSpillSorter`。后者按输入序号处理相同键，内存不足时写 run，最多 32 路归并，`Order` 支持逐行枚举，`OrderByThenPaginate` 返回数组。现有峰值指标统计算子 reservation。排序工作集、整库并发额度和用户结果的内存生命周期各有范围。[TopN](../../src/SonnetDB.Core/Sql/Execution/TopN.cs) · [SqlSpillSorter](../../src/SonnetDB.Core/Sql/Execution/SqlSpillSorter.cs) · [阻塞算子合同](../m41-blocking-operator-spill.md) |
| 时序读取 | `QueryEngine` 为单个点流持有段快照租约，并读取对应 MemTable 范围。排序会组合多个字段与 series 的枚举器，每个流的已有租约只定义其自身范围。首个交付须确认整条语句的读视图及其释放点，并在慢客户端、flush/compaction 和并发写入下核对。跨请求继续分页需要单独定义快照身份。[QueryEngine](../../src/SonnetDB.Core/Query/QueryEngine.cs) |
| Workbench Preview | `preview-1` 使用显式 10,000 个累计留存行、4 MiB SQL 值估算和 30 秒关联取消。直接 raw 查询按现有准入对排序候选、排序键和输出计费，再执行分页。超限走预算拒绝；显式预览截断须有结果状态。[WorkbenchPreviewSql](../../src/SonnetDB/Endpoints/Support/WorkbenchPreviewSql.cs) · [measurement 排序准入](../benchmarks/m42-measurement-order-bounds.md) |
| REST、Frame、ADO.NET 和 embedded | REST SQL 在执行后按 NDJSON 写出，缺少列类型声明时会先从结果行推断 meta。Frame Query 从 `Rows` 进行分块编码和回压。远程 ADO.NET 可边接收边读，嵌入式 ADO.NET 使用物化适配器。入口传输具备各自的分块能力，排序仍要追踪执行器产出首行时的内存和所有权。[REST](../../src/SonnetDB/Endpoints/Handlers/SqlEndpointHandler.cs) · [Frame](../../src/SonnetDB/Endpoints/Handlers/FrameEndpointHandler.cs) · [远程 ADO.NET](../../src/SonnetDB.Data/Remote/RemoteExecutionResult.cs) · [嵌入式 ADO.NET](../../src/SonnetDB.Data/Embedded/EmbeddedConnectionImpl.cs) |

## 首个实现切片

1. 保留已支持的投影和过滤语义，将适用的 measurement 时间顺序候选按需交给排序或归并。对已可按时间遍历的输入复用现有归并，对其它已支持形状复用 `TopN` 或 `SqlSpillSorter`。限制同时打开的 series 和文件游标，记录候选数、堆中行数与内存峰值。用固定基线逐行验证相同时间戳的顺序、跨字段合并、NULL 和重复点。
2. 建立可释放的结果读取生命周期。查询建立列元数据和读视图，然后逐批输出。对于必须消费全集才能确定顺序的输入，先完成排序准备，再逐批归并。REST、Frame 和 ADO.NET 分别核对首行、最后一行、成功终态与提前停止。兼容物化入口依原合同返回完整结果，流式入口的列类型以已绑定元数据或明确兼容的类型回退产生。
3. 让同一语句的取消、截止时间和断连贯穿读取、比较、临时文件和发送。分别约束算子保留内存、同时打开游标、临时 live bytes、发送批次和并发准入。沿用工作区归属标记，按当前文件大小管理 live bytes，并独立累计 spill I/O。额度耗尽、磁盘写入失败或取消时终止对应请求。响应开始后，由协议向客户端传播失败或缺失成功终态，保留已发送前缀的可观测结果。
4. 先核对仍处于请求作用域的快照、排序 run、配额和准入许可，再向上层交付完成。客户端提前关闭读者时释放这些资源，异常退出时由下次打开清理带归属标记的遗留目录。普通 SQL 交付完整结果，Workbench Preview 延续显式预算和预览状态。[SQL 查询资源作用域](../../src/SonnetDB.Core/Sql/Execution/SqlQueryResources.cs)

## 验收矩阵

| 范围 | 固定用例与退出条件 | 本轮 |
| --- | --- | --- |
| 结果与顺序 | 对同一固定 seed 小语料比较现有物化结果、新路径及强制 spill。覆盖空输入、并列时间、Unix 毫秒时间戳边界、不同宽度字段、ASC/DESC、OFFSET/LIMIT 越界及无 LIMIT 完整结果。 | NOT_RUN |
| 有界执行 | 先用确定性小语料强制外排，再上升到 10 万和 100 万行分层语料。分别记录输入与输出行数、首行与完整交付时间、峰值算子内存、进程峰值、live/cumulative spill 字节及打开游标峰值。所有比较使用同一语义、版本、查询和数据。 | NOT_RUN |
| 传输与超限 | embedded、REST、Frame、远程 ADO.NET 和 Workbench 分别验证列元数据、行数和终态。普通 SQL 对拍全结果，Preview 验证 10,000 行、4 MiB、30 秒和显式截断结果，独立覆盖慢读者、断连、取消与超时。 | NOT_RUN |
| 再次打开与资源回收 | 覆盖短读、排序/归并/发送期间取消、临时盘额度耗尽、写入失败、进程异常退出，以及重开清理。断言临时文件、读租约、内存额度和并发准入可回收，控制目录和无归属文件仍存在。 | NOT_RUN |
| 并发读视图 | 在受控屏障中交叉执行写入、flush/compaction 和慢读，验证所有返回行来自声明的读范围。比较并列键在单次查询内的稳定性，跨语句分页按单独合同记录。 | NOT_RUN |

基础回归作为对照入口。多序列实现、完整结果传输、性能容量和固定硬件验收按矩阵逐项记录。现场重启与 OOM 的因果需实际运行时间线、进程指标和错误日志。[强制 spill 回归](../../tests/SonnetDB.Core.Tests/Sql/SqlBlockingOperatorSpillTests.cs) · [measurement 排序预算回归](../../tests/SonnetDB.Core.Tests/Sql/MeasurementOrderMaterializationBudgetTests.cs) · [环境与现场资格路线](../roadmap-environment-validation.md)
