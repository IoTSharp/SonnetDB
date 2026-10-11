# WB84：VS Code preflight helper 观测合同（2026-10-09）

本片只核对 WB83 的冻结源码与原始失败证据，提出下一片的观测合同；没有修改 runner、Host、测试或产品代码，没有重跑 actual、Node、TS 或 .NET build。WB83 原始结果继续是 `FAIL / preflight / helper_deadline`，Server、Extension Host 与 API 旅程尚未建立。本会话原为 2/5，WB84 由根验收和闭合后计 3/5。

`run-query-host-real.mjs:103` 的入口顺序是初始 snapshot、runner 身份与父链接受、audit(first)、listeningPorts，再创建 runtime，之后才启动 Server。snapshot 复用 helper 的既有 CIM 快照；ports helper 同样先执行该 wrapper 的 CIM、lookup 和父链，再执行端口查询。CIM 的 `OperationTimeoutSec 3`、lookup 的 3 秒和父链的 2 秒各有自己的边界，不是 helper 总计时器。

`helper():603` 的实际 Promise.race 计时器为 **6,000ms**，在 spawn、stdin 与 stream 设置后启动。`helperStarts.startedAtUtc` 在 spawn 后记录，不能当作 timer arm 时间。run.json 的 `commandMilliseconds=20,000` 是另一项命令预算；Host `query-real.ts:162/355/372` 的 activation/public command 使用 20 秒。这不证明 preflight helper 有 20 秒 timer，也不能据此宣称 timeout 缺陷。通用 helper 的 signal abort 回调沿用同一个 deadline 错误消息；初始 listeningPorts 调用没有传 signal。

原始 ledger 中 PID13980 的 snapshot helper 之后，runner 于 09:46:40.452Z 接受，audit 的最后一项接受在 09:46:40.459Z。随后 PID72608 的第一个 helper launch 为 09:46:40.662Z；其接受的完整身份 created=09:46:40.463434Z，command body 含 Get-NetTCPConnection，身份事件为 09:46:42.888Z。下一项 cleanup snapshot helper 于 09:46:46.684Z 启动，两个 start 相差 6.022 秒。源码顺序、完整身份中的命令和这个时序支持把失败的 preflight wait 关联到初始 listeningPorts helper 边界；这是源码与证据的关联推断。它没有定位 Get-NetTCPConnection、JSON 序列化、PowerShell 退出、pipe close 或 timer 调度中哪一项阻塞，也不证明端口查询耗时 6.022 秒。wrapper 身份输出已被接受，只证明该输出边界被到达，不能证明 body、terminal 或解析成功。WB83 admission 中 CPU CIM 的 3 秒失败保持独立，原因未知，不能移作 actual helper 的根因。

原始记录还在 09:47:05.264Z 再次出现 PID72608；该后项 identityRecorded=false。前项 stdout 为 4,825 字节、complete=false，后项为 4,829 字节、complete=true。没有 immutable callId、每次调用的 close/timer 时间和后项完整 tuple，禁止只按 PID 合并，也不由 replacement_preserved 推断复用原因或变化字段。最终 closed=true 不能升级前项 wait 为成功。原始五个 cleanup/output flags 均为 false；final remaining/refused、root/passed、audit/passed、三轮/四次 stop/零 stopFailures/剩余一项分别保留。四条完整 stop 观测（三 already_exited、一 stopped）是 WB82 已实现诊断被消费的证据，不代表整体 cleanup 成功。

下一片建议先做 **JS-only 的逐次调用观测**。以 runId+既有 helper ordinal 冻结 immutable callId，记录固定 operation、preflight/active/cleanup phase、既有 accepted tuple 引用和 Node 单调 elapsed 的 entry/spawn/identity/timer arm/timer callback/signal callback/close/terminal/parse/finally 边界。stream summary 和原有 close listener 要引用各自 callId；缺失、重复 PID、未接受 tuple、invalid/overflow/观察器异常保持 unknown。原错误、返回、断言顺序、6 秒 timer、signal listener、CIM/snapshot/port/stop 次数及最终门禁都不改变。额外观测仅进入既有 process-events，最多 112 条、每条 1KiB、累计 64KiB，并继续受原文件/字节上限约束；超限不取得身份或停止权限。第一片不改 PowerShell stdout/stderr producer 或 JSON parser，避免额外 checkpoint 行改变原返回协议。内部 PowerShell 阻塞点继续 unknown；如仍需要定位，再冻结独立的 producer/解析兼容合同。

冻结测试已有 stop refusal/call-order、terminal assertion ordering、非法 marker/secret、throwing observer、384 上限和三项最终门禁（`query-host-evidence.test.mjs:59/83/96/141/182/255/284`），本片没有运行或重包。下一片只补逐次 callId、重复 PID、phase、timer/abort 来源与观察失败的行为证据，并证明原调用/权限/预算不增加；不能把这些测试计成真实宿主完成。

19 个输入最初完整读取和全 hash 通过。闭合前 18 个不变输入 hash 仍通过；HANDOFF 的原 507,707 字节前缀 SHA仍为 `5D31581B68787B5F1B315822AF711648274FCCEFE2B966D95B17C9DCAC542826`，工作文件有另会话新增的 2,680 字节，已保护，不宣称 19 个当前全文件 hash 都不变。额外只读 Host TS SHA 为 `980EB1EC333F9C2FAEE5DA0A2B5293A0A3DABFDBFF54CC1B2F7655E4D8DE8AA8`。代理没有创建长子进程、服务、下载或临时对象；早期三个短 shell 的完整结构化身份记录存在缺口，失败 accounting 也不作为源码或 actual 失败，不宣称全会话进程自由。根负责共享文档、原级别 restore/format、独立复核、提交与已知身份核验。

根冻结下一实施边界：仅三个命名JS脚本，最多16新顶层测试/48明确场景，一份Node测试文件120秒一次，至多一次因新失败的必要修复重跑；三命名JS语法各30秒，0actual/TS/新.NET产品build。下一片先另冻当前HEAD/源码SHA与45分钟总墙钟，最多32命名输入/10搜索/120结果/24短shell，count+wall/cancel/progress/backoff同时约束。112项是聚合逐调用记录、每项嵌套固定checkpoint，单项1KiB/总64KiB/投影50ms，原helper112序号与6秒timer/权限/预算/门禁保持。PS producer/stdout/stderr/parser字节保持；内部阻塞unknown，任何进一步PS观察需另冻兼容合同。此合同尚未实施。

本片根只集成HANDOFF/ROADMAP/CHANGELOG/queue/validation/本报告六文档。完整restore与原CI Format、工作区警告、精确树/独审/本地commit/fresh-post已记录身份退出及本会话3/5以artifacts/wb84-vscode-preflight-contract-20261009收据/Git为准，不预填未取得PASS。private HANDOFF严格HEAD blob+本片自有段，完整工作prefix与所有foreign追加保护。WB83原FAIL/五false/null/重复PID关联缺口不升级。本片0源码变更/actual/Node/TS/产品build；same workbench ACTIVE30分钟、本chat，当前不滚动。M47七导航/九模型/Graph Beta/MQ数据库identity与instance .system/mq保持；三宿主/真实Server/OS文件/安装/完整ExtensionHost/AOT/硬件/长稳/发行物分列。

```json
{
  "schema": "sonnetdb.wb84.next-observation-contract-candidate.v1",
  "status": "root_frozen_not_implemented",
  "task": "WB84",
  "baselineHead": "8cd20c5d2639a3305af44e0fab3e3d9b4c78bee5",
  "scope": "JS-only per-invocation observation of existing helper boundaries; PowerShell producer/output and authority paths remain byte-for-byte unchanged.",
  "proposedFiles": [
    "extensions/sonnetdb-vscode/scripts/run-query-host-real.mjs",
    "extensions/sonnetdb-vscode/scripts/query-host-evidence.mjs",
    "extensions/sonnetdb-vscode/scripts/query-host-evidence.test.mjs"
  ],
  "excluded": [
    "product/Host TS changes",
    "PowerShell checkpoint producer or parser changes",
    "new CIM/snapshot/port operations",
    "new retries, waits, deadlines, stop/identity permissions",
    "actual/TS/build rerun",
    "source-to-old-Server equivalence",
    "WB82 stop diagnostics duplication"
  ],
  "correlation": {
    "callId": "immutable runId + existing helper ordinal (1..112), allocated once before spawn; never keyed by PID alone",
    "operationEnum": [
      "process_snapshot",
      "reserved_ports",
      "owned_stop",
      "unknown"
    ],
    "phaseEnum": [
      "preflight",
      "active",
      "cleanup",
      "unknown"
    ],
    "phase": "capture helper entry phase; cleanup separately labelled even when preserved result.stoppedAtStage remains preflight",
    "acceptedHelperRef": "only exact pid+created reference to already accepted complete authority tuple; a spawned PID is diagnostic-only and cannot be admitted by observation",
    "streamBinding": "capture callId in each helper closure; per-call output summaries and close event reference it; preserve PID and existing summary fields",
    "unknown": "missing, invalid, ambiguous or overflow association stays unknown; do not infer a replacement/PID reuse or bind a later invocation to an earlier accepted PID"
  },
  "timing": {
    "clock": "monotonic Node elapsed offsets from helper entry; UTC labels retained for cross-record readability, never used as cross-runtime duration proof",
    "checkpoints": [
      "helper_entry",
      "spawn_return",
      "identity_accepted",
      "timer_armed",
      "timer_callback",
      "signal_callback",
      "close_observed",
      "terminal_verified",
      "result_parsed",
      "finally_entered"
    ],
    "waitBudgetMilliseconds": 6000,
    "hostCommandBudgetMilliseconds": 20000,
    "rejectionEnum": [
      "timer_deadline",
      "signal_abort",
      "child_error",
      "unknown"
    ],
    "rejection": "record callback provenance alongside the unchanged original error object/message; no Error-message inference and no change to listener/timer registration or authority assertion order",
    "close": "a later original close listener may add observation; no new await/retry/snapshot or claim that closed=true means the earlier wait succeeded"
  },
  "bounds": {
    "helperOrdinalMaximum": 112,
    "recordsMaximum": 112,
    "recordBytesMaximum": 1024,
    "totalDiagnosticBytesMaximum": 65536,
    "projectionMillisecondsMaximum": 50,
    "payloadLocation": "bounded additional observation in existing process-events.json; preserve existing24 files/512KiB per file/8MiB total and original4MiB helper stream cap",
    "overflow": "unknown observation; cannot grant identity/stop authority or alter original returns/exceptions"
  },
  "acceptance": [
    "injectable fake helper cases prove snapshot vs ports, preflight vs cleanup, repeated PID across two callIds, accepted vs unaccepted tuple, and timing checkpoints remain separate",
    "prove timer_callback vs signal_callback are distinct observations while original helper_deadline error and call order remain unchanged",
    "missing/throwing/accessor/invalid/overflow observation stays unknown with no secret/raw output retention",
    "existing WB82 stop boundary/terminal ordering/secret/overflow/final cleanup gate tests remain intact; do not repackage them as new behavior",
    "no additional helper/CIM/snapshot/port/retry/stop operations and no timer, listener, return, exception, cleanup gate or authority expansion",
    "syntax and meaningful new observation tests only in a separately frozen next implementation task; current WB84 has zero such executions"
  ],
  "unresolved": "Internal blocking operation (port query, serialization, PowerShell termination, stream close, timer scheduling) remains unknown after JS-only observation; any PowerShell checkpoints need a separate justified contract.",
  "executionBudget": {
    "newTopLevelTestsMaximum": 16,
    "newExplicitScenariosMaximum": 48,
    "nodeTestFilesMaximum": 1,
    "nodeSecondsMaximum": 120,
    "nodeInitialRunsMaximum": 1,
    "necessaryNewFailureRepairRerunsMaximum": 1,
    "namedSyntaxFilesMaximum": 3,
    "syntaxSecondsPerFileMaximum": 30,
    "actual": 0,
    "tsCompiles": 0,
    "newDotnetBuilds": 0,
    "totalSliceMinutesMaximum": 45,
    "namedInputsMaximum": 32,
    "searchesMaximum": 10,
    "searchResultsMaximum": 120,
    "shortShellCallsMaximum": 24
  },
  "rootFreeze": {
    "candidateSha256": "E7966A3F659962327C4CF71352C09F1CF584C578991EE43BB70D56929FFC21F9",
    "independentReviewSha256": "3F700E5501CC0A2F81FBDEBC225746E24726DDA44AC64D5D6CB01039976367D0",
    "mustRefreezeCurrentHeadAndSourceShaBeforeImplementation": true,
    "implemented": false
  }
}
```


WB84门禁窗口失败闭片（2026-10-09；本会话3/5）：完整restore exit0/naturalExit=true/48.8997354秒且44身份finally absent；Format于10:44:04Z被原600+60秒完整保留时窗守卫拒绝启动，NOT_RUN，未降低门禁/延长10:55Z/新增commit。六自有stage已精确撤回，原proposal/a2a028d9树与审查commitAllowed=false保留；新pending六文档字节、private HEAD+owned HANDOFF及已记录身份fresh退出见artifacts/wb84-vscode-preflight-contract-20261009。提前交接后新本地会话从0/5只接续完整门禁/本地集成，复用已完成调查/独审，不重调查或跑actual/Node/TS/产品build；同一workbench保持ACTIVE30分钟，release和真实目标ID见rollover.json。原WB83FAIL/false/null及全部foreign字节保护；pending变更暂不能提交因为强制Format未运行。
