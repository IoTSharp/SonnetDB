# M42 / SQL-002：显式 SQL 结果预览合同

2026-09-24 本地合同切片完成：REST SQL 调用方可以显式要求有界结果预览；Web SQL 控制台使用该合同。该切片不代表 SQL-002 的执行阶段内存边界或整项验收完成。

## 请求和响应

`POST /v1/db/{db}/sql`、`/sql/batch` 中的单条语句和 `/v1/sql` 可传 `previewMaxRows`（正整数）。实际预算为请求值与 `SonnetDBServer:SqlExecution:MaxResultRows` 的较小值；配置默认 10000，绑定范围 1～1000000。无效请求值在该语句执行前拒绝，避免写入后才报告预算错误。

未提供 `previewMaxRows` 的 REST 调用、嵌入式调用和 Frame 调用保持完整结果语义。Frame 字节格式未扩展；已有客户端和公共记录类型的主构造/Deconstruct 签名保持兼容。

```json
{"sql":"SELECT id FROM orders ORDER BY id","previewMaxRows":1000}
```

查询和 DML RETURNING 的结果超过预算时只传前 N 行，NDJSON end 的 `rowCount` 为实际返回行数，`truncated=true` 明确表示数据不完整；恰好 N 行或更少为 `false`。`recordsAffected` 始终保留完整写入影响数。预览不会更改 SQL 的写入、COMMIT 或 ROLLBACK 语义，也不会中止批次后续语句。

Web 通用 SQL API 不自动启用预览；仅 SQL 控制台显式请求最多 10000 行，在历史摘要、执行摘要及警告中显示结果已截断。依赖完整查询结果的导出和其他工作台沿用原行为。缺少 end、非法行数或格式损坏不视为成功完整结果。`SndbDataReader.Truncated` 提供可检查的状态；REST 流读到 end 后确定该值。

## 验证

- `SqlResultBoundsTests`：预览前缀、精确边界、默认完整和 DML 影响行数。
- `SqlResultBoundsEndpointTests`：REST 默认完整/显式截断、请求与 Server 上限、RETURNING、批次提交/失败回滚、非法预算不执行写入，以及 Frame 字节格式未变化。
- `web/tests/sql-workflow.test.mjs`：实际 API/控制台模块的 opt-in 请求、截断状态、非法元数据、结果不完整提示，复用既有取消与事务批次测试。

最终本地验证：Core 预览 3 项、公开 ADO 13 项、真实 REST/Frame/ADO 端点 12 项通过；连同覆盖索引和超时回归，Core 为 87/87。Server 首轮 185 个既有/relay 用例通过，新预览 fixture 的 12 项因使用不支持的列内主键语法初始化失败；仅将 fixture 改为表级主键后，12/12 定向重跑通过。两轮共 197 个不同 Server 用例最终通过，首次失败记录仍保留。[统一记录](../audits/roadmap-closure-evidence-20260924/validation.json)、[Core TRX](../audits/roadmap-closure-evidence-20260924/core.trx)及[端点 TRX](../audits/roadmap-closure-evidence-20260924/sql-preview.trx)包含原始证据。

Web：`node --experimental-vm-modules --test web/tests/sql-workflow.test.mjs` 共 11 项通过、0 失败、0 跳过；`vue-tsc --noEmit` 通过，Vite production build 通过，仍有大于 500 kB chunk 的体积提示。[测试日志](../audits/roadmap-closure-evidence-20260924/sql-preview-web-tests-final.out.log)与[构建日志](../audits/roadmap-closure-evidence-20260924/sql-preview-web-build-final.out.log)已留存。全部构建产物输出到本轮专属 C 盘临时目录，不写入仓库；.NET Release 使用 `/warnaserror`，未压制 AOT/trim 警告。

## 未覆盖的内存与性能边界

当前执行器仍先生成完整 `SelectExecutionResult`。此切片减少显式预览的传输行数和 Web 留存行数，**不保证服务端执行 heap 上界、字节预算或首行延迟**；单行可含大值，行数上限不能替代字节上限。完整 SQL-002 仍需算子/输出物化预算或端到端流式执行，以及大结果 heap/首行延迟基准、断连压力与固定硬件证据。不得将此文或合同测试用作完整 SQL-002、M42 或生产门禁完成证据。
