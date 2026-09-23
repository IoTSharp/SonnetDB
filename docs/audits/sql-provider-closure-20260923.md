# GH-Issue #177 / #180 / #193 功能闭环验收（2026-09-23）

三项原始需求的功能合同均已完成，状态为 **✅ 完成**。验收使用嵌入式引擎及本机真实 Kestrel REST、HTTP/2 Frame，不使用模拟数据库或模拟 HTTP 响应。最终 Release 回归 **358/358 通过，0 失败、0 跳过**。

这是工作树中的功能交付和真实服务集成验收。GitHub issue 的远程状态仍为 open；本报告不代表代码已推送、issue 已关闭、生产部署或固定硬件性能门禁通过。

## 原始需求与退出条件

| 任务 | 完整交付 | 验收 | 状态 |
|---|---|---|---|
| [GH-Issue #177](https://github.com/IoTSharp/SonnetDB/issues/177) | 关系 RIGHT/FULL/CROSS、NULL/重复/空输入、多连接链、列冲突/类型/排序规则、LEFT 回归和 ORM 能力发现；不支持的 measurement 外连接在 SELECT/EXPLAIN 均确定性拒绝 | `RelationalStandardJoinTests` 9/9，`RemoteStandardJoinTests` 23/23；并通过既有 JOIN/EXPLAIN/解析及 ADO 回归 | ✅ 完成 |
| [GH-Issue #180](https://github.com/IoTSharp/SonnetDB/issues/180) | 三种 JSON 函数、静态/参数 path、NULL/缺失/对象/数组/标量、错误与资源预算，以及有实际 EXPLAIN 证据的索引协作条件 | `SqlJsonFunctionTests` 14/14，`RemoteJsonFunctionTests` 24/24 | ✅ 完成 |
| [GH-Issue #193](https://github.com/IoTSharp/SonnetDB/issues/193) 选项 2 | 类型矩阵、SQL 参考、ADO `DataTypes` 明确仅 measurement FIELD 支持 VECTOR/GEOPOINT，并给出和验证显式关系替代模型 | `RemoteRelationTypeBoundaryTests` 15/15；CREATE/ALTER 拒绝无半成品，metadata、原生 FIELD 和替代模型均验证 | ✅ 完成 |

#193 原需求允许“完整关系类型支持”或“明确产品边界”二选一；本次完成的是后者，未把关系表 VECTOR/GEOPOINT 标为支持。#180 要求说明可用索引条件，已验证 `json_value` path 等值索引加 JSON 残余谓词；独立包含谓词的专用索引下推属于额外优化，不作为未交付功能隐藏。

## 本轮实际修复

1. `SndbConnection` 的 `SupportedJoinOperators` 原先只报告 INNER，现在报告 Inner/LeftOuter/RightOuter/FullOuter。`DataTypes` 保留原九行及标准列，追加模型能力列和 measurement 专用类型行，禁止 ORM 据类型名称误生成关系 DDL。
2. 参数绑定/CTE 展开会将 JOIN 规范化到 `JoinClauses`；measurement 执行器和 EXPLAIN 仍读取旧 `Join`，导致合法 INNER 查询或不支持类型的诊断出错。现统一读取规范化列表，只允许公开合同中的单个 measurement INNER JOIN；未实现的 measurement LEFT NULL 扩展明确拒绝。
3. `json_contains` 的数组贪心匹配会因重叠对象候选的顺序产生假阴性。例如目标 `[{"a":1,"b":2},{"a":1}]` 对候选 `[{"a":1},{"a":1,"b":2}]` 应为 TRUE。现用迭代增广匹配重新分配目标元素，保留一对一语义；辅助空间 O(n+m)，没有按候选数量递归，也不建立 n×m 比较图。扫描、重配和结构比较共享 1,000,000 次预算，并定期检查取消。

完整产品合同见 [标准 JOIN](../standard-join-contract.md)、[JSON 查询](../json-query-contract.md)、[关系类型边界](../relation-type-boundary.md)。三份合同均从 SQL 参考可达。

## 最终运行记录

- 基线 commit：`e818185590cdf0b14045dbfabe0c4d70f7907be2`，加本次未提交工作树增量；不将基线 SHA 当作包含本轮修复的发布 commit。
- 环境：Windows 11 x64，Intel Core Ultra 9 185H（16 核 / 22 逻辑处理器），约 63.5 GiB 可见内存；PowerShell 7.6.6，.NET SDK 10.0.401。
- Core Release：194/194，测试耗时约 7 秒，含构建约 30 秒。
- Server/SDK Release：164/164，测试耗时约 27 秒，含构建约 111 秒。
- 本轮新增 77 个数据化用例：15 个 Core、62 个集成用例。其余是已有功能的兼容回归。
- 所有构建串行执行，关闭 MSBuild/编译器常驻服务；每次命令设置 600 秒总超时，记录 PID、创建时间、命令和父进程，并核对任务子进程退出。

验收采用以下最终报告，原始日志和进程记录保留在本机 `artifacts/roadmap-closure-20260923/`（构建产物不入 Git）：

| 产物 | 内容 |
|---|---|
| `core-verified.trx` | 194 项最终 Core 结果 |
| `remote-verified.trx` | 164 项最终 Server/SDK 结果 |
| `core-verified.stdout.log` / `remote-verified.stdout.log` | 对应 Release 构建和测试输出 |
| `*.processes.json` | 有界命令、退出码、耗时与子进程身份 |
| `aot-properties-*.stdout.log` | MSBuild 实际 AOT/trim 属性 |
| `source-manifest.json` | 本轮源码/文档与最终 TRX 的 SHA-256 清单 |

复现命令（仓库根目录，PowerShell 7；外层应使用有界进程执行器）：

```powershell
dotnet test tests/SonnetDB.Core.Tests/SonnetDB.Core.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 -p:UseSharedCompilation=false --filter 'FullyQualifiedName~Join|FullyQualifiedName~SqlJsonFunctionTests|FullyQualifiedName~SqlExplain|FullyQualifiedName~SndbAdoApiTests|FullyQualifiedName~SqlCastTests|FullyQualifiedName~SqlParserTests' --logger 'trx;LogFileName=core-verified.trx' --results-directory artifacts/roadmap-closure-20260923

dotnet test tests/SonnetDB.Tests/SonnetDB.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 -p:UseSharedCompilation=false --filter 'FullyQualifiedName~RemoteStandardJoinTests|FullyQualifiedName~RemoteJsonFunctionTests|FullyQualifiedName~RemoteRelationTypeBoundaryTests|FullyQualifiedName~SqlFrameEndpointTests|FullyQualifiedName~FrameTransportParityTests|FullyQualifiedName~RemoteAdoEndToEndTests|FullyQualifiedName~RemoteVectorParameterTests|FullyQualifiedName~SchemaAndMaintenanceEndpointTests' --logger 'trx;LogFileName=remote-verified.trx' --results-directory artifacts/roadmap-closure-20260923
```

## AOT、协议和证据边界

Core 与 Server 的 `IsAotCompatible`、`EnableTrimAnalyzer`、`EnableAotAnalyzer`、`TreatWarningsAsErrors` 实测均为 true；本次 Release 构建没有 IL/AOT 或其他编译警告。`SonnetDB.Data.csproj` 原有 `IsAotCompatible=false`，其 ADO.NET/DataTable 元数据不承诺 Native AOT，本轮没有弱化或改变这个边界。没有新增反射 JSON 序列化、unsafe 或 Core 第三方依赖，也未改变文件/帧格式。本次未执行 NativeAOT 发布，不能据分析器通过宣称跨架构原生发布完成。

每条验收 SELECT 观察实际服务端请求路径和 HTTP 版本，Frame 必须命中 `/v1/frame` 且为 HTTP/2。DDL/DML 和实际关系目录使用 REST；`GetSchema("DataTypes")` 是 SDK 本地能力表，无网络请求，也不进行旧 Server 版本协商。JSON 长参数测试使用短结果别名，保留现有 Frame 的 512 字节列名和请求大小限制。

JOIN 重启用例是同一测试进程内完整停止/重建真实 Server host 后重新加载磁盘目录，并非跨操作系统进程 hard-kill。预取消用例验证未发送和连接复用，不承诺大规模执行中取消延迟。512 行 JSON 语料只证明结果完整性。PostgreSQL 等外部对拍、固定硬件容量、生产网络、长期 SLO 与 M20 七次 scheduled 均不计入上述 PASS；M27/后续里程碑继续按路线顺序执行。
