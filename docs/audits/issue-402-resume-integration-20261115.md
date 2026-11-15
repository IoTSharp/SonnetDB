# Issue #402：离线恢复准入接线

日期：2026-11-15（Asia/Shanghai）。基线 `main@fa96b1a3e41f4fc712f2bcdf9ba07276605fb200`。本轮只拥有恢复脚本、其直接测试以及本审计文件；没有修改 `CHANGELOG.md`、`HANDOFF.md`、`Directory.Packages.props` 或任何 GitHub workflow。

## 已交付

`eng/recover-tagged-release.ps1` 的 `-Resume` 现在必须接收 `-RecoveryEvidencePath`（别名 `-OfflineEvidencePath`、`-PublicationEvidencePath`）。恢复脚本先读取大小有界、严格 UTF-8 的本地 evidence bundle，核对：

- 精确 repository/tag/version/commit 身份；
- 独立批准 manifest 的 SHA-256、精确 run/attempt/artifact、ZIP digest 和 allowlist 文件；
- current-main 与 frozen-tag readiness policy 的来源、policy commit、十包清单；
- NuGet 有效 payload 对账和 Release asset id/name/size/digest 对账。

只允许 `ALREADY_PRESENT_SAME_PAYLOAD`、`MISSING_CONFIRMED` 或 `RECONCILED_REQUIRES_WRITE_FENCE` 进入恢复。`UNKNOWN_*`、`BLOCKED_*`、未确认 404、授权/传输/限流/远端错误和未知写入均 fail closed；不会自动重试未知写入。

`-Resume -Offline` 只返回 `OFFLINE_ADMISSION_VERIFIED` 计划，明确 `externalWriteAuthorized=false`，不会解析或调用 GitHub CLI，也不会 dispatch、rerun、创建/修改 Release、上传 NuGet 或修改 tag。正常 `-Resume` 的每个 workflow dispatch/rerun 还会在写入前重新执行离线对账并通过 `Invoke-TaggedPublicationWriteFence` 重新解析 tag；写入异常返回 `UNKNOWN_WRITE_PENDING` 后停止且不重试。

证据 bundle 中的相对 manifest/artifact 路径按 bundle 所在目录解析，并拒绝目录、文件或 reparse point 类型漂移。没有 evidence 的 `-Resume` 直接拒绝，Plan 模式仍保持只读且不授权外部写入。

## 验证

在 PowerShell 7.6.10 下执行：

- `eng/test-recover-tagged-release.ps1`：PASS。覆盖 parser/计划身份、离线有效 evidence、`-Resume -Offline` mock 集成、无 gh/dispatch/write、未知 NuGet 写入拒绝、缺 evidence 拒绝；临时 fixture 目录按唯一标记精确清理。
- `eng/test-tag-publication-policy.ps1`：13/13 PASS。
- `eng/test-tag-publication-reconciliation.ps1`：69/69 PASS，所有 Action 仍为本地 mock callback。
- 独立 PowerShell parser check：PASS。

本轮没有调用真实 GitHub/Release/NuGet 写入，没有 dispatch、rerun、上传、发布、tag 变更、提交、推送或 issue 状态变更。真实候选恢复、远端 CI、公开读回、签名信任和新消费者仍为 `NOT_RUN`，不能据此关闭 #402。
