# 2026-09-26 本地与远端分支合并核查

用户授权将开放 PR 及本地、远端尚未合并的开发分支全部并入 `main`。核查起点为 `4e47d1a0`（PR #203，Issue #197 安装包合同）；工作区与现存的其他 worktree 均无未提交变更。

## 开放 PR

| PR | 变更 | 合并提交 |
|---|---|---|
| [#204](https://github.com/IoTSharp/SonnetDB/pull/204) | CodeQL Action 4.38.0 → 4.38.1 | `02fd2a45` |
| [#205](https://github.com/IoTSharp/SonnetDB/pull/205) | OpenTelemetry.Exporter.Console 1.19.0 → 1.19.1 | `c85cf6f1` |
| [#206](https://github.com/IoTSharp/SonnetDB/pull/206) | OpenTelemetry.Exporter.InMemory 1.19.0 → 1.19.1 | `d551762c` |
| [#207](https://github.com/IoTSharp/SonnetDB/pull/207) | OpenTelemetry.Exporter.OpenTelemetryProtocol 1.19.0 → 1.19.1 | `64d6ec31` |
| [#208](https://github.com/IoTSharp/SonnetDB/pull/208) | OpenTelemetry.Extensions.Hosting 1.19.0 → 1.19.1 | `884272c2` |

每个 PR 均补充 `[Unreleased]` CHANGELOG 与仓库要求的说明，并使用预期 head SHA 调用 GitHub 合并接口。#207 的相邻依赖行冲突保留 Console 和 OpenTelemetryProtocol 的两项 1.19.1 更新。

## 历史分支

24 个本地开发分支以及两个额外远端分支仍缺少祖先关系，但其功能已通过 cherry-pick、集中集成或 squash 进入主线。逐个检查原始提交、主线对应提交、三方合并结果和冲突内容后，使用普通 merge 补齐祖先关系；冲突保留主线的后续实现，删除自动合并引入的重复或已淘汰片段。

下列 26 个合并提交的完整 Git tree 均与依赖更新后的 `884272c2` 相同，未改变代码、测试、配置或已有文档内容。远端与本地同 SHA 的同名分支只需合并一次。

| 分支 | 原 head | 已在主线的对应实现 | 历史合并提交 |
|---|---|---|---|
| `codex/issue-171-ctecols` | `992e9662` | e0cc749e、e6417a97 | `597aead8` |
| `codex/issue-172-window` | `7a4e13df` | dc920ed4 | `16d5faee` |
| `codex/issue-178-completion` | `47223f83` | a9dc71d0 | `2306b0c1` |
| `codex/issue-184-197-release-prep` | `998ee62f` | ee2f095f、314e6744、6d23c925 | `fe5ba6f8` |
| `codex/issue-184-remote-tx` | `c38a1f1d` | 7042b6f5 | `5c70bd60` |
| `codex/issue-184-upsert` | `dad11022` | b8beed14；7042b6f5 已将旧的拒绝合同改为服务端事务支持 | `b29b441a` |
| `codex/issue-186-closure-record` | `c1551e96` | 63009635 | `a1ed7e6a` |
| `codex/issue-186-frame-vector` | `f0802f5d` | b8ccc366 | `70a865ca` |
| `codex/issue-186-vector-update` | `2c16c872` | e3d185cf（PR #201，squash） | `652dcced` |
| `codex/issue-187-completion` | `3b6a422e` | eb54af54 | `80e34d1e` |
| `codex/issue-189-peak-memory` | `5632274f` | bc814536 | `53ae245d` |
| `codex/issue-189-recursive` | `90b3890a` | b8beed14；2cfa98fa、bc814536、ef4a57c4 后续补齐预算 | `51b8821b` |
| `codex/issue-189-resource-boundary` | `7c4be61f` | 2cfa98fa | `88e16f75` |
| `codex/issue-191-update-join` | `d91c2304` | b8beed14；d6212ad7 后续补齐索引路径 | `bfac3fdb` |
| `codex/issue-194-returning` | `a9864f05` | b8beed14；2ba84b8d 后续补齐返回列合同 | `4c1d7800` |
| `codex/issue-195-source-budget` | `120be882` | ef4a57c4 | `b83d56a4` |
| `codex/issue-197-release-review` | `12167310` | d23772a7 | `ffae3a32` |
| `codex/issue-198-frame-compat` | `6675e91c` | 0d3aa1f5 | `c07e1d09` |
| `codex/issue-198-overflow` | `81e01485` | ba76e6f6 | `c9bdeb03` |
| `codex/issue-91-offline-studio` | `0d160920` | 58e21217、a8f6c936 | `f59f9d1b` |
| `codex/m36-311-client-contracts` | `b5e12a25` | 95ef48ad | `6d778cf0` |
| `codex/m36-314-bounded-write-admission` | `9652f7a1` | d27d9dc8 | `da31a474` |
| `codex/m36-315-timeseries-preflight` | `b3ab14ed` | 99250bab | `78df9e4f` |
| `codex/m36-321-vector-lifecycle` | `99c4cd6b` | 280ebd3c | `fd40a470` |
| `origin/dependabot/nuget/OpenTelemetry.Exporter.Console-1.19.0` | `af72d185` | c85cf6f1（PR #205，同一版本更新） | `4c24bbe4` |
| `origin/fix/issue-184-commit-readback` | `8091faf1` | 7ce5aef8（PR #200，squash） | `2050d99d` |

## 冲突决策

- #184：保留 PR #200 的提交终态回读恢复；保留主线 `RemoteTransaction_UpsertReturning_AppliesWithEarlierWrite`，不恢复旧的“事务 UPSERT RETURNING 必须拒绝”测试。同名条件更新测试已经存在。
- #189：保留 `SqlRowRetentionBudget` 统一预算及保守字符串估算；不恢复已被替代的 `RecursiveCteBranchBudget`，不重复追加旧版 SQL 参考章节。
- #194：UPDATE/DELETE RETURNING 的复合键、回滚、NDJSON 和 Frame 只读测试已在当前文件其他位置存在，不追加第二份同名方法。
- #195：保留 `1f160ada` 删除重复 `sourceBudget.Retain(row)` 的修复及其回归，防止源行再次被双重收费。
- #197：保留 PR #203 增加的 `insert-returning` / `insert-legacy` 场景、精确 NuGet 版本约束和编译器设置。
- #178、#186、#198：保留较新的集合运算测试、VECTOR UPDATE 与 Frame 参数合同，保留合入后的审计状态。
- M36：保留写入许可直到已接收批次完成的后续修复、显式 loopback Kestrel 测试和已更新验证记录；不追加重复 CHANGELOG。

## 验证与边界

- 历史合并逐次执行 tree 相等断言；最终分支祖先核查只剩 `origin/parity-results`。
- CodeQL workflow 的 actionlint 检查通过；合并差异的 `git diff --check` 通过。
- 合并后的 Release 构建与 OpenTelemetry/Prometheus 定向测试通过：5/5、0 跳过，覆盖 provider 注册、Core 指标导出、Prometheus 端点及端到端追踪；日志无编译警告或错误。命令：`dotnet test tests/SonnetDB.Tests/SonnetDB.Tests.csproj --configuration Release --filter "FullyQualifiedName~Observability|FullyQualifiedName~PrometheusEndpointTests"`。本地原始结果保存在忽略目录 `artifacts/branch-integration/test-results/observability.trx`。
- 原五个依赖提交的 CodeQL、三平台 NativeAOT 与三平台连接器检查通过。完整 CI 仍有失败，不能写为全部通过：格式、EF HTTP/2 路径断言、Linux CDC 独占锁失败；#208 Windows 还出现 `GraphProductionGateTests` 的 `completion.state` 文件被占用失败。
- 原失败检查属于依赖原始提交的证据；追加 CHANGELOG、冲突解决及主线合并后触发的 CI 是独立运行，不以旧结果替代。
- `parity-results` 无共同祖先，是 `.github/workflows/parity.yml` 用 `checkout --orphan` 并强制更新的结果发布分支，继续独立保存。将其合并会把运行结果混入源码根目录，破坏既定分工。
- 本次未创建发布 tag，不代表 4.0.0 已正式发布；保留现有开发分支和其他 worktree。
