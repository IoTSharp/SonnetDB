# M43 #397 发布证据汇总

`eng/verify-m43-release-evidence.ps1` 把候选提交的八组证据汇总为 `PASS`、`NOT_READY` 或 `DEFERRED`。它重新运行仓库已有 verifier，不接受由清单直接指定 `PASS`，也不接受任意 verifier 命令。每次 verifier 使用新目录，保存原始结果路径和 SHA-256，失败不能复用旧报告。M19 原始文件须与 verifier 保存的摘要一致，汇总写出前再次复核已保留文件；验证后修改或删除文件不能保留 `PASS`。

这是 M43 的证据收口工具，不修改当前正式发布工作流的门禁政策。M20 七天 scheduled 仍是发布流程中的非阻断观察；在 M43 中继续独立显示其未完成状态。

## 入口

```powershell
pwsh -NoProfile -File eng/verify-m43-release-evidence.ps1 `
  -ManifestPath eng/m43-release-evidence.template.json `
  -CommitSha (git rev-parse HEAD) -Version 4.0.0 `
  -OutputPath artifacts/m43-release-evidence/report.json -AllowNotReady
```

模板明确延后全部现场证据，结果为 `DEFERRED`，不表示曾经执行。缺项、非法清单、报告损坏、身份错配或 verifier 失败产生 `NOT_READY`。任何一项 `NOT_READY` 优先于 `DEFERRED`；八项均为 `PASS` 才能汇总为 `PASS`。不带 `-AllowNotReady` 时，非 `PASS` 在保存报告后抛出错误。

清单 `schemaVersion` 固定为 `1`，大小最多 1 MiB，`gates` 最多八项，每个 ID 唯一。汇总读取的单个 JSON 报告最多 16 MiB。每项使用 `mode: "deferred"` 和非空 `reason`，或使用 `mode: "verify"`。所有原始输入路径相对于清单所在目录解析；输入也可以是绝对路径。身份通过命令行的完整 `CommitSha`、`Version` 和 `Repository` 绑定。输出目录必须独立，不能包含原始输入或位于 bundle 内。路径的现存组件不得使用符号链接、目录联接或硬链接；预检在写出任何失败报告前执行。Windows 按路径大小写不敏感规则比较，Linux 按大小写敏感规则比较；最终报告刷盘后原子替换。

## 范围

| ID | 实际 verifier / 所需字段 | 证据边界 |
|---|---|---|
| `candidate-workflows` | `eng/verify-release-readiness.ps1`；无附加字段 | 联网读取候选的十二类工作流、AOT/发布 artifact 和原始 light/full Parity，绑定提交和版本。 |
| `m19-capacity` | `verify-m19-capacity-bundle.ps1`；`bundleRoot`、`artifactUrl`、`targetHardwareId`、`targetHardwareContract` | 重新校验四档原始报告、硬件、checkout 和 artifact 清单；CI 仓库、候选 SHA 和 GitHub run URL 必须与命令行身份一致，继承原 verifier 的 attestation 局限。 |
| `m20-nightly` | `verify-parity-nightly-evidence.ps1`；无附加字段 | 联网读取连续七个 UTC 日期的 scheduled light/full，不能用 fixture 或手动复验替代；窗口内提交可不同于当前候选。 |
| `m25-capacity` | `verify-document-soak-evidence.ps1`；`reports` 两个路径、`targetHardwareId` | 必须分别为 million/ten-million，同候选提交、同硬件。当前 verifier 缺外部 attestation，合法报告仍为 `DEFERRED`。 |
| `m27-quality-cost` | 当前只接受显式延期 | Copilot 场景报告不足以证明全部真实模型质量、成本、回滚及双网合同。 |
| `m29-installation` | 当前只接受显式延期 | bundle/宿主单元测试不能代替干净目标机安装、升级与卸载。 |
| `m40-graph-production` | Release Benchmarks `--m40-production-gate --manifest`；`manifestPath` | 干净 HEAD 与候选提交一致后，先 `dotnet build --no-incremental /warnaserror`，成功才运行严格 evaluator 重算并回放原始证据；运行后再次检查 HEAD/工作树，必须获得 Production `PASS`。 |
| `m41-m42-performance` | 当前只接受显式延期 | 局部 SQL/KV 正确性与开发机短测不能代替固定双架构、统一语料及 168 小时证据。 |

例如启用 M19 的清单项：

```json
{
  "id": "m19-capacity",
  "mode": "verify",
  "bundleRoot": "../artifacts/m19-fixed-target",
  "artifactUrl": "https://github.com/IoTSharp/SonnetDB/actions/runs/123456",
  "targetHardwareId": "fixed-target-id",
  "targetHardwareContract": "M19-#125-frozen-target-v1"
}
```

`artifactUrl` 沿用 M19 verifier 的精确 run URL 合同，必须等于 checkout 的 `ci.runUrl`；不能使用另一个仓库、运行或 artifact 页面替代。

`PASS` 保留原 verifier 的证明边界。路径和 SHA-256 用于对账，不能独立证明机器真实性、protected environment 权限或报告未被伪造。30 项合同测试包含真实 M19 verifier 的合成 bundle、验证后修改/删除、最终输出与 verifier 子目录的路径别名拒绝及 M40 构建调用顺序；M40 调度测试使用 mock 命令，不计真实 Graph Production 证据。模板和合同测试均只计工具本地验收，不计上述任何现场门禁。
