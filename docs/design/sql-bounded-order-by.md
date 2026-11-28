# 大结果 ORDER BY 的资源和传输合同

本切片基于 `c42e7044`，沿用 `e57afee9` 的单序列单字段读取和已有稳定 Top-N、外部归并实现。[GH-Issue #440](https://github.com/IoTSharp/SonnetDB/issues/440)

## 执行路径

| 路径 | 读视图和排序 | 交付 |
| --- | --- | --- |
| 直接 measurement 的 `ORDER BY time` | 在同一写入边界内捕获 series、相关活跃点范围、墓碑、向量替换视图和段租约。低扇入使用时间堆归并，最多打开 32 个选定点流。高扇入逐序列读入稳定 Top-N 或外排，单序列的读者数量随选定字段数增长。时间并列时按该语句捕获的目录序列顺序，同字段同时间戳沿用原来的末值规则。 | Core 流式入口支持时间输出列未被别名遮蔽、投影可声明类型且 WHERE 可下推的直接查询。ASC、DESC、LIMIT、OFFSET 均可进入该路径。兼容 Core 调用可继续获得物化的 `Rows`。 |
| 直接关系表全结果 | 在语句打开时取得 KV 表快照，扫描候选行、筛选、投影，按现有列排序比较器和候选行序执行外排。支持列名排序和普通列投影，OFFSET 在归并后执行。 | 流式路径目前覆盖无 FETCH 的直接表扫描。需要二级索引访问计划、复杂表达式、JOIN、CTE、聚合、DISTINCT 或活动事务的查询继续走现有执行器。已有分页 Top-N 和覆盖索引计划照常使用。 |
| Workbench Preview | 直接查询继续使用现有物化准入，累计最多 10,000 个保留行、4 MiB 估算值和 30 秒截止。 | 显式请求结果上限可返回 `truncated`，候选或排序阶段超额返回预算错误。普通有序查询读至成功终态时提供完整结果。 |

`SqlExecutionOptions.StreamResults = true` 允许 `SqlExecutor.Execute` 或 `ExecuteStatement` 返回 `SqlStreamingSelectResult`。使用 `using` 管理结果，并反复调用 `MoveNext` 和读取 `Current`。适用查询在打开阶段声明列元数据并固定读视图。可合并的输入直接产出第一行，需要读取全部候选才能排序的输入先生成有序 run。读至末尾、单次读取失败或提前释放时关闭游标、归还内存额度并清理排序工作区。绝对期限继续覆盖最后一次网络刷新，直到结果释放。

同一条语句的输入版本跨写入、flush、compaction 和慢读保持固定。再次执行 SQL 或单独执行下一页会获得新的读视图。受支持的语句内相同排序键保留原候选顺序。跨请求没有读视图续传凭证。

## 资源与错误

- 流式排序使用数据库和单查询的阻塞算子内存预留，临时 run 受 `SortSpillMaxBytes` 约束，默认 8 GiB。归并输入与输出暂时共存时一起计入，文件增长前按编码后字节预留。累计写出统计覆盖完成的初始和中间 run。归并同时最多打开 32 个 run 读者。超大单行仍按现有 oversized 归并合同处理。
- 所选活跃 MemTable 切片、段租约、段块读缓存与单序列投影宽度属于输入读取开销。阻塞算子预留只计排序工作集。查询打开时复制所选活跃点并短时持有写入边界。默认 MemTable 写入策略给活跃内存设上限。
- 请求取消、绝对截止或断连贯穿游标读取、比较、写出和响应刷新。空间额度耗尽和 I/O 错误立刻结束受影响的请求。客户端可能读到已发送的行前缀，此时没有成功终态。正常释放主动删除工作区。异常退出后的数据库重开只清理归属标记匹配的临时目录。
- 慢查询条目和低基数指标记录候选行、返回行、耗时、峰值算子预留、峰值 live spill、完成 run 的累计写出字节、比较次数和排序读者峰值。按现有诊断配置启用，指标使用现有的 outcome、access path 和 fallback reason 标签。

## 消费者和兼容

REST 先发送 NDJSON 列元数据，然后每 64 行或约 64 KiB 刷新一批。Frame 使用现有 meta、rows、end 帧和分块策略。单条 REST 请求和开始输出前的 Frame 可通过 `X-SonnetDB-Sql-Streaming: 1` 识别 Core 游标。启用 `SqlExecution:StreamingQueryTimeoutMilliseconds` 时覆盖只读 `ORDER BY` 的执行和流式响应写出，默认 `0` 由请求取消控制。HTTP 请求准入保留至结果交付结束。

远程 REST ADO.NET 继续逐行读 NDJSON。嵌入式和远程 Frame ADO.NET 通过 `CommandBehavior.SequentialAccess` 选择按行或按块读取。Frame 的逐块读取器验证帧身份、成功终态、结束行数和响应末尾。已有 .NET Frame 客户端可继续直接使用 `SendStreamingAsync`。工作台、执行脚本、GQL 和未选择逐步读取的 ADO.NET 路径保留原有结果形态。

## 验收与复现

验证命令、进程回收记录和未执行环境项见[工作树交接](../../HANDOFF.md)。

| 验收 | 有界用例 |
| --- | --- |
| 结果与顺序 | 固定小语料对拍原始稳定排序、时间归并和强制外排。覆盖稀疏字段、重复点、时间并列、ASC/DESC、空页、长 OFFSET、Unix 时间戳边界。 |
| 容量 | 固定种子 440、8 个序列，按 10 万和 999,992 行两层逐行核对。记录首行与全量时间、进程生命周期峰值、候选、比较次数、预留和 spill，使用生成公式校验大语料。 |
| 真实消费者 | 本地 Kestrel HTTP/1.1 REST、HTTP/2 Frame、REST/Frame ADO.NET 和嵌入式 ADO.NET。验证列、首末行、完整行数、结果终态、预览截断或拒绝、发送期限和客户端断连。 |
| 读视图与回收 | 固定屏障覆盖并发写入、删除、flush 和物理 compaction。计算或写出取消、短读、文件路径写失败和 live quota 耗尽均检查资源回收。独立本地 Server 进程持有运行中的 spill 时强停，重开验证原数据与控制目录。 |
| 长稳与环境 | 512 轮混合完整、短读、取消和额度拒绝。物理磁盘 ENOSPC、固定硬件、168 小时和现场环境另行取证。 |

核心入口见[时序归并](../../src/SonnetDB.Core/Sql/Execution/SelectExecutor.OrderedStream.cs)、[选择与游标](../../src/SonnetDB.Core/Sql/Execution/SqlExecutor.Streaming.cs)、[外部排序](../../src/SonnetDB.Core/Sql/Execution/SqlSpillSorter.cs)和[分层测试](../../tests/SonnetDB.Core.Tests/Sql/StreamingOrderByCapacityTests.cs)。
