# WB81 VS Code stop 拒绝：原始证据与下一实现合同

本片仅调查、冻结下一合同；0 源码变更、0 测试、0 actual、0 build。WB57 的唯一 actual 保持 FAIL。它的 `stop-verification` 表示 `stopVerified` 整个回调抛错，不能改写为“Stop-Process 成功后验真失败”。

| 已证事实 | 原文件与位置 | 边界 |
|---|---|---|
| 拒绝 PID 92952，时间 2026-10-07T10:33:11.299Z | process-events.json `auditFailures[13]` | 唯一保留原因 `ownership_or_verifier_failed_process_preserved`，无更细原因 |
| candidate PID 92952 / parent 70704 / created 2026-10-07T10:32:54.1583510Z | `events[14]` 与 acceptedIdentities | 有身份锚定不等于 OS stop 已调用 |
| 3 rounds，16 stop attempts，1 stop failure | cleanup.json `processCleanupDiagnostic.structure` | 15 条 owned-process-stop 事件，PID92952 无成功事件 |
| final remaining/root/audit = refused/passed/refused | cleanup.json `finalChecks` | finalLiveCount=2，remainingCount=1；缺最终身份清单，不能断言 residue 就是92952 |
| 27 helpers 均 closed 与 identityRecorded | process-events.json `helperStarts` | helperCleanupProven 不证明每个 helper 业务结果或退出码成功 |
| helper79028 是失败附近的 stop 脚本 helper | `events[34]`、acceptedIdentities、child-output.json `helperStreams` | 与92952仅按串行执行/时间相关推断；没有持久 operationId 或 stdin目标关联 |
| helper79028 stdout7395B、stderr227B，两份 complete hash | child-output.json | rawOutputRetained=false；不可由 hash 还原文本、exitCode、signal 或拒绝子步骤 |
| 原 cleanup=false，runtimeRemoved=false；ports/helper/output=true | cleanup.json 与 result.json | later rootCurrentRecovery 的62 PID absence、282对象回收、extraStops0是后续时点，不能追认原成功 |

原 actual 目录是 `artifacts/wb57-validation-20261007/query-host-real-c2fe13a9-329e-4036-add6-1638e96c7c4c`。五个关键 payload 的 bytes/SHA 与 manifest 一致，manifest/result SHA 与 terminal-status 一致；未重开全部14个 payload。每个已读输入 SHA 写在 stop-contract.json。

当前 runner SHA `D774DA6A7BE6115E7756267B2F2DCB8DE7AD835E724DCFC6F2941A748D9D80FB`、证据核心 `3C0DD1781653B4532CE310A7A2FA8A173182E149AFAC1FE02FE956DA93E54AB0`、证据测试 `521D2118971E0B00849C9A067C0EA66E35B9EA795B0B9AE07651653C7008AED6` 与 WB57 记录的预先冻结值相等。原 run.json 没有运行时 source hash attestation；此处不声称 source-to-binary 等价。当前 Host TS 已不同于WB57原冻结，且可见 WB58 QueryHistoryObservation、通知与history尝试计数，不重复实现。WB59仅核一个设计收据；既有兼容交付状态沿用根最新队列，本片未重新审查生产parser/panel。

源码绑定：runner 515–609是helper生命周期，600只断言终态，没有保留退出细项；611–652是stopVerified，OS stop在649，成功事件在652。evidence 732–737把所有回调失败收敛为一个标签；747–756是最终三个独立门禁。runner248的helper证明只检查closed和identityRecorded。已有75个顶层测试声明，623–641已覆盖opaque stop异常，656–663已覆盖closed helper的子孙仍计入remaining，不能重复包装为新任务。

下一任务只改现有三个脚本：run-query-host-real.mjs、query-host-evidence.mjs、query-host-evidence.test.mjs。为真实拒绝增加有界stop attempt→candidate/helper ledger关联和固定阶段诊断，覆盖JS锚校验、helper派发/身份/等待/退出/结果解析，以及原PowerShell身份/父链/锚/Stop-Process周围的固定检查点。记录exit分类与returned result分类，不保存原文错误、stdout、stderr、stack或凭据，也不解析任意错误文本。诊断缺失/格式异常/伪造/抛错/超限均为unknown，绝不能改变原return/throw、stop调用次数、校验顺序、audit计数、primaryFailure、最终remaining/root/audit或runtime门禁。不额外快照、等待、重试stop或放宽权限。

验收使用runner实际接入的可注入边界及受控脚本生产器进行模拟；测试中的OS stop必须替换，不可杀真实进程。保留已有测试，最多新增16个顶层测试/48矩阵case，只运行一个命名Node测试文件、120秒墙钟，最多一次基于新失败检查点的必要修复重跑。覆盖所有退出分支、观察器异常、未知枚举、泄露标记、getter异常及次数上限，并断言原执行行为保持。0 actual/0产品build；未来Host实际启动需另冻结合同。该交付仍仅是诊断合同，不会证明WB57旧根因或三宿主完成。

未知项保持未知：PID92952是否到达OS stop、其是否返回；具体是JS校验、helper非零/超时、PS所有权校验、OS调用还是结果解析拒绝；helper原exit/signal/原文；最终残留PID；后续absence原因；原Host API恢复。不能猜瞬态、权限、PID复用，也不从后来absence追认成功。

执行边界：写收据时13次shell、20个明确输入、6次限定rg；无等待/轮询/actual/build/test/长进程/安装/删除/Git变更。初次第一层枚举超过20后触发guard，未递归；一次原始设计收据合并输出超过投影约束并截断，后用小投影补核，未执行其中命令。精确执行偏差和writer身份在JSON中，短shell的全生命周期退出证明不在本子任务声明范围；根需fresh验证writer已退出。最后只保留这两个自有交付文件。

## 根验收、集成与滚动交接

本片从本地56ee66495ef62ab9665015187eb0841f0a0ac9f6接续，是当前会话第5个独立有界任务。WB80本机Studio选择/恢复/查询/正常关闭证据保持；本片没有重跑Native或Extension Host，也没有修改产品/诊断源码。任务冻结08:00Z截止，最多6个受管命令，源调查与工具准备、独立复核、文档验收及提交合计一项。

独立复核见artifacts/wb81-vscode-stop-contract-20261009/independent-review.json（含内嵌notes）；原始输入、逐字段位置和SHA见stop-contract.json。调查者终态复验20个输入未变，并核其收据writer已退出；这不等于完整会话所有短进程生命周期证明。读盘包含完整最新五文档字节，语义重点是当前交接与WB41/WB57–59队列；巨大历史全文语义审计不声明。

根只提交本报告与HANDOFF、ROADMAP、CHANGELOG、work-queue、validation-report共6条路径。HANDOFF严格以HEAD blob加本片LF自有段生成私有候选，完整工作前缀和其它会话尾追加保留；博客/CSDN/OSChina/微博内容不进入本提交。精确stage/diff检查在完整最终树门禁前完成，门禁后再核同一SHA/tree。最终完整dotnet restore SonnetDB.slnx与dotnet format SonnetDB.slnx --verify-no-changes --no-restore --severity warn --exclude extensions/结果见restore-final.result.json、format-final.result.json；不得从命令计划预填通过。新门禁工具只允许micro/restore/format，原WB79 v2的完整身份/fresh拒绝/父链/finally逻辑保留，经独审后才运行。

准确提交哈希、5/5计数、门禁与已观察身份退出见commit-checkpoint.json和post-commit-verification.json。wholeSessionOrphanFreedomProved仍为false，旧WB57 strict cleanup=false及旧Native失败不改写。当前四个受保护源码SHA须在最终集成前后保持；origin/parity-results固定0061d6d78591fb08493f473d3231ca42303faae1。

提交并核验退出后，按用户授权创建D:/source/SonnetDB本地接续聊天，将同一个workbench绑定过去并保持ACTIVE、每30分钟。迁移事实和写入权以本片rollover.json的released/newThreadId为准：新会话从0/5开始，先只读接收，released=true且目标匹配后立即冻结并推进上述三脚本诊断实现；旧会话发布release后停止仓库写入。未发布release前不能声称已经迁移成功或并发写入。

现有M47导航/九模型合同、Graph Beta及MQ database identity/instance .system/mq边界保持。OS文件对话框仍先确认可见/激活条件；VS Code真实history/回收、向导UI/Webview分页/Notebook/LSP、安装、AOT、固定硬件、长期及发行物继续分列。固定PS7、有界数量/墙钟/取消/进度、完整身份树和精确归属清理要求保持；禁止Graphify、广域扫描、未授权安装、push、发布、部署与外部发送，旧拒删Temp/WB40runtime/其它会话/共享缓存/交付物受到保护。
