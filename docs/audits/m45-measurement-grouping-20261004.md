# M45-C01 measurement TAG 分组首批验证

日期：2026-10-04。基线：`4b004946`；验证对象为本次 TAG/time 分组改动后的工作树，源码 SHA-256 保存在本机 `artifacts/m45-c01-20261004/source-hashes.json`。当前是 M45-C01 的部分切片，完整类型统一、HAVING、聚合状态预算及持续计算仍待实现。

## 分支治理

- 核对 7 个本地 `codex/*` 分支全部是 `main` 的祖先，无未合并提交，使用 `git branch -d` 清理。
- 唯一缓存的 `origin/codex/roadmap-four-closures-20261001` 已不存在于远端；`git fetch origin --prune` 清理失效引用。
- 未合并或删除 `parity-results`，并将该限制写入 `AGENTS.md`。
- 本次没有改写历史；此前的 4.5 规划文档改动保留为独立未提交工作。

## 交付范围

见[支持矩阵与成本合同](../benchmarks/m45-measurement-tag-grouping.md)。首批支持一个或多个 TAG 以及可选时间桶的裸聚合投影：完整行时刻并集建立组，缺少 TAG 为 NULL 键，稀疏 FIELD 的 COUNT 为 0、其它空聚合为 NULL，Int64 SUM 使用 checked Int64。统一名称绑定也覆盖直接 planner 分组入口，EXPLAIN 声明阻塞状态及结果物化。

未支持的残差/Geo WHERE、measurement HAVING、复合投影、TAG/别名/多键排序提前拒绝。现有 time-only 聚合的类型行为未被统一改写，默认分组状态和扩展/DISTINCT state 没有新增字节准入或 spill。

## 本机验证

环境：Windows，PowerShell 7.6.6，.NET SDK 10.0.401。仅使用当前工作树，未安装工具或新依赖。

| 检查 | 结果与边界 |
|---|---|
| Core Release build | 通过，0 warning / 0 error；项目既有 `IsAotCompatible=true` 启用 trim/AOT 分析，未新增相关警告。未执行 NativeAOT publish。 |
| 最终定向 Release 回归 | 181/181 通过，覆盖新分组、旧 SELECT/聚合、EXPLAIN、扩展与流聚合、SQL 表达式。不是完整 Core 回归。 |
| 新增测试 | 17 项，覆盖多 TAG/跨 series、TAG 值大小写、空输入、NULL TAG/稀疏 FIELD、COUNT(*) 去重、flush/reopen、Int64 精度及溢出、名称/引号、支持矩阵拒绝。 |
| `dotnet restore SonnetDB.slnx` | 通过，项目均为最新。 |
| CI 同等 Format Check | 最终退出 0；输出一条工作区加载提示，无格式错误。 |
| `git diff --check` | 通过。 |

实现中间轮曾因测试数据的桶数/顺序、保留字名称及非法空 TAG 值失败，修正 fixture 后重新验证；格式检查曾发现两处空格，修正后再运行完整命令取得退出 0。不能将这些中间失败改写为从未失败。

最终定向测试命令：

```powershell
dotnet test tests/SonnetDB.Core.Tests/SonnetDB.Core.Tests.csproj -c Release --no-restore --filter 'FullyQualifiedName~SqlExecutorMeasurementGroupByTests|FullyQualifiedName~SqlExecutorSelectTests|FullyQualifiedName~SqlExplain|FullyQualifiedName~SqlExecutorCategoricalAggregateTests|FullyQualifiedName~SqlExecutorMultiAggregateTests|FullyQualifiedName~SqlExecutorExtendedAggregateTests|FullyQualifiedName~SqlExecutorAggregateStreamingTests|FullyQualifiedName~SqlExpressionExecutionTests|FullyQualifiedName~SqlIdentifierCase'
dotnet restore SonnetDB.slnx
dotnet format SonnetDB.slnx --verify-no-changes --no-restore --severity warn --exclude extensions/
```

测试 runner 设 240 秒超时，最终格式 runner 设 300 秒超时；记录 dotnet PID、创建时间、命令及父 PID，超时/异常清理任务子进程树。最终检查确认已记录进程均结束。原始输出、进程元数据与源码哈希保留在本机 `artifacts/m45-c01-20261004/`，不提交构建产物。任务专属临时 runner 在完成后删除。

真实 Server/SDK/MCP 组合、容量/总 heap、固定硬件、长稳及生产门禁本轮未执行。
