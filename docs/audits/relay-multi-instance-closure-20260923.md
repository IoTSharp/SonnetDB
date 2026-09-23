# M27 #340 ServerRelay 多实例代码闭环（2026-09-23）

本次交付关闭了 ServerRelay 的本地多实例代码残余：共享 journal 的单执行者租约、跨实例活跃事件跟随、owner 失效后的稳定失败终态和生产配置接线。它关闭的是可执行代码与本机产品入口范围，不把真实 IdP、部署网络、真实模型质量或 provider 故障后的透明续跑写成完成。

## 交付范围

- `CopilotServerRelayRunStore` 以 journal 锁合并读取/创建，使用每个 run 的独占 lease 保证同一 owner/runId 只有一个 provider/tool 执行者。
- 其他 Server 实例可以从已确认 cursor 继续读取活跃事件；follower 只能订阅，取消只结束自身连接，不会取消 owner 或追加事件。
- owner 正常完成后重放既有 `final`/`done`；TTL、Dispose 或进程丢失则封闭唯一 `error`/`done`，后续重放保留原 sequence/payload，不重复 provider/tool。
- journal 损坏、重复身份、超限、截断事件和无法合并的快照 fail closed；dispose、取消回调、follower/replay trim 和临时文件都有界回收。
- `SonnetDBServer:Copilot:ServerRelayJournalPath` 接受规范化绝对路径；默认仍是 `<DataRoot>/.system/copilot-relay-journal.json`。共享该文件不共享数据库或会话目录，各实例必须使用独立 `DataRoot`。

## 验证证据

Server Release 构建使用 PowerShell 7、`--artifacts-path C:\Users\mysti\AppData\Local\Temp\sonnetdb-roadmap-20260923-server`、`-m:1 --disable-build-servers /warnaserror`，结果为 0 warning/0 error。定向回归包含 `CopilotServerRelayMultiInstanceTests`、`CopilotServerRelayMultiHostTests`、`CopilotChatEndpointTests`、`CopilotServerRelayContractTests`、`CopilotInfrastructureTests` 和 `ServerOptionsTests`，通过 **114/114**，失败 0，跳过 0；TRX 为 `test-results-2/relay-tests-2.trx`。

真实本机产品入口 smoke 使用两个独立 OS Server 进程、两个独立 `DataRoot` 和同一个显式 journal：

```text
tests/SonnetDB.Tests/Copilot/scripts/test-m27-server-relay-multi-instance.ps1
Server DLL SHA-256: 4FCF95A8E18135E78558FEF0A919C66CE685503F6F8214FEF063AEA708109BCC
script SHA-256:     426456B1BCC2255DC65E3870540CC351EF3173F6E53A69DD3C512A6405056058
status=PASS_LOCAL_ONLY
liveOwnerFollowing=PASS
hardKillFailureSeal=PASS
stableFailureReplay=PASS
providerCalls=6, plannerCalls=4, answerCalls=2, toolPairsPerRun=1
cleanup=PASS, cleanupErrors=[]
```

原始本机 JSON 证据已保存到 `docs/audits/relay-multi-instance-evidence-20260923/`，包含 report、provider 状态、live owner/follower 事件和 hard-kill 后的 owner/follower/replay 事件。报告记录两个 Server PID、创建时间、完整命令行、共享 journal 路径和独立数据目录；进程树已全部回收。

## 2026-09-24 最终复验

最终审查补齐了纯空白数据库名称与 run/tombstone 跨集合身份冲突的拒绝，并保留未选库控制面对话的合法空字符串 binding。另修复 journal 锁超时后的 lease/活动槽位泄漏、未持久化终态覆写，以及 Dispose 取消回调重入创建新 owner 的问题。

最终 Release/AOT 分析构建使用 `--artifacts-path` 将各项目输出隔离到 C 盘，以绕过 D 盘空间不足；未压制警告。上述六组 relay/configuration 测试最终 **119/119** 通过、0 跳过；真实锁竞争、空数据库跨实例重放、Dispose 重入及损坏 journal 均有回归。原始 [Server TRX](roadmap-closure-evidence-20260924/server-first-pass.trx)还包含其他 SQL 测试，整体首次结果为 185 通过、12 个新预览 fixture 初始化失败；这 12 项的测试建表语法单独修复后 [12/12 通过](roadmap-closure-evidence-20260924/sql-preview.trx)，不得把首次整轮记为全绿。全部计数见[统一记录](roadmap-closure-evidence-20260924/validation.json)。

最新 DLL 又执行了一次双独立进程 smoke，live follow、hard-kill、稳定失败重放与 cleanup 全部通过；provider 仍为 6 次（planner 4、answer 2）。最新 [原始报告](roadmap-closure-evidence-20260924/relay-smoke/report.json)的 Server SHA-256 为 `AB2F34D227B96AB223900337A4AC0197122CB65679526C5FBCD70BD9FE8E0B20`，脚本 SHA-256 不变。该报告替代上方初次运行的二进制作为最终源码 smoke 证据，仍为 `PASS_LOCAL_ONLY`。

## 边界

`IChatProvider` 仍只有完整 `CompleteAsync`，云端 gateway 也没有 provider continuation/lease API。因此 owner 崩溃后本实现 fail closed 并稳定重放错误，不能透明恢复 provider 或工具执行；没有新增跨实例取消转移 API。真实 IdP、双网、公网 CSP/CORS、真实模型质量/成本、共享文件系统跨机器锁语义、安装和长稳门禁继续按 M27 真机待办执行。
