# M27 #187 Copilot Eval 与成本报告

`m27-copilot-eval-v1` 是 M27 #187 的可复现报告合同。示例文件见 [`m27-copilot-eval-report.example.json`](m27-copilot-eval-report.example.json)，PowerShell 7 verifier 见 [`verify-m27-eval-report.ps1`](../../tests/SonnetDB.Tests/Copilot/scripts/verify-m27-eval-report.ps1)。

报告必须覆盖五条场景：`anomaly_device`、`slow_query`、`schema`、`repair_advice` 和 `approval`。每条场景记录 `provider`、`model`、有序 `toolNames`、`toolCalls`、`failureReason` 和 `usage`。`usage.reported=false` 时 input/output/total tokens 和 `costUsd` 必须为 `null`；不能用字符数或脚本响应估算模型调用成本。

现有 xUnit nightly suite 使用 `ScriptedChatProvider`，它只验证工具规划、错误修复、引用和延迟，不是真实模型调用。因此示例报告的总体状态是 `NOT_READY`，`run.realProvider=false`，并保留所有 token/cost 为空。真实 provider 接线后，报告生成器必须把 provider 返回的 usage 原样写入并通过 `-RequireReady`，否则不能作为模型质量或成本发布证据。

验证：

```powershell
& $PSHOME\pwsh.exe -NoLogo -NoProfile -File tests/SonnetDB.Tests/Copilot/scripts/test-verify-m27-eval-report.ps1
```

工业数据链路的独立样例见 [`samples/SonnetDB.IndustrialDiagnostics/README.md`](../../samples/SonnetDB.IndustrialDiagnostics/README.md)。它把 `dataStatus=PASS` 与 `copilot.status=NOT_READY` 分开，避免本地 SQL/MQTT smoke 被误报为 provider 证据。
