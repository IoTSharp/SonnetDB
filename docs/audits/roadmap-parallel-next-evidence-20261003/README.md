# M42 本地执行证据

本目录对应 `codex/roadmap-parallel-next-20261003`，完整范围和边界见[本轮报告](../roadmap-parallel-next-20261003.md)。

`run-owned.ps1` 使用结构化进程参数、单 MSBuild worker、两颗逻辑处理器及固定 PowerShell 7/.NET 路径。
每次执行有同名 `.out.log`、`.err.log` 和 `.process.json`；测试另有 `.trx`。
进程记录包括命令、超时/轮询界、当前父链身份、清理动作和最终存活核查。
历史 PID 或父 PID 不可用来认领或终止当前进程。

| 证据前缀 | 含义 |
| --- | --- |
| `toolchain` | .NET 版本；短命令未取得完整 CIM 根身份，不作为进程归属证明 |
| `runner-*-trial` | 执行器的小输入截止试运行；`runner-owned-final-trial` 使用最终执行器，预期退出 124 |
| `release-build` / `release-build-final` | 保留的初次编译失败 |
| `release-build-complete` / `release-build-corrected` | 完整 Release 解决方案构建 |
| `test-build-final` | 最后 fixture 修正后的测试项目构建 |
| `targeted` / `targeted-corrected` | 保留的两次定向失败与 TRX |
| `targeted-final` | 467 项定向回归全部通过 |
| `core-full` | 完整 Core 回归 |
| `managed-journey` / `aot-journey` | 实际 `--budgets` 文件/双模型向量/重开入口 |
| `aot-publish` | 本机 win-x64 NativeAOT 编译，输出位于本轮独占临时目录 |
| `final-restore` / `final-format` | 最终待提交源树的完整 restore 与 CI 同级格式检查 |

最终统计、代码/文档 SHA-256、AOT 二进制摘要及资源核查汇总于 `validation-summary.json`。
`summarize.ps1 -Trial` 先以单记录/单源文件/单 TRX 与当前 launcher 快照试运行，`summary-trial.json` 的范围不是完整资源核查；正式汇总采用一次完整当前进程快照。
失败记录与旧执行器试运行保留原状，通过结果需核对对应记录的 `runnerSha256`。
这些证据属于本地构建、合同测试和有序持久重开，不替代远程 parity、容量、模型质量、长稳或生产门禁。
