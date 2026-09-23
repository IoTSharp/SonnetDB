# GH-Issue #196：measurement-to-relation 参数化 JOIN 合同与验收

本交付修复 measurement JOIN 执行器与参数绑定器使用不同 AST 字段的问题，并给出可由 Provider 生成 SQL 前检查的模型能力。**功能合同已完成：最终 Release 回归 394/394 通过，0 失败、0 跳过。** 专用新增 Core 18 项、嵌入式及真实远程 18 项均通过；GitHub 关闭状态以实时快照为准。

## 问题与修复

`SqlParameterBinder` 把 JOIN 统一保存在 `SelectStatement.JoinClauses`，同时清空旧的 `Join` 属性。此前 measurement 执行器读取旧字段，即使 SQL 和绑定值合法，也可能报“JOIN 执行器要求 SELECT 包含 JOIN 子句”。当前执行器和 EXPLAIN 均读取规范化后的 JOIN 列表。参数重写保留原 AST，覆盖 WHERE、投影、ON 与分页；ON 能否执行还须满足以下模型范围。

三入口逐行对拍还发现同类排序缺陷：绑定器清空 `OrderBy` 并保存到 `OrderByItems`，measurement JOIN 仍读取旧属性，嵌入式 ADO 因而丢失排序。现字段加载、排序值计算与结果排序统一使用完整 `OrderByList`，按每项的 ASC/DESC 方向稳定排序。新增第二个未投影 FIELD 的升降序，以及关系键加隐藏 FIELD 的多键混合方向与参数分页用例，确保排序跨 series 生效、后续排序项没有被忽略，且分页在排序之后执行。

本轮另将 GROUP BY、HAVING 和聚合函数的拒绝提前到读取数据之前，避免 `COUNT(*)` 在有数据时失败、空输入时却返回空行集。SELECT 和 EXPLAIN 使用同一检查；检查以迭代遍历实现，最多 65,536 个表达式节点和 5 秒，并响应 SQL 执行取消令牌。

## 可执行范围

| 项目 | 合同 |
|---|---|
| 数据源 | 左侧一个 measurement，右侧一个关系表 |
| 连接种类 | 仅 INNER JOIN；LEFT/RIGHT/FULL/CROSS 拒绝 |
| ON | 单个等值条件，一侧为 measurement TAG，另一侧为关系列；列引用可以交换左右位置 |
| 参数 | 支持 WHERE、标量投影和 OFFSET/FETCH；字段/表名不是参数 |
| ON 参数/附加谓词 | 拒绝；`ON a.Host = b.Name AND b.Id = @id` 应由 Provider 对本 INNER 合同改写为 `ON a.Host = b.Name WHERE b.Id = @id` |
| 多个 JOIN | 拒绝，不把多连接链当作单关系维表 |
| 投影 | 支持已解析的 time、TAG、FIELD、关系列及标量；使用显式 alias 可稳定结果列名 |
| 聚合与分组 | 不支持聚合、GROUP BY、HAVING；空/非空输入与 EXPLAIN 均拒绝 |
| 分页 | 支持排序后的 OFFSET/FETCH，参数必须绑定为非负整数；这是结果分页，不承诺有界扫描或固定硬件性能 |
| 排序 | 此 JOIN 支持多个排序键、每键 ASC/DESC 与未投影 FIELD；相等键保持输入稳定次序，分页在完整排序之后执行 |
| 轻事务 | 可读取已提交 measurement 与关系数据；本次仅验收读取及回滚，不声明跨模型原子写入、measurement 快照或并发隔离升级 |
| measurement/document | 不属于此 JOIN 合同；右侧文档集合明确报“JOIN 右侧必须是关系表” |

可执行的参数化 SQL：

```sql
SELECT a.time, a."Host", b."Id", a."Value"
FROM "sonnet_metric" AS a
INNER JOIN "sonnet_device" AS b ON a."Host" = b."Name"
WHERE a.time >= @begin AND b."Id" = @device_id
ORDER BY a.time
OFFSET @skip ROWS FETCH NEXT @take ROWS ONLY;
```

这些范围是 measurement-to-relation 的合同。关系表自身的 RIGHT/FULL/CROSS、多连接及聚合能力不授权 Provider 对 measurement 使用相同语法。

## Provider 的生成前能力检查

`SndbConnection.GetSchema("DataSourceInformation")` 保留标准列，追加以下机器可读字段：

| 字段 | 类型和值 |
|---|---|
| `MeasurementJoinKinds` | string：`INNER` |
| `MeasurementJoinMaxTables` | int：`1`，表示右侧关系表的最大数量 |
| `MeasurementJoinOnPredicate` | string：`measurement_tag_equals_relation_column` |
| `MeasurementJoinParameterLocations` | string：`projection,where,pagination` |
| `MeasurementJoinSupportsAggregates` | bool：`false` |
| `MeasurementJoinSupportsPagination` | bool：`true` |

Provider 应先从实体映射确定左/右模型与 TAG 列，再按这些字段检查 JOIN 种类、数量、ON 形状、参数位置和聚合。超出合同应在生成/发送 SQL 前返回清楚的能力错误，不能把外连接改写成 INNER，也不能静默丢弃 ON 谓词。旧 SDK 缺少扩展列时应维持禁用。以上扩展是 SDK 本地能力，读取没有 HTTP 请求；它不协商旧 Server 版本，部署必须使用包含对应修复的 Server。

通用 ADO.NET 接口仍允许用户直接提交 SQL，不能据此宣称它自动执行模型预检。测试有意将不支持语句发送给服务器以验证稳定拒绝。缺少参数值属于客户端已知错误：驱动在请求发出之前拒绝，测试断言服务端观察到零请求。

## 错误与协议

嵌入式不支持模型/形状抛出 `InvalidOperationException`，消息包含具体范围；REST/Frame 包装为 `SndbServerException`，错误码 `sql_error`，保留中文模型诊断。重复执行同一错误得到相同诊断；错误后连接可继续执行合法 JOIN。缺失参数由客户端抛 `InvalidOperationException` 并指出参数名。

非事务 SELECT：REST 使用 `/v1/db/measurement_join/sql`、HTTP/1.1；Frame 使用 `/v1/frame`、HTTP/2。测试检查实际请求端点和协议，不将 REST 回落算作 Frame。DDL/DML 使用 REST；远程轻事务读取经 REST 控制面执行，`frame-http2` 配置不将事务读改为二进制 Frame。

## 验收矩阵与运行记录

新增 `MeasurementJoinContractTests` 验证 AST 绑定/不可变性、原 issue 引号 SQL、多行结果、SELECT/EXPLAIN、模型拒绝及空/非空聚合诊断。新增 `RemoteMeasurementJoinContractTests` 验证真实 Kestrel HTTP/1.1 与 HTTP/2，并以独立嵌入式库对照：

- 同步/异步读取 × 三入口，无参数、WHERE 参数、标量投影参数和分页参数；多行包含 time/TAG/FIELD/关系值及 CLR 类型。
- ON 参数、多 JOIN、LEFT、measurement/document、GROUP BY、COUNT 的重复诊断与错误后恢复。
- 缺失参数零网络请求；能力 metadata 零网络请求；轻事务读取与回滚。

每个远程 fixture 总时限 60 秒，命令 5 秒，最多 2 个端口探测、2 个种子库、5 条种子语句、9 种错误各重复 2 次、8 行读取；停止和销毁各 10 秒。只清理 fixture 自己生成且核实位于临时目录的唯一目录。

2026-09-23 Windows / .NET SDK 10.0.401 最终串行运行：Core 212/212、Server/SDK 182/182，包含前轮标准 JOIN、JSON、类型边界、Frame、ADO 和 metadata 回归。两次构建均无编译警告。最终报告为 `artifacts/roadmap-closure-20260923/core-196-final.trx` 与 `remote-196-final.trx`，对应 `.stdout.log`、`.processes.json` 记录完整命令、耗时、退出码及进程身份。外层每次最多 600 秒，禁用常驻编译服务。

初次对拍实际发现嵌入式参数绑定后丢失排序，已修复后重跑；Core 的中间失败还包含生产和测试编译时源文件不同步，因此最终构建在文件冻结后执行。中间失败报告保留，不计入上述 PASS。最终排序测试覆盖隐含 FIELD、多键 ASC/DESC、参数化分页，保留原始逐行值和类型断言。

```text
dotnet test tests/SonnetDB.Core.Tests/SonnetDB.Core.Tests.csproj -c Release --filter FullyQualifiedName~MeasurementJoinContractTests
dotnet test tests/SonnetDB.Tests/SonnetDB.Tests.csproj -c Release --filter FullyQualifiedName~RemoteMeasurementJoinContractTests
```

本机集成证据不代表 FreeSql Provider 代码已修改或发布、旧 Server 自动兼容、生产部署或固定目标硬件容量门禁。Issue 关闭应引用包含上述源码与文档的提交及最终实际测试结果。
