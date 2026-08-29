# SonnetDB Industrial Diagnostics Sample

这是一条可复现的工业数据 journey：用 HTTP Line Protocol（或显式启用的 MQTT）写入温度、电流、振动，执行异常设备查询，并输出带维修建议和引用的 JSON 报告。

```powershell
$env:SONNETDB_TOKEN = '<database-write-token>'
dotnet run --project samples/SonnetDB.IndustrialDiagnostics -- --transport http
```

MQTT 需要先在 Server 配置并启动 broker：

```powershell
dotnet run --project samples/SonnetDB.IndustrialDiagnostics -- --transport both --mqtt-port 1883
```

报告默认写入 `artifacts/m27-industrial-diagnostics/industrial-diagnostics-report.json`。`dataStatus=PASS` 仅证明本地数据链路，`transportStatus` 单独表示 MQTT 是否可用；Copilot provider 没有绑定、不可达或未请求时，报告中的 `copilot.status` 保持 `NOT_READY`，不会把确定性样例文本计作模型输出。即使使用 `--copilot`，token usage 只有 provider 明确返回时才记录，当前 sample 不估算 token 或费用。

进程只有在报告总体 `status=PASS` 时返回 0；任一已请求的链路仍为 `NOT_READY` 时返回 3，便于 CI 保留报告并阻止误将部分 smoke 当作完整证据。

建议和引用是演示素材，不构成自动维修命令；写入或控制操作仍需 M29 staged approval。

离线合同 smoke（不会声称 Server 或 provider 可用）：

```powershell
& $PSHOME\pwsh.exe -NoLogo -NoProfile -File samples/SonnetDB.IndustrialDiagnostics/scripts/test-industrial-diagnostics.ps1
```
