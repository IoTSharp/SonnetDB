# Workbench Preview 1 当前执行入口（2026-10-10）

**当前唯一执行路线是根 [ROADMAP：Workbench Preview 1](../../../ROADMAP.md#workbench-preview-1)。** 已完成切片和原路线状态表见 [CHANGELOG归档](../../../CHANGELOG.md#roadmap-completed-archive-2026-10-10-workbench)；本文件下方WB记录保留为原始历史证据，不继续按旧“下一片”或任务次数派单。

首版优先Web Admin与匹配Server的已验收子集；Studio/VS Code未通过各自安装/完整宿主旅程时延期公开下载物。当前 NOT_READY；旧workbench定时 PAUSED，不自动恢复WB98或创建滚动会话。Graph Beta、MQ database逻辑/instance .system/mq物理与单库备份边界保持。

| 顺序 | 根路线交付ID | 下一步与依赖 |
|---|---|---|
| 1 | M47-P01 | **范围冻结完成，产品NOT_READY**：[范围/控制](preview-1-scope.md)、[候选01清单](preview-1-candidate-01.json)、[独立分发](preview-1-distribution.md)。来源c40fa670完整SHA与保留版本4.5.0-preview.1.1绑定，入口控制/组包/上传器尚未实现。 |
| 2 | M47-P02 | **本地完成**：[修复与验证](preview-1-p02-smoke.md)、[候选02](preview-1-candidate-02.json)，来源 `ed4f48242c5d5b4e7e16861504d03cc845146402` / 4.5.0-preview.1.2；307fixture、12宿主、448合同和build通过，25默认skip分列。远端门禁NOT_RUN，原失败/安全事件保留。 |
| 3 | M47-P03 | **R1 Preview专项通过；完整模式回归partial**：[入口/验证报告](preview-1-p03-controls.md)、[范围修订](preview-1-p03-scope-revision.md)。开放SQL/关系/时序子集，七模型专用读取延期；[候选03](preview-1-candidate-03.json)固定`faa03a018c77` / 4.5.0-preview.1.3，产品NOT_READY。 |
| 4 | M47-P04 | P02/P03后：生成当前Web/Server资源包、manifest与hash。 |
| 5 | M47-P05 | P04后：验证实际包的首次部署、用户旅程、停止重开/回退。 |
| 6 | M47-P06 | P01～P05后：预览说明、限制/反馈/回退和必要的限定资产分发切片。 |
| 7 | M47-P07 | P02～P06后：同候选完整提交/CI/发行物/真实旅程门禁放行，不降低现有policy。 |
| 8 | M47-P08 | P07通过并获实际发布授权后：一次发布、独立回读下载/版本/hash和归档。 |

每项验收标准、宿主后续和正式版U02～U09只在根路线图维护。WB80的本机恢复PASS不能代替当前桌面包安装；WB83 preflight失败与WB98独审超时不能推成产品故障。已有dirty诊断、旧失败/UNKNOWN和其它会话内容保留。

## 历史WB记录（旧状态与接续指令不再作为当前执行计划）

# Workbench 持续推进队列

WB84门禁窗口失败闭片（2026-10-09；本会话3/5）：完整restore exit0/naturalExit=true/48.8997354秒且44身份finally absent；Format于10:44:04Z被原600+60秒完整保留时窗守卫拒绝启动，NOT_RUN，未降低门禁/延长10:55Z/新增commit。六自有stage已精确撤回，原proposal/a2a028d9树与审查commitAllowed=false保留；新pending六文档字节、private HEAD+owned HANDOFF及已记录身份fresh退出见artifacts/wb84-vscode-preflight-contract-20261009。提前交接后新本地会话从0/5只接续完整门禁/本地集成，复用已完成调查/独审，不重调查或跑actual/Node/TS/产品build；同一workbench保持ACTIVE30分钟，release和真实目标ID见rollover.json。原WB83FAIL/false/null及全部foreign字节保护；pending变更暂不能提交因为强制Format未运行。

WB84 preflight合同（2026-10-09；本会话第3项）：已核helper6秒与Host命令20秒分立；源码/command/时序支持初始ports helper wait关联推断，内层原因unknown。PID72608前后调用缺immutable关联，原WB83 FAIL/回收false/null不升级。冻结下一JS-only调用ID/phase/timer-vs-abort/close观察，原PS/parser/权限/预算/门禁保持，尚未实施；本片源码/actual/Node/TS/产品build0。六文档完整门禁/本地提交/已记录身份退出与闭合3/5见[报告](wb84-vscode-preflight-contract.md)及本片artifacts，同workbench ACTIVE30分钟/本chat。

WB83独立真实窗口（2026-10-09；本会话第2项）：两源只迁移元数据，逆SHA/独审/微试/语法/TS1通过，35输入冻结，Node0/新.NETbuild0。CPU准入首次原3秒超时保留、同条件唯一复核PASS；actual1在preflight helper_deadline FAIL，0phase/history且Code/Host/API null，原五回收flags false、remaining/root/audit=refused/passed/passed。新stop ledger四记录complete（三already_exited/一stopped）仅为诊断；根later24已记录身份0存活/未知、0新stop/delete不升级旧false。精确8路径/完整门禁/本地提交及2/5以[报告](docs/design/m47-unified-management-workbench/wb83-vscode-host-validation.md)和artifacts/wb83-vscode-host-window-20261009为准；下一片有界核preflight helper合同，不盲重actual，三宿主边界保持。

WB82同片必要重验（2026-10-09）：新restore通过，但首format因托管observe-failed被安全中断，原结果UNKNOWN_INTERRUPTED未通过，细因unknown。51已记录身份fresh无存活/未知、2PID复用保留；失败原收据保留。三源码SHA不变/88复用，准确文档后完整restore-retry+原CI format-retry至多一次，只有两新门禁和全身份审计通过才本地提交；同片总预算8/计划5、09:45Z不扩，仍为本会话第1项，关闭后1/5。实际结果见WB82报告及artifacts/wb82-local-integration-20261009。

WB82本地集成接续（2026-10-09；本会话第1项）：释放严格匹配，三最终源码SHA不变，复用88/88而不重实施/重测。旧08:25Z失败闭片保留；新09:45Z窗口的新工具micro/独审、最终九路径完整restore/原CI format、本地commit及已记录身份fresh退出以artifacts/wb82-local-integration-20261009真实收据/Git为准，未取得完整PASS不得提交。本片合计一项，关闭后本会话1/5；0actual/产品build，三宿主与原未知边界不提升。

WB82闭片（2026-10-09；本会话1/5）：三源独审/唯一Node88/88及完整restore通过，Format未运行、无新commit。08:25Z门禁窗口剩余不足，300秒收紧scope接收已过期，未放行；精确撤销自有stage、index空，57已记录身份0自有存活/0unknown、1PID复用保留。已保存真实失败检查点，提前交接优先新窗口完整restore/原format及本地集成，源码未变复用测试，不重做WB81/80；详细事实见WB82报告/artifacts。

WB82（2026-10-09；收尾后本会话1/5）：WB81释放匹配后实施已审三脚本诊断合同，最多384 attempt仅引用已接受candidate/helper；固定阶段/类别、私有失效及未finish unknown保持原return/throw/顺序/次数/权限/三门禁/runtime。唯一Node88/88（75旧+13新/48新场景）、六受控PS/三语法通过，0actual/build；[报告](wb82-vscode-stop-diagnostics.md)和独立收据记录最终门禁/提交/退出。下一片先核本提交与最新队列，再另冻真实Host窗口或有据缺口，不重做WB81/80或盲加actual。

WB81（2026-10-09；本会话收尾后5/5）：已核WB57唯一stop-verification拒绝PID92952；整个stopVerified回调边界不等于OS stop成功后检查，helper79028仅为附近候选，旧原文/退出状态关联缺失。源/原证据SHA与现有75项覆盖分列，形成[下一三脚本诊断合同](wb81-vscode-stop-contract.md)：固定阶段和candidate/helper关联，不新增snapshot/stop/retry/权限。0源码变更/测试/actual/产品build；5/5收尾后新本地会话从0/5立即实施此合同。

WB80（2026-10-09；本会话收尾后4/5）：新冻结37依赖/11源的唯一Native actual通过A/B→正常关闭→第二桌面被动恢复B→真实B查询→正常关闭；旧Release复用、0build，两close四端口释放/零fallback，独立终态复核另有收据。详见[报告](wb80-native-validation.md)。下一片从尚未闭合的真实缺口选取；OS文件对话框须先证明窗口可见/可激活，VS Code旅程先核最新队列；不重跑WB80已用尽actual，不升级WB77/79失败或整体三宿主状态。

WB79（2026-10-09；本会话收尾后3/5）：以真实SaveAsync磁盘文件验证共享fixture，C#20/20、Node完整80/80；只允许完整一致的已知派生身份对或旧双缺省形态，原未知字段/身份/ack/两desktop/查询/close门禁保持。原包装器FAIL与中止Node不覆盖，最终本地集成见[报告](wb79-serialized-library-contract.md)。下一片可据新合同冻结独立Native窗口，重新核源码/依赖/runtime/资源及预算；不重跑耗尽的WB77、不预称真实恢复完成。

WB78（2026-10-09；本会话闭合后2/5）：WB77收尾已提交`25d274ef`。连接库安全字段类别观察已接入现有磁盘读取/终态，14新合同加60回归全部通过；只报告允许字段名与未知数量，原拒绝及正常退出/回收门禁不变，0actual。后续先基于真实序列化和新观察评审磁盘接受合同，再建立独立真实窗口；不猜旧失败字段、不重跑WB77。完整门禁、提交与边界见[报告](wb78-library-field-observation.md)。

WB77（2026-10-09；闭合后1/5）：released移交后完成wrapper最窄修复/两case/独审/managed micro/语法；CPU首次5秒超时保留，唯一原条件准入复核通过。唯一actual为174.468秒原FAIL，B preparation于firstA前被library字段守卫拒绝，fresh PUT/DOM不代替磁盘确认；selections0/launch1、恢复/查询null、normalExit=false、严格故障cleanup=true。最终本地门禁/owned集成见[报告](wb77-native-continuation.md)及收据；下一片先核真实schema与安全字段名观察，不盲重跑、改未知字段权限或重复已有14/41/38。

WB76（2026-10-09；本会话从1/5接续）：先获取并合并最新 `origin/main`，双方仍为 `5424b666`、0/0。新冻结 `artifacts/wb76-native-actual-20261009`，最多14 managed／1 actual、原900s与90s回收、outer1020s；仅 runner 专属证据入口、条件标签和 .62 文件前置文案。先独审源／工具、micro／语法及新鲜32文件／四端口／资源，再验普通 A→B、两 desktop、恢复 B、真实 B 查询、正常关闭与严格 cleanup。已有 PS14/Node41/guard38不因接续重跑，原失败保持，进度和最终证据见[WB76 报告](wb76-native-actual-window.md)。

WB74（2026-10-08）：修复本地共享 HANDOFF 的 owned 内容守卫，固定完整工作前缀、精确 owned 段、private blob/index tree 与其它提交路径，仅允许未暂存尾追加。继承 WB73 PS14/Node41，不重测、不改六源，0actual/产品build；原 WB73 集成失败保留。新微试、完整门禁与本地提交以 [WB74 报告](wb74-owned-handoff-integration.md) 和独立收据为准；不预填完成。

状态：2026-10-06 三宿主阶段实施中。用户授权持续推进、子智能体独立实施、无冲突并行、任务闭环后本地提交，并确认本轮收尾后新建会话、转移同一每30分钟 heartbeat。任务状态以当前文件、验证记录和提交为准，本文的待办不表示已完成。

### WB73 Native CIM 本地验收（2026-10-08；本会话第2个独立任务，闭合结果见收据）

- 仅验收已有WB72六源，0actual/产品build；PS/Node micro各1/1、修复PS14/14、Node41/41且0skip。首full child-cache失败保留；fake缺失CIM只改bare return，断言及其余五源不变，独立逆向hash/AST0通过。源测试5/5结束，不追加重试。
- 根共享docs/14paths精确owned集成，HANDOFF只拼WB71 staged307508+ownending3963+WB72 own3150+本delta，foreign352979B逐byte保持。最终完整restore/原CI format、cached whitespace、observed identity audit/本地commit/post和独立终审见artifacts/wb73-native-cim-validation-20261008；不预填尚未生成的PASS，不push。
- 旧WB72过期、WB71 shared复用strict失败与WB70 actual/normalexit/cleanupfalse不改写；已观察tuple退出不等于whole-session orphan-freedom/processIntegrity。下一独立窗口冻结新观测下的真实Native准入/单次旅程，先review再fresh资源；不盲重跑WB70，三宿主/OS/安装/ExtensionHost/AOT/硬件/长期/发布另验，Graph Beta。

### WB72 Native CIM 阶段观测（2026-10-08；本新会话第1/5失败检查点）

- 六路径 snapshot-only 四phase/160slots/lookup1及children64/相对单调耗时/100ms观测记账已实现，静态源码/工具独审；WB66 cache/budget、WB67 scalar与原primary/authority/runtime保持，0actual/产品build。
- 初始PS micro exit0却打印PASS s，singleton字符串索引未执行self，语义FAIL不可升级；string[]+default拒绝+executedCases修复后00:57:50Z返回PASS self/query1/1of1。完整Node micro/PS14/Node/restore/format/commit均NOT_RUN。
- root01:25Z窗口过期，02:29:53Z接续时失败关闭；三个保留child退出0，closure observed13/retained3/survivor-reuse-unknown0、0stop/delete；whole-session/processIntegrity/rootClosureTimely/localIntegration/overallfalse。旧WB70/71等失败保持。
- 下一独立WB73只验收和集成已经实现的六路径，新冻结窗口/同源hash、Node micro与完整定向测试后根串行owned候选/最终树完整门禁/本地提交，0actual且不重跑WB70；不是新功能包装，最多5任务规则不变。

### WB71 Native运行时/snapshot合同（2026-10-08；本会话第2/5，0actual）

- 新报告绑定.53 prereq文件与原.62 CDP HTTP version/进程tuple；启动未传明确runtime选择，当前Program到复用binary等价未证。coarse CDP fatal含ownership snapshot，四helper/callsite映射静态推定，不能称HTTP端点不可达或版本导致失败。
- helper整体20s与单CIM OperationTimeout20边界分列；记录13/17/21.537s及cleanup13/17/20.540s，低于160数量上限，无逐查询时序/根因。WB66预算/WB67三标量已有，不重复包装；下一WB72阶段/查询耗时观察候选NOT_IMPLEMENTED，先独立冻结/本地夹具，0新增CIM/权限，不重跑WB70。
- 0sourcechange/actual/产品build/Node重测，七path root集成含有归属六pending与新report，完整restore/CI format/本任务fresh numeric/CAS commit/post/终审以本片收据为准。WB70checkpointVerified/rootClosureTimely与所有旧FAIL保持，三宿主/安装/AOT/硬件/长期/发布分列，Graph Beta；root00:35Z/review00:30Z/12wrapper，同一heartbeat ACTIVE30m。

### WB70 Native真实窗口（2026-10-08；本会话1/5，唯一actual失败检查点）

- WB69十五closed SHA/HEAD3027a1aa与foreign HANDOFF338755B已核，旧actual0/两formatPASS/private whitespaceFAIL/localIntegrationfalse保持；runner仅唯一CR修LF+五metadata，89122B/49FA869E…、inverse normalizedWB69 F730906B…/WB68 A2B76713…，原B准备/freshA与恢复/退出/预算合同保持。
- source/tools独立审阅后fresh资源门禁23:18:38.510Z通过，唯一actual23:19:37.744Z/67.61s/launch1 FAIL在WebView2 loopback CDP；helper96692为CIM13/cache17/21.5372204，随后cleanup10704为13/17/20.540533，exit1/timedOutfalse。A/B选择、重启恢复与B查询未证明，normalExit/cleanupProven原false保留；不推slow CIM或数量耗尽。
- prerequisite WebView2 .53与actual .62分列。outer17+extra59844联合18 fresh gate/current-reuse-related-associated-unknown-changed0/四exclusiveports通过，根仅回收324自有临时对象，新增stop0；原七evidence不改。只读验收null-index首次FAIL及最小修复另记；完整CIM/过程缺口保持overall/processIntegrityfalse。
- 六paths/private owned HANDOFF/whitespace/full restore/原format/strict numeric/本地CAS commit/post/独立review以artifacts/wb70-studio-database-window-20261008收据为准；0build/0Node、actual1/1不重跑。同一heartbeat ACTIVE30m/root23:45Z/14wrapper；下一独立片有界调查actual runtime选择/helper snapshot合同，旧失败与三宿主/安装/AOT/硬件/长期/发布不升级，Graph Beta。

### WB69 Native资源准入（2026-10-08；本会话1/5，0actual失败检查点）

- 继承HEAD3027a1aa/closed十二SHA/own3056B ending，0重复commit/tests；仅runner五metadata迁WB69/actual及18369/18370/55369/9369，inverse exactA2B76713，8EEF源SHA绑定，prepareFirstSelection:true与原恢复/预算/authority保持。
- 23明确依赖/九runtime SHA、WebView2154.0.4258.53、.NET10三framework/Playwright CDP import与四fresh exclusiveports已核；source syntax、tiny micro/AST/metadata inverse通过，0产品build/旧合同重测。独立source/tools/deps局部通过；预检22:23:29.404Z与最终review22:26:35.298Z之间185.894s，180s准入超时5.894s，actual0/native NOT_RUN。原错误/过程gap保留，整体过程完整性false，失败结束计1个任务。
- 六paths根集成/完整restore/原format/精确自有HANDOFF private CAS提交/post/strict numeric与finalreview以本片收据为准，root23:00Z/review22:55Z/max12wrapper。下一独立资源窗口先review source/tools，再fresh gate与单次actual；不重复此窗口/旧同值A，不改旧WB68/67/66/65/64false，三宿主/安装/AOT/硬件/长期/发布仍分列。

### WB68 普通异库准备（2026-10-08；本会话第2/5独立任务，0actual）

- base d841e027；WB67本地提交已在HEAD，旧post numeric70/current1/related0与最终localIntegrationPassed=false保持。本片一次继承观察current2/related1，未知共享MCP/复用PID保留，0stop/0exclusion，不回写旧FAIL。同一workbench ACTIVE30分钟，最多5任务。
- 专属scenario/runner/新test+根五docs共8paths；runner唯一literal prepareFirstSelection:true opt-in，默认同值/unknown拒绝仍由旧tests确认。仅首次A已active且七fresh谓词有效时最多一份普通B控件准备，完整ack/DOM/disk后重新读原A guard/barriers；准备独立于两次journey selection和contract.selected。未知0动作、初始可变更0准备/额外读；原A→B/strict双关闭/relaunch/B恢复/query及预算保持。
- micro1/1、Node60/60（新17+旧43）、三JS syntax与源码29/工具27独立检查通过，零fail/cancel/skip/todo。0actual/产品build/安装；旧A PUT来源unknown，不重跑原实跑。最终freeze/完整restore/CI format/精确8path CAS提交/post/当前numeric退出/独立集成以artifacts/wb68-database-preparation-20261008收据为准，root22:15Z/review22:10Z/14wrapper/2test固定。
- 下一片先核最终状态，再冻结独立native metadata/runtime窗口，复用普通异库准备与三统计观察。三宿主/真实Server/OS文件/安装/ExtensionHost/AOT/硬件/长期/发布分列；原WB64 actual/normalExit/cleanupfalse、WB65overallFAIL、WB66晚审核FAIL和Graph Beta保持。

### WB67 helper三统计保留（2026-10-08；本会话第1独立任务，0actual）

- 基线c7db9130；WB66继承六路径post与精确private回收0commit，独立内容23PASS但收据timeliness FAIL单列；strict首numeric PID碰撞FAIL与唯一v2 69PID/current0/related0/0stop/0exclusion PASS、原pending/NOT_PRODUCED保持。继承不计任务，WB67计本会话1/5，同一workbench ACTIVE30分钟。
- 仅H/R/E+专属新test，root五docs共九路径；success/error同envelope既有cimQueries/cachedPids/elapsedSeconds进入当前record/compact。固定own descriptor拒accessor、missing/非法null unknown、不coerce/造0；0新CIM/协议行/authority，原业务结果/primary错误/全部预算/fresh与cleanup门禁保持。
- 微试1/1、最终Node31/31（新15+旧E16）零fail/cancel/skip/todo，纯PS7精确H输出两夹具与syntax通过；独立源码22PASS。工具首23PASS/1FAIL漏transient PID保留，修正版26PASS/真实Admit micro通过；完整最终tree restore/原format/本地commit/post/strict退出/独立终审见artifacts/wb67-helper-statistics-20261008。0actual/产品build/安装，旧真实失败不升级。
- root21:40Z/14wrapper，原2test冻结保留且只为purePS emission独占amendment至3；scope/原helper budget不扩。下一片先核提交，再冻结有异库前置的真实恢复准备与诊断源，不盲actual；具体慢query仍unknown，三宿主/安装/AOT/硬件/长期/发布分列，Graph Beta。
### WB66 helper预算合同（2026-10-08；本会话第1独立任务，报告交付）

- base6c67ff89；WB65继承index-only恢复及post通过、综合v2 overallFAIL/23of25和strict numeric-parent碰撞失败均保留，不计任务。新会话本片0/5→1/5，同一个workbench ACTIVE每30分钟。
- 实际H/E/source关键range已绑定，WB64为内部20.19s墙钟拒绝，CIM15/cache16非160数量耗尽，Node timedOut=false；5snapshot最后exit1。成功envelope三预算统计被caller丢弃，逐查询阶段耗时缺证；没有产品修复/新actual或历史因果结论。
- [专属合同](wb66-helper-budget-contract.md)与实施证据已停写，独立28/28 PASS；只report+root五docs共六路径，0tests/build/actual，完整restore/原format及本地CAS提交/当前strict退出/终审见artifacts/wb66-helper-budget-20261008。原protected4/9/7SHA与false状态保持，根20:10Z/≤5wrapper。
- 下一WB67仅保留existing envelope三安全标量到helper record/compact terminal，0新CIM/协议行/authority，保原result、primary错误、全部预算/fresh身份/stop门禁并冻结meaningful本地合同；真实恢复另窗口需异库前置。三宿主/安装/AOT/硬件/长期/发布分列，Graph Beta。

### WB65 合同与文件冻结（2026-10-08；本会话第1任务，同值fresh PUT前置）

- 继承WB64综合终审新v2的23检查PASS，原审阅工具19total/15owned误判首FAIL保留、不计新任务。当前parent05fa5e8e；同一workbench ACTIVE/每30分钟已target新会话01a11782，计数0/5→本片1/5。
- 普通DOM前置每A/B一次≤2秒，计数≤16与typed布尔/活动身份一致；固定change-required/target-already-active/unknown，后两态0click/0ack-poll失败checkpoint。原双barrier在读后同步捕获，七find谓词及正常close/disk/ownership/完整成功链不改。无产品/准备动作/HTTP/save或虚造ack；没有历史pre-click DOM/origin，WB64及WB62旧因果仍unknown。
- 3源（scenario、新test、旧32的必要fixture/精确读取计数），五root docs共八路径。根micro1/1与最终43/43（32+11新）零fail/cancel/skip/todo；全合成成功链单列为本地合同，0actual/0build/0install，不能称恢复已验。源码/根工具独立复核、完整restore/原format、CAS本地commit/post/退出/最终收据见本片新独占目录。
- 冻结18:50Z/10wrapper/2tests，原foreign HANDOFF307122B/旧根1509B补充/parity0061d6d7/旧policy对象保留；helper预算缺口下一独立任务，真实新恢复窗口随后明确冻结。三宿主/Server/OS文件/安装/Extension Host/AOT/硬件/长期/发布分列，Graph Beta；无push发布部署安装外发。
### WB64 合同与文件冻结（2026-10-08；新会话第1任务，真实fresh barrier拒绝）

- 接续ea875e49，新会话从0/5开始，失败checkpoint计1/5；唯一metadata runner新增WB64准入/目录/slice/提示及18340/18341/55340/9340。原产品、scenario/test及32合同不重做，inverse逐byte与旧基线一致；根独占五docs/gates/git，最终六路径。
- 唯一actual38583042，8seed/SELECT越过；A pre-click双barrier6/57，六候选中的A PUT200 seq6/request43被两fresh谓词拒绝，其余五项true、无unknown。普通DOM匹配A且warningfalse；30poll/7658ms，20秒上限与JSON解码atUtc边界保持。selections0/launch1与所有close/restart/restored/B-query null，原FAIL不升级WB62历史unknown。
- native95.85s、wrapper完整122.1665353s；原normalExit/cleanupProven false，helperCIM15/20.19s/cache16预算失败，六terminal保存。根随后wrapper19/鲜22absence、exact19tuple/15owned与四ports/三个owner目录454对象回收为失败checkpoint独立证据，0新增kill，不改原false。祖先/reparse/creation/marker/hash守卫与原7证据SHA保持。
- 实际源82012CD保存；最初metadata插入CR导致diff FAIL，必要修正仅删除一个CR、最终C696CE1E与inverse保持，0追加actual/0定向测试/0项目build，9source/runtime SHA复用。max8wrapper/actual1/launch2/17:50:49Z；源码/工具/实际/根回收独立复核、最终完整restore/原format/精确本地提交与退出见本片收据。原foreign HANDOFF/旧rollover/parity/旧policy对象保持，ACTIVE每30分钟同id，不新建自动化，无push/发布/部署/安装/外发。
- 下一片冻结pre-click之前已存在A ack与同值点击fresh PUT前置的真实合同；不放宽七谓词、不伪造barrier或历史因果。helper回收预算缺口另片；两desktop/B查询、三宿主、安装/Extension Host/AOT/硬件/长期/发布仍分别待验，Graph Beta。

### WB63 合同与文件冻结（2026-10-08；选择失败观察，0actual）

- 同一会话第2个独立任务：WB62提交ff9de57d已闭环，WB63完成后2/5；实施、修正、合同及独立复核合计1个，不拆分凑数。仅scenario.mjs/test.mjs两源；根独占五共享docs、门禁、集成与本地提交，七路径边界，runner/产品不改。
- pre-click同步保存原请求/响应双barrier，至多2次A/B选择记录与固定phase；候选至多128/100ms，只保存安全序号/status、原七find谓词的true/false/null和固定拒绝/未知名。普通DOM观察至多2秒、节点计数至多16、目标/host身份及warning仅布尔；getter/超限/超时/取消/观察失败保持unknown与原error对象，不保存raw/text/SQL/header/token，不新建HTTP或重试。
- root微试1/1、完整32/32（最终实测，预估33已更正），无fail/cancel/skip/todo。原成功选择不增加观察读取，原30次/250ms/20秒上限及ack/DOM/disk/normal-close/ownership/资源预算保持；callbackCalls/elapsedMs为实际观察，不把20秒上限声称为完整等待。0actual/0build，不能解释WB62缺barrier的历史拒绝或升级其真实FAIL。
- 35分钟/root≤8封装/≤2测试边界不扩，源停写SHA、源码/根工具独立复核、PS7 parser、最终七路径完整restore/原format与本地commit/退出审计见 `artifacts/wb63-studio-selection-observation-20261008`。门禁和退出未取得PASS不得提交；共享HANDOFF原293834字节、Identity发布、origin/parity-results及旧policy Temp/WB40保留；没有push/发布/部署/安装/外发。
- 下一任务WB64先接收本片真实提交与退出收据，再冻结一次新源绑定的真实恢复/失败观察验收；旧WB62 A PUT200 seq6/request43已存在但barrier与到达时序缺证，atUtc只代表JSON解码完成。A→B/两desktop/磁盘/B查询/正常关闭仍独立待验，三宿主/安装/Extension Host/AOT/硬件/长稳/发布分列，Graph Beta。

### WB62 合同与文件冻结（2026-10-08；真实A选库ack验收失败）

- 新会话 `01a11709-c3a5-7603-84d0-77de883518a7` 从7cf310a6接管；第1独立任务已形成失败checkpoint，1/5。同一个workbench ACTIVE每30分钟；新滚动迁移时机优先，既有旧不迁移描述不生效，真实验收门槛保持。
- 仅三e2e源，root五docs/验证/集成/提交；原产品及strict退出/ownership/预算未改。seed失败8KiB保留（overflow sentinel8193）/16read/≤2s受原5s/mainDeadline及abort控制，只固定类别/type/5code白名单；observer/record异常保primary，seed finally abort fetch。STRING/plain PK静态及本次真实门禁通过，不补WB61旧400因果。
- root微试1/1+最终23/23、runner语法及源码独立PASS，9产品source/runtime SHA沿用WB61，0新build。唯一actual已越过双库8seed/SELECT门禁；A普通点击fresh PUT验收30次/20s失败，actual FAIL/launch1/selections0，firstClose/secondLaunch/restored/query/secondClose null。bridge确有sequence6/request43的A PUT200及一致identity/defaultDatabase，失败barrier未保存，原因unknown，不能称无PUT。
- native208.791s/wrapper210s；六terminal保存，9fallback、四ports释放、314+163+4自有对象回收、helper reclaim0/errors空；cleanupProven=true不升normalExit=false。fresh root52身份absent/0新stop/0排除，短CIM gap分列。失败checkpoint独立复核、最终8路径restore/原format、expected-parent CAS本地commit与退出收据见 `artifacts/wb62-studio-database-recovery-20261007`。
- 45分钟/root≤9封装/测试≤2run/actual≤1/launch≤2/0build窗口不扩；实现12短shell/10文件/3rg停写，review有界只读。辅助文件删除micro在执行前被自动审查blocked by policy，0执行/0文件，保留不重试。外部HANDOFF原prefix/Identity发布/parity/旧policy Temp与WB40保留；PS7/禁Graphify广域扫描安装/归属finally及绝对路径继续，无push发布部署外发。
- 下一独立任务WB63先冻结失败action/request双barrier、候选ack判定与普通DOM投影，核现有实际A PUT的失败边界；不盲重跑或追加无关Query诊断。第二desktop恢复旅程仍未验收；三宿主/安装/Extension Host/AOT/硬件/长稳/发布分开，Graph Beta。

### WB-61 合同与文件冻结（2026-10-07；Studio 数据库选择及桌面重启恢复验收）

- 基线 c5716912；WB60 只读验收盘点和 WB61 源码盘点已结束，未新增 actual。既有 connections store、公开 GET/PUT、普通 Explorer 选库及 mounted 恢复实现复用，不改产品来迎合测试。当前 Identity 会话已完成但未提交，其源码、solution 与共享文档全部保留。
- 专属实施仅拥有 `web/e2e/run-studio-native-real.mjs`、新增 `studio-native-database-scenario.mjs` 及其合同测试。根独占共享文档、验证、集成、stage 与本地 commit；独立复核只读。新增独立 database-recovery scenario，旧 lifecycle/sql-dialogs 动作、预算及证据结论不放宽。
- 真旅程最多一次、Studio 最多两次启动：隔离真实 Server 准备混合大小写 A/B 库及不同只读查询哨兵；普通展开 Explorer 并选 A→B，每次对拍新的 PUT 200 ack、DOM、activeIdentity、profiles defaultDatabase 与精确 owned 磁盘白名单语义。store 对普通非控制库确会更新 defaultDatabase，不能把控制库动作加入该断言。正常关闭第一桌面、全四端口及所属身份退出后，保留专属 library/data/profile 并重新启动一次；先观察普通 bootstrap GET/DOM 恢复 B，再普通查询对拍真实 B 请求与不同哨兵，最后正常退出和严格回收。page reload、API 选库或 localStorage 设置数据库不能代替恢复。
- 单场景墙钟 900 秒，最后 90 秒保留回收；launch≤2、API≤80、bridge responses≤128、owned identities≤32、process helpers≤96（32 留回收）、证据文件≤64，poll≤30次且≤20秒（既有启动/关闭特定守卫保留）。不存凭据/header/HAR/console/原始库全文；仅白名单语义、hash/bytes，旧 policy 保留对象不触碰。fallback/unknown/缺 ack 使实际验收 FAIL。
- 实施≤25分钟、≤12命名内容文件、≤12短 shell、≤3定向 rg，必要本地合同测试由根运行；最多1 Web项目、2 .NET入口项目编译各1次，actual≤1，测试≤2次。新方案审查通过才创建窗口，失败保留原值，不盲重跑。最终待提交树完整 restore/原 format、精确自有hunk、diff及进程审计、本地commit。PS7、禁Graphify/广域扫描/未授权安装，无push/发布/部署/外发；三宿主/文件对话框/登录UI/安装/AOT/长期/发布仍独立验收，同一ACTIVE30分钟heartbeat继续。
- **实际结果**：唯一 `studio-native-real-64f810fa-d555-4999-8000-d13c594b203b` 原FAIL保持；首次native bootstrap、普通运行态/真实managed Server已证，混合大小写A库创建后建表POST400。选库/磁盘ack/第二desktop/恢复/B查询全部NOT_RUN；normalExit=false、cleanupProven=true，9完整归属fallback、四port释放、371+106+4专属对象回收及六独立terminal保存。237.47秒native/239秒wrapper分列。
- **修正与接续**：源码review先拒绝迟解码ack和拒绝root仍可获清理权，修正后14/14合同PASS。actual后精确Lexer/Parser静态核TEXT不是关系类型、STRING受支持；单literal修成STRING+普通可写主键，inverse SHA等原实际模块。主键不是此次CREATE400已证原因，400业务body未保存；修正版本0actual。为这一个输入修正明确追加2分钟/0探索实施和一次必要本地14项复验（总3定向run、仍14独立合同），不增加actual。下一片先补有界脱敏失败观察/核修正seed，再冻结唯一真实桌面恢复旅程；不追加无关Query诊断替代收口。

### WB-59 合同与文件冻结（2026-10-07；VS Code SQL耗时字段兼容，0actual）

- 基线09afe8dd；原WB57实际reference只含elapsedMilliseconds，Panel/history读elapsedMs。专属实施独占core/types.ts、core/sonnetdbClient.ts及新增test/sqlElapsedCompatibility.test.ts；根独占六共享docs、工具/gates/stage/九文件本地提交，独立review只读。Panel/command/Store/Host/runner及33份先前证据保持，不重复已完成页或旧测试。
- 唯一parser end边界：elapsedMs optional；native-only/legacy-only/相等双有效finite非负primitive number保留原值。缺失/未知、任一字段非法或冲突省略canonical alias；不转换、fallback、优先选择、round/clamp或填0。保留native/其余end metadata、最后end及frames/error。parsed Raw export可能新增alias，不授NDJSON原字节round-trip。
- 唯一TS项目、单end微试1及新Node5/5已PASS；含7组合成production client→Panel→Store→完整生成script测试，只为本地metadata，0actual/新.NETbuild，不升级WB57原FAIL/false/null或推history因果。最终九树完整restore/原format/fresh零排除≤90秒/共享自有hunk/commit/post/退出/独立验收见本片收据。≤14wrapper/TS≤2attempt/定向≤2/13:40Z；门禁仅自建环境禁MSBuild node reuse。
- 下一片核本次提交/退出后按WB58合同冻结独立真实history/ack或stop-verification，三宿主/安装/AOT等门禁仍分列。保留外部Identity/博客/CSDN/HANDOFF与parity、旧拒删policy对象；PS7/禁Graphify广域扫描安装/有界count+墙钟/完整身份父链/finally。ACTIVE30分钟继续，无push发布部署外发。

### WB-58 合同与文件冻结（2026-10-07；公开history/错误通知安全观察，0actual）

- 基线11f358c6；盘点已确认原完整await/串行Store与catch通知后fulfilled，不判WB57两history因果。专属实施独占新增core/queryHistoryObservation.ts、新增test/queryHistoryObservation.test.ts及Host query-real.ts；根独占六共享docs、runner/gates/stage/九文件本地提交，独立review只读。生产command/panel/history和runner、23旧实际/收据保持。
- 3phase固定公开错误通知计数/类别及单次history快照；不存原错误/SQL/token，计数与label/context匹配/unknown不授持久ack或实际PASS。第四API原样转发并finally恢复，诊断失败不遮primary。dispatch前消费总20history命令slot，含3phase及最终poll；原20pick/50entry/16notification、110s/final10s、history3/order/context/fail不放宽，不增加retry/authority。
- 根唯一TS项目与新必要Node行为验证、最终source/compiled SHA见本目录；synthetic ack拒绝/deferred仅本地合同，0actual/.NETbuild，不升级WB57原FAIL/false/null。纠正WB57自有duration措辞为pre-finally55与完整outer73.6699466秒，不改旧收据。最终九树完整restore/原format/fresh零排除≤90秒/精确共享hunk本地提交/post/退出/review以收据为准，截止12:40Z/≤14wrapper/TS≤2attempt/定向≤2。
- 下一片按新有界观察与已确认缺口冻结独立真实history/ack或stop-verification，不盲跑或推因果；elapsed DTO差异、三宿主/安装/AOT仍另验。保护外部Identity/博客/HANDOFF及parity，PS7/禁Graphify广域扫描安装/有界count+墙钟/完整进程身份父链/finally/旧policy对象不变，ACTIVE30分钟继续，无push发布部署外发。

### WB-57 合同与文件冻结（2026-10-07；真实终态三检查局部实证，原旅程/cleanupFAIL）

- 接续5b82d947/closedWB56；专属metadata两源仅六literal迁WB57/Workbench57/18358-59与新证据目录，inverse逐byte等HEAD，独立source review通过。根独占六docs/验证/gates/stage/八文件本地commit；0重复75测试、唯一TS项目、actual1/1、0新.NETbuild，窗口11:40Z/14wrapper，不启动WB58。
- 唯一c2fe13a9实际三payload2/1/45逐值对真实reference/POST200同；history20picks/2entries/AssertionError、history缺席/Host0byte/API恢复unknown/Code及Hostnull/normalfalse。原FAIL/primary true/process_audit/Error、cleanup/runtimefalse保留，不计整体Host或history成功。
- 新finalChecks真实refused/passed/refused，三检查在remaining拒绝后均到达；首remaining/final、recoverable stop-verification/round，3round/16stop/1fail/remaining1/blocking14保持。诊断accepted48与49ledger/64refs/27closedhelper分列；7parent缺command+6fresh候选缺项+1stop只记拒绝，不扩authority或推因果。
- 原16文件/14manifest343013B/5terminal完整冻结。原strict audit/完整旅程checkpoint拒绝留存；exact失败adapter用同完整tuple/event/absence/无run关联/ports/owner/depth/count/time守卫，根fresh62PID absent/0新stop/282新runtime对象逐绝对路径回收另列。最终完整restore/原format/八树SHA/fresh零排除≤90秒/精确本地提交/post/关闭/独立收据见本目录。初Span比较工具错在0wrapper/actual前，修正微试后通过且原边界保留。
- 下一片先冻结history/ack终态及stop-verification拒绝合同，不盲加实跑或升级旧FAIL；三宿主/DOM/分页/Notebook/LSP/OS对话框/安装/AOT/硬件/长稳/发布继续另验。foreign22/共享外部段/博客CSDN/parity/旧policy对象保持，ACTIVE30分钟不暂停，无push/发布/部署/外发。

### WB-56 合同与文件冻结（2026-10-07；终态三检查本地75通过，真实cleanup另验）

- 唯一切片接续318b6d1e；WB55实际remaining1/blocking7及原FAIL/false/null冻结。原终态remaining检查抛出会跳过必需的root/audit检查；专属实施仅两diagnostic源，独立review只读源码PASS，根独占六docs/验证/gates/stage与八文件本地commit，不启动WB57。
- 三原同步检查原顺序各一次、独立捕获拒绝、首terminalFailure保留；三passed且无terminal才proof。固定finalChecks仅remainingProcesses/rootIdentities/auditFailures，passed/refused/not-reached/unknown；投影单读、异常隔离/幂等，旧observer异常整体unknown。原45秒/3round/128stop/35秒reserve、身份/父链/ledger/events/helper过滤与authority保持，不新增snapshot/stop/await/loop/timer。
- 两源syntax与最终75/75（67+8）、全部失败/取消/跳过/todo0；source/test/完整日志与test-acceptance绑定，DI本地证据，0actual/TS/新.NETbuild。唯一新test Temp由finally删除并根核不存在；旧policy对象不动。根窗口10:45Z/12wrapper/最多2测试，最终完整restore/原format、八树前后SHA/fresh零排除≤90秒/精确自有hunk/post/关闭与独立最终收据见artifacts/wb56-validation-20261007。
- 下一片以实际提交和退出检查点为准，另冻结真实cleanup窗口或有证据生命周期缺口；本地75不证明WB55根因、严格cleanup或三宿主完成。外部共享/博客CSDN、foreign22/parity及旧证据保持，ACTIVE30分钟继续，安装/AOT/硬件/长稳/发布与三宿主分列，无push/发布/部署/外发。
### WB-55 合同与文件冻结（2026-10-07；真实Query/history/Code局部通过，原严格回收FAIL）

- 接续0878f154/closedWB54，完整四docs/git/路线图/提交/活动代理核验；不重做67本地合同。专属metadata仅runner/Host五组六literal、inverse等HEAD；review只读，根独占六docs/验证/gates/stage/八文件本地commit。新窗口截止10:05Z、14wrapper/1actual/1TS/0新.NETbuild/0旧合同重测，review24shell/32文件/18wait/10:00Z。
- 唯一842ecab9实跑current2/selection1/EXPLAIN45逐SQL/database/editor/columns/values/end对真实reference及POST200；公开history3、HostPASS/API恢复true/cleanupErrors0、Code0/signalnull/normalExit true为局部实证。原runner FAIL/primaryFailure true、reason/type null与cleanup/runtimefalse保持，不推未知根因，不把生成payload计为DOM/history UI或production fetch timeout证据。
- 三existing_fresh parent观察为initial893/fresh880、matches1→0、missing→null/unknown；七拒绝、37ledger/43refs/17closedhelper、4/34输出与14manifest/242000B/16原文件完整。原remaining-processes/final、3round/6stop/remaining1/blocking7保持；不补父身份/排除后代/授stop或推权限、短命、复用。
- 根原check-failure7拒绝保留，prepare-integration8用同一guards复核fresh44PID absent/0新增kill/两port及exact owner，280新runtime对象有界逐绝对路径回收另列；原strict成功工具仍拒绝，exact failed-run/current adapter不泛化放行。最终完整restore/原format/零排除fresh≤90秒/精确自有hunk提交/post/关闭/独立收据见证据目录。
- 外部微博两次HANDOFF追加严格byte prefix保留，只stage自有段/CHANGELOG一行，foreign22/parity/旧policy对象不动。下一片先冻结blocking/refusal与终态cleanup合同；三宿主/DOM/分页/Notebook/LSP/OS文件对话框/安装/AOT/硬件/长稳/发布另验，唯一ACTIVE30分钟继续，不启动WB56或盲加actual。
### WB-54 合同与文件冻结（2026-10-07；既有fresh安全观察本地通过）

- 接续155dc2df，专属实施仅query-host-evidence.mjs/test.mjs，独立只读review；根独占六docs、验证与八文件集成。只复用同batch已有source/fresh，在原准入和failure断言后给最多128个initial failures追加可选candidateTransition/v1，不新增采样或stop。
- 固定安全availability/subject、两端≤4096 snapshotCount、饱和matches与commandPresence、tupleRelation；两端唯一且有界四字段才能same/changed，invalid/throw为unknown。沿原expires前后双guard，超时省略；不改变原failure/ledger/event/authority/接受身份/终态。关系不证明权限/短命/复用/stop，不保存新增原始tuple/命令/创建时间/错误正文或hash。
- 原窗口closed5/12、两测试尝试（0启动工具失败及实际66/67 fixture FAIL）、0actual，原准备失败和全部原日志保留。只修65字符ISO创建fixture的小数秒补零，finite先决断言与原拒绝/准入期待保持。窄接续artifacts/wb54-integration-20261007-0846同09:20Z硬截止、8wrapper/仅一次67，最终67/67、syntax与source/test binding通过；0actual/TS/新build，不重做WB53。
- 外部HANDOFF新增CSDN段严格保留原received字节prefix，刷新whole-outside-own保护且只stage自有段；foreign22/parity/旧policy对象不动。最终完整restore/原format、精确stage/commit/post及独立收据见接续证据目录。真实Host/history/API/Code退出/原cleanup及三宿主/UI/安装/AOT/硬件/长稳/发行物仍另验；下一片需新冻结真实窗口，不因本地PASS升级原FAIL或暂停唯一30分钟heartbeat。

### WB-53 合同与文件冻结（2026-10-07；真实三payload局部通过，完整生命周期仍未证）

- 接续aea0181c/closedWB52，四docs完整字节快照/git/路线图/已有提交/已完成代理已核；不重做调用21/history6/diagnostic55。专属metadata只改runner/Host两源五组literal，反向SHA等于基线；root独占六docs/工具/验证/gates/stage/本地commit，共八文件，独立review只读。
- 新窗口artifacts/wb53-validation-20261007截止08:25Z、14wrapper/1actual/1TS/0新.NETbuild/0合同重测；review24shell/32命名文件/08:20Z。syntax/双PS7 AST、唯一TS compile与三文件source-build freeze通过，生产/诊断/旧失败/foreign22/shared HANDOFF/parity保持。
- 唯一actual ed4f028a：current2/selection1/EXPLAIN45与独立真实reference逐值及POST200一致；history/Host终态缺席、API恢复unknown，runner原FAIL/process_audit/Code及Host null/normalExit false保留，不把三payload当完整Host或旧原因。snapshot真实两initial parent缺command（847/matches1）与一fresh候选lookup缺项（845/matches0/null）只证首拒绝，不证明权限/短命/复用或授权stop。
- 原cleanup false/runtimefalse、3round/17stop/remaining1/blocking3，51ledger/68events/28helper与manifest12/342719B/目录14文件完整。根fresh54PID absent/无关联/两port可bind/0新增kill，canonical新runtime292对象一次删除独立分列；checkpoint C0814703…0E76E9C4及原14文件SHA冻结，strict成功工具仍拒绝。旧current adapter exact39/45拒绝保留，新的此run失败/current-absence adapter不泛化放行。
- 最终八文件完整restore/原format/前后SHA、fresh outside≤90秒零排除、精确自有共享hunk提交/post/关闭以收据为准。PS7/禁Graphify广域扫描安装/有界执行/归属finally/绝对路径/旧policy对象保护继续，无push/发布/部署/外发。下一片先冻结initial→fresh短命父与blocking/refusal合同，不重复metadata或盲加actual；真实history/API/Code正常退出/原cleanup及UI/分页/Notebook/LSP/安装/AOT/三宿主另验，唯一ACTIVE30分钟继续。

### WB-52 合同与文件冻结（2026-10-07；父链snapshot本地安全诊断通过，真实Host另验）

- 接续43093571/closedWB51，最新四docs完整字节快照/git/路线图/提交/代理核验；不重做调用21/history6或Host。专属parent_snapshot独占query-host-evidence.mjs/test.mjs，runner冻结，snapshot_review只读，根独占六docs/验证/gates/stage/本地commit，共八文件。
- WB50原6拒绝只证首guard，snapshot来源及CIM原因unknown。冻结可选candidateSnapshot：fixed schema、initial/fresh、candidate/parent/anchor、有界count/matches与missing/empty/present/invalid命令presence，unknown null；只已有lookup/duplicates O(1)，无rawtuple/time/命令/error/hash或再采样/扫描，第三参和二次投影白名单。保原拒绝/authority/stop/一batch一次fresh/12hop/预算/ledger/events，恶意观察失败不改变原终态，不用ledger补身份。
- 根截止06:35Z、14wrapper/0actual/0TS/0新.NETbuild、定向最多2次；实施14shell/16文件/06:15Z，review18shell/26文件/06:30Z。最终八文件完整restore/原format/前后SHA、fresh outside零排除≤90秒、精确自有共享hunk提交/post。22保护源/compiled/Host、旧证据、foreign22/共享HANDOFF/六PS1缺席/parity不动；PS7/禁止Graphify广域扫描安装/有界执行/归属finally/绝对临时路径保持。
- 真实Host/正常Code退出/原cleanup/安装/AOT/硬件/长稳/发布/整体三宿主仍待独立窗口。唯一ACTIVE每30分钟继续，无push/发布/部署/外发，不因诊断局部完成暂停；本片实际结果见收据。

- 初53/53与初freeze保留；独立review补未知role必须safe coords、同PIDpresence一次getter，新增两实际capture断言后最终55/55（46受影响+9新），fail/cancel/skip/todo0，最终两源/runner syntax与双PS7 helper AST通过。empty仅字面空串，present非validity，matches0/2非absence proof。实施14shell/11文件已停写，最终源码独立PASS；两测试新Temp finally回收/根绝对路径不存在核验。
- provider503中断使旧窗到期closed4/14/0actual，旧final55/gates/commit NOT_RUN及outside无存活保留。07:00Z同片接续artifacts/wb52-integration-20261007-0700，07:45Z截止/8wrapper/0actual/0TS/0新build，仅最后一次55测试和最终八文件完整门禁/提交/post，不扩旧窗或重做source/syntax/初53；review同代理新增9shell/20文件/07:40Z补验，旧预算保留。下一独立冻结真实Host窗口验证诊断及原history/Code/cleanup，不据本地PASS补旧因果或盲改身份门禁。

### WB-51 合同与文件冻结（2026-10-07；调用时SQL上下文本地合同通过，真实Host另验）

- 接续0707cae1/closedWB50，最新四文档全字节接收/快照及git/提交/代理核验。唯一切片是生产query/selection/EXPLAIN在首await前捕获immutable SQL字符串；已证旧读取位于token/database/active database三个await之后，可漂移。WB50省略source正文与无EXPLAIN transport仅支持诊断风险，不证明根因；旧FAIL/false/null原样冻结。
- 专属query_invocation只改runQueryCommand.ts和新queryInvocation.test.ts；独立query_review只读，根独占六docs/验证/集成/gates/stage/localcommit，共八文件。getEditorSql selection/current statement语义、EXPLAIN尾分号、缺SQL/数据库顺序/错误及await history ack保持；deferred token/database和真实handler/panel夹具验调用后editor/text/selection变化与完成边界。
- 根窗口 `artifacts/wb51-validation-20261007` 截止06:00Z、14wrapper/0actual/1TS项目/0新.NETbuild；新合同与command变更所需history回归分列，不重复未改cleanup46。最终冻结树完整restore/原format、SHA、fresh outside零排除≤90秒、精确自有hunk/提交/post必需。foreign22含六PS1缺席/shared HANDOFF/parity与旧policy对象保护；PS7/禁Graphify广域扫描安装/有界执行/完整进程finally/绝对临时路径保持，不push/发布/部署/外发。
- 真实Host三phase/history3/Code退出、parent snapshot/原cleanup及UI/安装/AOT/硬件/长稳/发布与三宿主另验；下一真实窗口须新冻结范围，唯一ACTIVE30分钟heartbeat继续。

- 最终定向调用21/21、必要history6/6，fail/cancel/skip/todo0；同一TS项目初版/v2两次compile exit0并保留，最终两源/两compiled SHA与test-acceptance绑定。独立review所见failure cleanup风险已补最多一条command Promise、gate/ack释放后≤1秒drain及nested finally恢复stub，原断言失败保留；生产仅两行移动、21项断言不降级。仅确定性真实handler/panel合同，未发网络/Host；最终八文件完整门禁/本地commit/post/窗口关闭以收据为准。
- 下一片先冻结parent snapshot/Host实际缺口的诊断范围，不重测本片或盲加actual；WB50具体因果与正常生命周期仍未证，三宿主继续ACTIVE30分钟检查。

### WB-50 合同与文件冻结（2026-10-07；真实Query/history与退出独立验证窗口）

- 接续c0475103，复用WB48 history6项与WB49 cleanup46项已过合同，不重做/重测；旧WB47失败冻结。专属metadata代理仅runner/Host两源五组literal替换（WB50、18352/18353、Workbench50、新evidence parent），字节逆投影等于基线，已停写；独立query_review只读，根独占六docs/验证/集成/门禁/提交，共八文件。
- 根窗口 `artifacts/wb50-validation-20261007` 截止05:40Z，13wrapper/1actual/1TS项目/0新.NETbuild，Server/Code固定产物复用。只实跑一次，严格分验三phase真实reference、公开history3/API恢复、Code0/signalnull、原cleanup与安全诊断；失败原值冻结，不追加actual或降低门禁。
- 生产history/evidence/tests/shared helpers、旧失败与foreign22路径/共享HANDOFF/origin-parity-results保护；最终冻结树完整restore/原参数format、SHA、fresh outside零排除≤90秒与精准自有hunk提交。PowerShell7、禁Graphify/广域扫描/安装、有界count+墙钟、完整进程归属/finally/绝对临时路径保持；旧policy拒删对象不触碰，不push/发布/部署/外发。
- 此片不计向导UI/Webview DOM分页/Notebook/LSP/安装/AOT/硬件/长稳/发布或整体三宿主闭环；结果另绑本窗口原收据。唯一workbench ACTIVE30分钟继续，不重复派单/迁移/暂停。

- 单次actual cf033b3b：current2/selection1逐值/SQL/database/POST200对reference通过；Host FAIL explain/source，observation3为前一selection payload且NOT_RUN，history/phase3缺席、API restored=true/cleanupErrors0。runner FAIL/process_audit/codeExit null/hostOutcome null，正常退出未证，actual1/1耗尽。
- 原cleanup false保持；新诊断complete/terminal remaining-processes-final/3round/6stop/0stopfail/remaining1/blocking6，原identity拒绝3 parent_command_missing/chain0与3 candidate_snapshot_missing。ledger39/events45/helper17全closed、manifest13/236110B/15files/五terminal完整。根fresh45PID absent/无run关联/两port可bind/0追加kill，canonical runtime280对象一次删除与原失败分列；exact root checkpoint SHA C0973CF2…9686BD及15原文件冻结绑定，strict成功拒绝，失败adapter不泛化放行。
- syntax/双PS7 AST/唯一TS编译通过，独立review接收原失败及根回收边界。下一片先定位EXPLAIN前一payload观察与parent snapshot合同缺口，不重复两源metadata或追加actual；最终八文件完整门禁/commit/post以本窗口收据为准，三宿主继续未闭环。

### WB-49 合同与文件冻结（2026-10-07；cleanup子检查诊断，真实Host另验）

- 接续本地 `fa7b9528651d75503f66e185a739ff6031e8740f`，WB48最终集成已closed7/8、0actual，6项本地history合同、最终完整restore/原级format、精确10文件提交与独立收据验收通过；不重复实施/编译/测试。四文档完整字节接收和hash/snapshot、git/路线图/已提交范围/已完成代理已核，foreign HANDOFF/22命名博客路径（含六PS1缺席）与origin/parity-results保护。
- 唯一WB49切片是既有owned-processes的固定安全子检查诊断。专属cleanup_impl仅runner/evidence/test三诊断源；独立cleanup_review只读；根独占六docs/工具/最终验证/stage/本地commit，共九文件。先确认合同再实施，生产history/Host metadata/compiled产物/shared helpers和旧WB47 FAIL/false/null不改；0actual/0TS/0新.NETbuild。
- 诊断固定subcheck/stage白名单，first recoverable与terminal失败分列；结构仅null或有界计数，不保存raw error/command/未准入身份正文或hash。依赖注入验证实际cleanup编排及runner接线；诊断不得授权stop、扩大exact helper PID排除或放松原身份/父链/失败/roots/终态门禁。保留三round/128identity/45秒且总deadline留35秒、ledger/事件/文件/秘密预算和独立cleanup/terminal；观察失败保留unknown，不把局部模拟写成完整真实回收。
- 已证旧owned-runtime先因process proof=false阻断；旧owned-processes具体子检查仍unknown。末snapshot helper29344后代64440仅为风险，不能推断因果或排除后代；WB47 history20/2根因、Code正常退出及原cleanup仍未证。向导UI/Webview分页/Notebook/LSP/OS窗口、安装/AOT/固定硬件/长稳/发布与整体三宿主另验。
- 根窗口`artifacts/wb49-validation-20261007`截止05:05Z、最多12wrapper/0actual/0TS/0新.NETbuild；首次root support提取包含门禁循环，prepare在dot-source前置检查失败且未写文档，原失败保留，修正后新label准备成功。最终九文件冻结后完整restore/原级format、前后SHA、fresh outside零排除/90秒绑定、精确自有共享hunk与post审计才允许提交，实际结果以该目录收据/git log为准。PowerShell7固定路径、禁Graphify/广域扫描/未授权安装、有界count+墙钟/比较退出/完整PID创建命令父链/finally/绝对临时路径规则继续；旧policy拒删对象不触碰，无push/发布/部署/外发。唯一workbench ACTIVE每30分钟不迁移/暂停；本片不启动下一切片。

- 最终三源冻结绑定source-build-freeze-final；定向46/46、fail/cancel/skip/todo均0，Node syntax与精确snapshot/stop双PS7 AST通过。首44/45因两个合成父链长度相等而失败，补完整continuity链并保持原depth排序/期待后复验通过，原失败保留；另证event-only沿原proof语义而overflow仍拒绝。测试Temp由原finally逐绝对路径清理；这些仅确定性DI/源码接线证据，不计真实stop/磁盘runtime删除/Host正常退出。

### WB-48 合同与文件冻结（2026-10-07；本地history完成合同已验证，真实Host另验）

- 从本地 `0ec1aff59872ca34999f51cb320a8b039f974302` 接续，四文档完整接收，git/路线图/旧提交与已完成代理核验；WB47 closed10/14wrapper、1/1actual、原FAIL/history20/2/Code1-null/hostOutcome null/两原cleanup false冻结。旧staged-handoff删除被自动审查以blocked by policy拒绝，保留不重试；旧handoff Temp拒删对象同样不碰。外部两个oschina文件更新已核为其它活动会话，刷新独立保护快照而不覆盖、回滚或stage。
- 唯一切片为生产query history完成/串行合同，专属history代理仅四源：新core/queryHistory.ts、新src/test/queryHistory.test.ts、既有panels/queryResultPanel.ts与commands/runQueryCommand.ts；只读cleanup代理与独立review，根独占六docs/工具/验证/完整门禁/stage/本地commit，共十文件。源码可证明旧show:void/void recordHistory没有ack等待，read-modify-write没有串行且原地unshift；仅是已证合同风险，尚非WB47真实20/2根因。
- 保留sonnetdb.queryHistory key、entry schema、最新逆序50条；writer immutable FIFO、执行时fresh读取、pending最多50、当前写失败可观察而尾队列恢复/finally释放；QueryResultPanel.show返回Promise，query/selection/EXPLAIN progress callback await写ack，showHistory等待调用前已排队写，showRows/Copilot同步void且不记query history。Host原20次poll、runner/metadata/evidence37与旧失败不改，0actual/同一TS项目/0新.NETbuild。
- cleanup只读盘点分列：owned-runtime可确定先被process proof=false守卫拒绝，未进入removeRuntime；owned-processes子检查仍unknown，因为step_failed没有保存子标签/final snapshot。最后snapshot helper29344后代64440可能被旧snapshot计残余而只排除helper exact PID，这是有证风险而非确证，不能自动排除后代或放松stop/身份门禁。固定安全subcheck诊断留下一独立切片。
- 本片新artifacts/wb48-validation-20261007截止03:45Z/14wrapper/0actual/1TS项目/0新.NETbuild；仅测试与编译可证明本地合同，真实Server/Extension Host/history3/API/正常Code退出和原cleanup仍待新窗口。最终十文件完整restore及原级format、pre/post冻结SHA、白名单diff、fresh outside零exclusions与≤90秒绑定才准本地commit；实际验证/gates/commit/postaudit见本片收据与git log，计划不写PASS。PowerShell7固定路径，禁Graphify/广域扫描/未授权安装、有界count+墙钟/比较微输入/完整进程归属父链/finally/临时绝对路径保持；最新foreign追加尾段/22命名博客路径（含六已移走脚本的缺席状态）与origin/parity-results保护，无push/发布/部署/外部沟通。三宿主仍未闭环，唯一ACTIVE每30分钟workbench继续，本片不另开切片或迁移/暂停。
- 最终四源码/四compiled JS绑定source-build-freeze-final，唯一TS编译通过；定向history-tests 6/6、失败/取消/skip0，完整stdout与exit0收据保留。deferred Memento与真实production command/panel在stub边界验证ack前未完成、FIFO三查询无覆盖/逆序、原数组不变/50条cap/context、picker读屏障、showRows不记历史、失败恢复/50 pending背压释放。它们为确定性存储/公开API夹具，未运行新真实Host，不宣称WB47根因或正常生命周期成功；独立复核、完整最终门禁和本地提交另绑本片收据。

- 最终集成未提交（03:42Z检查点）：完整restore/原级format已在03:31冻结树退出0并独立复核，integrate-final因其它活动OSChina heartbeat更新PROGRESS/events/state三个foreign文件而在stage前安全拒绝；随后该会话追加HANDOFF，旧十文件冻结已失效。外部会话现已idle，但完整重跑门禁的预检因03:45Z总墙钟及80秒finally余量不足而拒绝启动，未新增.NET进程或扩大预算。保留原结果/6项测试/源码SHA与所有外部内容，index为空，HEAD仍01063ddd716492122a79cb3a47305fbf71113658；本轮根checkpoint与共享记录未提交，原因即最终提交门禁待重新取得。下一次先续WB48最终集成：重新接收文档/git/代理/最新foreign，开新的明确验证窗口，冻结最终十文件后完整restore/format、fresh outside审计、精确自有hunk提交和post审计；不重复四源实施/TS/已过6tests、不新开cleanup切片或actual，不推导外部发送/push授权。

- WB48最终集成接续窗口（03:46Z启动）：旧03:45Z窗口已closed12/14、0actual，原失败/门禁/延期收据保留。外部OSChina会话revision21已idle，HEAD01063ddd、空index、parity与四源/四compiled SHA重新核验；只在artifacts/wb48-integration-20261007-0346进行最终十文件冻结、完整restore/原级format、fresh outside零exclusions与90秒绑定、精确自有Handoff/CHANGELOG hunk本地提交及post审计。新窗口截止04:10Z、最多8wrapper、0actual/0TS/0新.NETbuild；源码及已过6tests不重跑，不扩旧预算，不启动cleanup/下一切片或三宿主实跑。实际门禁、commit及post结果以新窗口收据/git log为准，未取得前不写完成；最新foreign完整内容/存在性和六缺席PS1保持，唯一workbench ACTIVE30分钟不迁移/暂停，不push/发布/部署/外发。

### WB-47 合同与文件冻结（2026-10-07；真实history失败，原门禁保持）

- 接续 `64e7a19c2104452b0c3f7999cf42b2a6fb54b1d4`，最新四文档/AGENTS/git/路线图/已提交范围与旧代理接收核验；只推进WB47。专属metadata代理仅替换既有runner/Host两源的WB47标签、evidence parent、18350/18351、Workbench47与seed，反向替换与base blob精确相同；诊断逻辑、schema/owner marker、production/shared helpers不改。独立合同与复核，根独占六docs/工具/验证/最终门禁/stage/本地commit，共八文件。
- 最终runner `A3250B55…53FD`、Host TS `C9EAF960…6701`、compiled Host JS `C9B7C978…14C2`，未改evidence/tests与生产、复用Server/Code产物均绑定source-build freeze；同一TS项目compile、Node syntax/双嵌入PS7 AST通过。未重跑未改37项合成测试，0新.NETbuild；这些检查不替代真实Host成功。
- 唯一actual `query-host-real-d3cb5ca2-5bb7-4c1d-b9ae-7734c8898689`，02:35:11→02:35:52Z、wrapper exit1。current2/selection1/EXPLAIN45行逐列/逐值/SQL/数据库与独立真实Server reference一致、POST200；Host文件FAIL/history、三phase完成、apiRestored=true/cleanupErrors0，固定失败观察history pickCount20/entryCount2，history.json不存在。runner仍FAIL/assertion，Code code1/signalnull、normalExit=false、hostOutcome=null；Host文件结果不能冒称runner已取得Host outcome。history及正常退出未通过，失败根因尚未定位，不盲改、不追加actual。
- accepted ledger44/events50、auditFailureCount0、19helper全closed且identityRecorded、4child+38helper stream hashes完整；manifest14项267672B/目录16文件、五原terminal及末FAIL status齐全。原processCleanupProven/runtimeRemoved=false，ports/helper/output=true原样保持；本run零审计拒绝不证明旧五candidate全部恢复，cleanup false根因也未定。原严格audit-owned/accept-real成功门禁保持，拒绝将失败接收为成功。
- 根失败收尾与原失败分列：fresh44完整记录PID及run关联当前无存活，18350/18351可bind，无额外kill；canonical owner marker核exact run/runner34168，depth16/4096对象/15秒inventory、30秒一次逐绝对路径删除283个自有runtime对象。root-failure-checkpoint仅证根当前回收，五原证据/source-build SHA与原FAIL/false/null不改；current-tuples audit核46条、50exact event refs，live/changed/reuse/verification/exclusions均0。旧policy拒删目录不碰，不绕过。
- 新窗口artifacts/wb47-validation-20261007截止03:15Z，14wrapper/1actual/1TS项目/0新.NETbuild；actual1/1已耗尽并关闭实跑。提交失败metadata/document检查点须最终八文件完整 `dotnet restore SonnetDB.slnx` 和 `dotnet format SonnetDB.slnx --verify-no-changes --no-restore --severity warn --exclude extensions/`、pre/post SHA、diff白名单、固定失败收据及≤90秒fresh outside审计全部通过；实际门禁/提交/提交后回收见final-gates、commit-checkpoint、post-commit-process-audit与git log，不把根收尾exit0写成整体PASS。
- 下一片先在新冻结范围定位history20/2与独立的原cleanup false，补必要合同/故障证据后才开新实际窗口，不重复本片实跑或37项既有实现。向导UI/Webview DOM分页/Notebook/LSP/WB41 OS文件窗口、安装/AOT/固定硬件/长稳/发行物另验。三宿主未闭环，唯一workbench ACTIVE每30分钟继续，本片不启动下一片、不迁移/暂停；PowerShell7固定路径、禁Graphify/广域扫描/未授权安装、有界count+墙钟/比较微输入/完整进程父链/finally与绝对路径规则保持，foreign54行footer/19博客与origin/parity-results保护，无push/发布/部署/外部沟通。

### WB-46 合同与文件冻结（2026-10-07；诊断合同完成，真实Host另验）

- 接续 `6d60c62f5072dd92be0d42c8c8d32c97ca66d96c`，完整HANDOFF/AGENTS/queue/ROADMAP接收、git/已有提交/完成代理核验；只推进WB46，WB45已closed的11/14wrapper、2/2调用与原FAIL/false/null不改。专属实施三诊断源，独立只读合同/源码复核，根独占六docs/验证/集成/完整门禁/stage/本地commit，共九文件。
- 新candidate intake先核原始candidate、当前完整父链和external文本，再构造连续至exact Node anchor的≤12hop authority；direct validator完整13hop仍拒绝。仅原PID/creation/parent有效且own command缺/空可在每batch一次fresh≤4096中重采，同PID+creation+parent且原/fresh父tuple与anchor完整一致才准入；absent/reuse/duplicate/change/仍缺命令保留fixed safe FAIL、不入ledger/不授权stop，不从旧ledger补命令，不靠rediscovery丢初候选。
- 恢复event只留candidateCommandRechecked/initialCommandState及完整fresh ledger引用；raw external先凭据guard后仅count/omitted/hash，拒绝原文与专属hash不保存。discovery3秒、fresh6秒、累计prepare/admit1秒；async等待暂停flush但只恢复剩余时间。原validator1秒、12hop、80snapshots/112helpers/128identities、ledger/event/file字节门禁和live连续父链stop保持；pendingAudit单飞/前序串行，前台与cleanup均await，finally先等pending。取消只结束等待，保留helper记录，不自动kill。
- 最终三源freeze-final-v2：runner/evidence未再改，review限定补exact12成功/完整tuple与direct完整13拒绝；最终37/37（26旧+11新）零skip、Node syntax/两个精确嵌入PS7 AST及独立源码复核通过。首版37/37也通过，旧记录保留。本片0actual、0TS、0新.NETbuild，Host与WB45 actual metadata/生产/shared helpers/旧产物hash冻结；仅合成合同证据，不承诺旧五candidate全部恢复或正常Code退出。
- 根证据 `artifacts/wb46-validation-20261007`，总窗口至02:45Z/12wrapper/0actual。最终九文件完整restore及原级format、前后hash/精确命令/时间绑定、白名单diff才允许本地commit；实际结果见final-gates/commit-checkpoint/post-commit-process-audit与git log。短命验证进程未取得完整CIM的边界单列，完整记录fresh审计无存活才放行；新测试Temp由原finally绝对路径核验移除，诊断交付物保留，旧policy拒删目录不碰。
- 下一片使用新metadata/新冻结窗口验证公开history/API恢复与正常Code退出；WB45三查询局部数据/原FAIL与WB44历史/API证据保持各自范围。生产maxRows、向导UI/Webview分页/Notebook/LSP与WB41 OS窗口条件另验。三宿主仍未闭环，旧任务/唯一workbench ACTIVE每30分钟继续，不迁移/暂停、不在本片启动下一片；PowerShell7、禁止Graphify/广域扫描/安装、有界执行/完整归属父链/finally规则、foreign54行footer/19博客等/origin-parity-results保护保持，无push/发布/部署/外部沟通。

### WB-45 合同与文件冻结（2026-10-07；安全诊断完成，真实完整旅程仍失败）

- 接续实际 `d3d3a4ff85d898149d3771c35b4eaad35294e5a9`；最新四文档完整receipt/hash、AGENTS/git/路线图/已有提交与子代理核验。专属实施三个既有runner/evidence/test、metadata代理仅Host固定WB45目录/18348+18349/Workbench45/label/seed，独立合同与根工具复核；根独占六docs/验证/最终门禁/stage/commit，共十文件，生产/legacy/shared helpers与Server/Code产物hash冻结，0新.NETbuild、同一TS项目。
- 完整身份先验改为fixed subreason/field/completeness、null或0～11 chainIndex与六项有界数值structure；每个getter只读一次，异常固定fallback，event阶段不附身份诊断。不存原error/command/created/未接受父链正文或其hash，拒绝候选不加入ledger也不授权stop；原safe-integer准入、祖先时间语义、exact Node anchor/live连续父链、12hop/1秒及128身份/256KiBledger/256events/192KiBevent/512KiBfile/24files/8MiB全部保持。
- 初次freeze自审发现dynamic getter与测试Temp异常清理边界，修后停止写入并保存 `source-freeze-final-v2.json`、`source-build-freeze-final.json`，旧freeze/25项中间测试不覆盖。最终26/26定向故障测试（含动态getter/凭据/固定字段与既有预算）、既有七Node文件20/20零skip、同一TS编译/Node syntax/两个嵌入PS7 AST和独立源码review通过。首代理短测试CIM身份缺失失败保留，后续完整tuple/父链/absent与新测试Temp finally清理另证，不冒称全进程生存期捕获。
- 根首次actual invocation遗漏required `SONNETDB_QUERY_REAL_SERVER_SHA256`，preflight退出1且run目录/Server/Code均未创建；原日志/result与 `root-preflight-invocation-failure.json`绑定保留。修调用参数后第二/最后新run `query-host-real-7e789c8f-a395-44fc-ba10-2b6ff9e09152` 40秒仍FAIL/process_audit；两次调用已用尽2/2，本片不第三跑。Server原产物hash复用，没有重构建或诊断后盲改准入。
- 新安全诊断精确五candidate PID30716/86700/65980/86808/74180，首失败guard均 `identity_command_missing` / `commandLine` / `missing_command`，候选parent为48592/48592/30716/48592/86700、观测chainCount为13/13/14/13/14。这只证明该快照缺命令的首拒绝点，不证明退出或其它guard一定通过，不保存其完整tuple、不补入ledger；缺命令为何发生及WB44旧三个unknown原因仍未确定，不能回溯猜因果或降低stop门禁。
- 三phase current2/selection1/EXPLAIN45行逐值与sql-only独立真实Server参考相同、POST200；公开history/host-result本run不存在，API恢复与正常Code退出未证，codeExit/hostOutcome仍null。已有WB44公开history/API证据保持但不转算为本run；公共prompt/API/生成payload不计向导UI、wire SQL正文、Webview DOM或分页。
- accepted ledger46/events61、25helper全完整identity+closed；process-events249814B、4child+50helper完整stream hashes，五terminal与末FAIL status全部保存。Manifest12项311736B/目录14文件及源码/product/foreign绑定已独立根核验；原processCleanupProven/runtimeRemoved=false、ports/helper/output=true原样保持。Outside完整primary tuple/逐event exact ledger引用审计无live/reused/changed/exclusion；root另核52记录/未完整PID及run-associated均无存活，两port可bind，canonical owner marker/4096对象/depth16/15秒inventory与30秒逐项删除一次清279对象，无追加kill。`root-failure-acceptance.json`只证安全诊断/三局部数据与根回收，不升级完整runner或身份生存期。
- 根失败适配只认上述exact run、固定收据SHA及原result/cleanup/events/manifest/status/source-build hashes，fresh五candidate/runnerchildren/ports及全部helper/output/live tuple仍必需；原false保留，unknown新run/changed/live/未闭helper仍拒绝。最终十文件完整 `dotnet restore SonnetDB.slnx` 和原级别 `dotnet format SonnetDB.slnx --verify-no-changes --no-restore --severity warn --exclude extensions/`、前后tree hash/时间/精确命令与白名单diff是本地commit放行条件，实际结果见本片final-gates/commit-checkpoint/post-commit-process-audit及git log；14wrapper总预算到02:15Z不扩大。
- 下一片先为已证缺commandLine候选设计有界fresh重采与exact anchor链处理合同/故障注入，仍要求完整tuple与所有原门禁，再在新冻结窗口验证history/API/正常Code退出。生产maxRows、向导/DOM分页/Notebook/LSP和WB41 OS条件另片；三宿主未闭环，旧任务/唯一ACTIVE30分钟heartbeat继续，本片不启动下一片、不迁移/暂停。PowerShell7固定路径、禁Graphify/广域扫描/未授权安装、有界count+墙钟/微输入比较/完整归属父链/finally与绝对临时路径核验保持，foreign54footer/19博客等/origin-parity-results与旧policy目录不碰，不push/发布/部署/外部沟通。

### WB-44 合同与文件冻结（2026-10-07；公开history/API恢复已核，完整实跑仍失败）

- 基线实际 `e11c52a2`，一个有界诊断切片；ledger实施独占runner/evidence/test、metadata代理仅Host固定WB44目录/slice/ports/库/label/seed，合同及root工具独立只读复核，根六docs/验证/完整门禁/本地commit串行，最多三活动子代理，依赖既有WB43，0新.NETbuild。

- 最终四诊断源/六共享文档共十文件；完整原身份与安全文本先验，own完整tuple及连续父链截到exact Node anchor inclusive，外部ancestor正文只留count/omitted/SHA；secondary events以exact PID/creation、full-command SHA和primary ledger引用保留，逐parent tuple匹配。stop必须核每个live中间父及ledger-bound完整anchor，missing/changed拒绝，不停止Node/verifier；128身份/256KiBledger/256events/192KiBevent/512KiBfile/24file/8MiB保持。
- 两旧数据纯内存micro绑定WB43不可变event/result/manifest：35项ledger256642B、代表第36项265173B超过262144，去重复external正文后代表36项120593B可准入；这是代表性合成准入证明，旧PID26604及全部旧拒绝的唯一原因仍unknown，旧FAIL/false及耗尽14wrapper/2actual不改。
- 最终四源码SHA在source-freeze-final/source-build-freeze-final；同一TypeScript项目编译、Node syntax/两个嵌入PS7 AST、定向内存17/17及既有七Node文件20/20零skip通过。独立合同复核PASS；production11/legacy/shared helper与旧Server/Code hashes保持，0新.NETbuild，不把mock/内存或编译当真实旅程。
- 唯一新actual `query-host-real-f5a28bd1-6401-42c4-a4c9-b832d4b05f80`（30秒）完整仍FAIL/process_audit。三phase current2/selection1/EXPLAIN45逐值与独立真实Server参考相同、POST200；公开history三条按逆序SQL/rows/原库与连接上下文对拍；Host-result PASS/apiRestored=true/cleanupErrors0。公开prompt/API与生成payload/QuickPick条目不计向导UI、DOM/分页或wire SQL正文；Codeexit/runner hostOutcome仍null，不能计正常Code退出。
- accepted ledger37/events44、17helper全部完整identity+closed、process-events180383B；三candidate PID45664/46964/52084仅固定identity/chain先验失败，候选完整tuple未保存，具体字段/父链/时间断言unknown，安全诊断绑定五原文件hash，不盲修或盲用第二actual。原processCleanupProven/runtimeRemoved=false，ports/helper/output=true、全部六终态齐全；manifest14项239725B/目录16文件、4child+34helper完整stream hash核验，原FAIL/false保留。
- 根acceptance分别核三phase/reference/history/API与manifest/status/source-build/product/foreign；outside primary tuple/每event exact ledger refs审计39完整记录无自有存活、无PID复用/changed/exclusion。fresh41记录或未完整PID与original runner child/runtime-associated均无存活，两port可bind；canonical owner marker/4096对象/depth16/15秒inventory及30秒逐项删除一次清273对象，无额外kill。根收据只证root回收，不升级normalExit/完整身份/全生存期。首两根acceptance selector失败（自身RunName关联与WQL反斜线）保留，修后smalltrial0/第三acceptance通过；未重跑actual。
- 根适配仅认上述exact失败run、固定收据SHA及原result/cleanup/events/manifest/status/source-build hash；原false保持，unknownPID fresh不存在、runtime不存在、ports/helper完整、samecreationchanged/live/未闭helper仍阻断；外审计必须所有旧wrapper已结束、零exclusions，commit绑定≤90秒。独立root工具review已补逐hop creation、同creation changed/incomplete阻断和commit全部cleanup字段，foreign精确19项。
- 本片窗口至01:40Z/最多12wrapper、1/2actual已用；不扩大预算，最终十文件完整restore/原级别format、前后树hash/时间/精确命令绑定及白名单diff后才本地commit，实际结果见final-gates/commit-checkpoint/post-commit-process-audit和git log。HANDOFF只WB44顶部，54行foreign footer/博客/oschina/origin/parity-results及旧policy目录保持，不push/发布/部署/外部沟通。下一片先把完整identity先验拒绝安全分解到fixed subreason/缺失字段及链断点，再新冻结窗口验证正常Code退出；生产maxRows、向导/DOM分页/Notebook/LSP与WB41 OS条件另片，三宿主未闭环，旧任务/唯一ACTIVE30分钟heartbeat继续，不迁移/暂停，本片不启动下一片。

### WB-43 合同与文件冻结（2026-10-07；三条真实数据已核，完整旅程仍失败）

- 基线 `35e16c50`；完整四文档receipt与WB42最终tree对拍，git/路线图/提交/旧完成代理核对。只修partial discovery日志/finally独立回收、reference与生产sql-only预览合同及Host严格比较前的安全观察，不改生产query.maxRows、命令或shared helpers。WB42原两失败/耗尽预算/integration exit1保持。
- Runner代理独占existing `extensions/sonnetdb-vscode/scripts/run-query-host-real.mjs`及new `query-host-evidence.mjs`/`.test.mjs`；Host代理仅existing `src/test/host/query-real.ts`；第三代理独立只读。根六docs/长验证/gates/git串行，共十任务文件。先故障注入与最终语法/TS/复核，再最多2新actual验证三个SQL命令、公开history与正常Code退出；各层PASS不相互升级。
- 新根 `artifacts/wb43-validation-20261007` 至01:05Z/14wrapper/2actual/1TS项目/0新.NET构建；runner00:34Z/20短shell/24源/20rg，host00:29Z/14shell/18源/12rg，review00:58Z/20shell/30源/15rg；短读30秒/rg60匹配15秒。真实端口18344/18345、Workbench43、五seed，固定已有Code/Node/dotnet与Serverhash。schemas兼容wb42.v1加sliceWB43；reference仅{sql}默认完整响应，验收100行只属本片小语料，不计产品分页/预算。
- 原600秒run/120秒Code/510秒active/90秒cleanup、snapshot80/4096项、24文件/512KiB/8MiB/wx/凭据门禁保持；partial完整安全身份可追溯，changed/missing身份保留并FAIL，其它核验目标独立回收；terminal/detail写失败也继续cleanup/result尝试，末terminal-status记录失败不改旧证据。Host最多9文件/4MiB，observation1～3与failure-observation使用固定check/安全projection，phase只在严格比较通过后计完成。
- PowerShell7、禁Graphify/广域扫描/未授权安装；次数/墙钟/微输入比较/归属进程完整身份父链/finally回收/绝对临时路径与policy不绕过均保持。Foreign/footer/博客/oschina/parity与旧拒删目录不stage、不碰；根最终十文件完整restore/原format/hash通过才本地commit，无push/发布/部署/外部沟通。旧三宿主与WB41阻断分别继续，唯一heartbeat ACTIVE30分钟，本片不启动下一片。

- 最终四诊断源/六docs共十文件；Host/runner/evidence/tests SHA与证据freeze绑定，生产/legacy/shared helpers不改。同一TS compile、最终syntax/嵌入PS7 AST、纯内存9/9及既有Node20/20零skip通过；root syntax提取器旧参数selector失败修后通过，失败记录保留。
- 两actual均保留FAIL，不第三跑。首preflight四helper closed但identity未捕获、Server/Code/runtime未创建；精确微输入定位外部祖先缺command，最小修连续完整父链至仍存活exact Node anchor，外部祖先诊断/自身不stop。第二63秒实跑三phase current2/selection1/EXPLAIN45逐值与sql-only真实reference一致、POST200；三比较前安全observation保持NOT_RUN，三phase才是严格比较通过证据，100行只属诊断准入。
- 第二完整旅程FAIL/process_audit，ledger35/events44、59 failures、45helper closed但29身份缺；接近256KiB ledger仅候选。history/host-result缺、API恢复/正常Code退出未证；四cleanup false/输出hash true。五terminal与末FAIL status、manifest12项519651字节/总14文件均核验，4child+90helper完整stream hash保留；不计向导/DOM/分页或唯一根因。
- 根wrapper归属回收后fresh65 PID/run-associated无存活、两port可bind，canonical owner marker/4096对象/depth16/15秒inventory与30秒一次逐项删除281对象；root-failure-acceptance与55完整记录审计独立绑定原FAIL及源码/manifest/status，原false保持。适配器仅允许两个exact失败run；初根freeze selector错误保留修正记录，未知failure/live/runtime/unclosed仍拒绝。完整restore/原format及最终十文件hash/白名单diff为本地commit放行条件，实际见final-gates/commit-checkpoint/postaudit。
- 下一片先有界定位ledger准入/固定合并reason，再补history/API恢复/正常Code退出。生产maxRows、真实向导/分页/Notebook/LSP与WB41 OS窗口条件另验；三宿主仍未闭环，旧任务/唯一ACTIVE30分钟heartbeat继续，不迁移/暂停、不触碰foreign或旧policy目录。

### WB-42 合同与文件冻结（2026-10-07；两条真实数据证据，完整旅程失败待续）

- 依赖现有Remote-first、M32/M34与WB14/16，从实际 `38c6f688` 接续，完整receipt/fresh hashes与git/路线图/既有提交/完成代理核对。inventory有界只读10短shell后停止，无进程/temp；现有真实Host只证激活/注册/导航和轻量语言，HTTP mock/LSP假sidecar不替代真实Server，不重做已实现命令。
- 只补3个现有read-only生产命令到任务独占真实Kestrel；最多5条seed、列原名、精确当前语句/selection与EXPLAIN按实际Server schema，对拍真实generated panel payload和公开QueryHistory QuickPick条目。公共prompt-driver≠连接向导UI，HTML≠Webview DOM/分页，API认证≠登录UI；Notebook/LSP/OS/VSIX安装/AOT/硬件/整体三宿主另验，query.maxRows候选另片。
- 专属代理仅new `src/test/host/query-real.ts` 和 `scripts/run-query-host-real.mjs`（均在extensions/sonnetdb-vscode）；readonly独立复核；生产/原host index/旧smoke runner/共享helper冻结，根六共享docs/验证/审计/完整最终门禁/stage/commit，共八文件。Code/Node/dotnet已知绝对路径，不下载/安装，复用已核Server DLL，0新.NET build/最多1TS项目。
- 新root到00:45Z、14wrapper/2actual；实施00:00Z、24短shell30秒/24named/24rg60匹配15秒；复核00:30Z、20短shell/30named。Runner600秒/Code120秒/readiness120次60秒/command20秒/仅setup-reference HTTP10秒（生产fetch无signal，不计取消）/history20次10秒；证据24份/512KiB/8MiB/wx/秘密拒写；public hooks finally恢复，Server/Code实际身份/父链/正常测试退出、端口与runtime各验，失败终态保留。
- PowerShell7、禁Graphify/广域扫描/未授权安装；有界执行/微输入退出比较、完整PID/creation/command/父链/finally归属回收和绝对临时路径/policy不绕过规则不变。Foreign/footer/parity/旧拒删目录不碰，无push/发布/部署/外部沟通；最终八文件完整restore/原级别format/hash门禁通过才commit。WB41失败未解除、不盲重跑，三宿主未闭环，唯一heartbeat ACTIVE30分钟、本片不启动下一片。

- 最终两新诊断源Host `B6669926…BB5BC0`/runner `D30DA1C4…D11E6F`，生产/legacy/helper冻结；Node syntax/同一TS项目compile exit0，既有Node20/20（mock另列）。首actual f76a462a preflightFAIL/六终态，两个helper缺完整身份，Code/Server/runtime未启动；精确微输入证parent2秒逐CIM超时，改一次≤4096/3秒snapshot+内存lookup，原12hop/2秒/身份门禁不降。原失败不覆盖。
- 最后actual ed4167fe：真实READ用户/五MixedCase seed/独立reference，current rows4/5、selection row2，原列/SQL/database生成payload与reference同，POST200。EXPLAIN第三POST200但AssertionError/无phase3载荷，history NOT_RUN；reference显式preview32/truncated、生产只发{sql}的合同差异留候选，不凭缺载荷认唯一根因。apiRestored真，runner process_audit FAIL/tracked29/events24、六helperclosed；codeExit null/原cleanup false，整体3/3未通过。
- 根fresh两单PIDfallback后16wrapper记录0，端口18342/18343释放；282对象新runtime经marker/绝对路径/次数/墙钟门禁一次逐项删除，原10证据/manifest189693B不改。Root acceptance只验两局部生成数据/根回收，不计正常Code/Server生命周期、UI或全进程捕获。14wrapper/2actual窗口已固定，最终八文件完整门禁、白名单commit/postaudit见证据目录；foreign/parity/旧policy目录保持。
- 下一有界切片：先reference/生产preview合同与诊断partial discovery日志/finally回收的最小修补，再真实EXPLAIN/history/Code正常退出。不得第三跑本片；query.maxRows生产、向导UI/Webview分页、Notebook/LSP与WB41 OS窗口阻断分别继续，三宿主整体仍未闭环，唯一heartbeat ACTIVE30分钟不迁移/暂停。

### WB-41 合同与文件冻结（2026-10-07；诊断入口已验证，原生旅程失败待续）

- 依赖WB-40实际提交 `0a9834bd`，完整receipt+fresh exact SHA256确认HANDOFF/AGENTS/queue/ROADMAP相同；git/已存在提交/完成代理核对。现有SQL Ctrl+O/Ctrl+S、原生Open SQL/Save SQL As、typed bridge及WinForms Service已有实现。独立inventory只读盘点8短读/11rg/11命名源文件后停止，无进程或临时对象，不将已有功能重新包装。
- 唯一切片：普通DOM Ctrl+O通过真实OS picker打开任务自有SQL，再正常取消Open；Ctrl+S保存至唯一任务自有新输出，再正常取消Save。保存工具返回OS窗口/截图、实际bridge DTO、SQL/tab与输出bytes/hash；取消不新建tab、不改SQL、不提示成功、无额外输出。只验证这四步，不执行SQL，最后普通退出/四端口/归属进程/三runtime分别核验；API认证准备非登录UI。
- 文件归属：专属runner代理仅existing `web/e2e/run-studio-native-real.mjs` 显式sql-dialogs场景与new `web/e2e/run-studio-native-dialog-real.mjs` 薄入口；独立review只读；根六共享docs/Computer Use Windows交互/验证/最终门禁/stage/commit。生产Vue/API/Studio/Server、helper/evidence与旧lifecycle默认行为冻结；真实Windows输入仅node_repl/@oai/sky且每次工具观察→唯一返回窗口→单动作→刷新，不混PS UIAutomation，不碰其它窗口或登录/安全提示。
- 根新 `artifacts/wb41-validation-20261007` 至00:05Z、10wrapper命令/2actual run/0build项目；runner23:16Z、18短读各30秒/20命名文件/16rg各60匹配15秒，review23:43Z、16短读/24命名文件；Windows调用48次/25分钟、单调用20秒，phase次数+60秒墙钟，原48file/512KiB/凭据/terminal门禁保持。构建复用WB40已核hash产物，真实picker/bridge/mock/安装/AOT证据不混。
- PowerShell7、禁止Graphify/广域扫描/未授权安装；有界执行/微输入比较退出、PID/creation/full command/父链/finally归属回收和绝对临时路径规则继续；policy拒绝删除不绕过。Foreign footer/博客/oschina/parity、两个Temp及WB40策略保留runtime不动，无push/发布/部署/外部沟通。最终八任务文件完整restore/原级别format前后freeze/hash通过后本地commit；binary/目录/owner与超时矩阵/库恢复/安装/完整三宿主仍待验，唯一heartbeat ACTIVE30分钟，本片不启动下一片。

- 最终runner `03C1162B…AB68C`/thin `078EB0F2…6AB5B9`，显式四phase/真实DTO、普通DOM SQL/tab/toast、磁盘bytes/BOM/SHA对拍与失败终态；旧default lifecycle/生产/helper/evidence与六旧产物不改，初/final Node syntax通过、0新build。实跑后仅三处说明文案明确验证范围与OS/per-phase必须另证，原失败文件不改。
- actual1 `77847de9-98d9-4588-9288-fced310cb6dd`，旧source4230C285、wrapper1/232秒：Ctrl+O真实POST open-file已观察，response=null、无ack、60秒超时；Open成功FAILED，Open取消/Save成功/Save取消NOT_RUN，无saved输出/SQL执行。sky4调用只见Studio11996610，snapshot实际仅桌面背景，activate失败后fresh list仍无picker；无文件输入或按钮动作、无其它UIA绕过，不盲用第二run额度。JPEG118207B及OS观测独立保存；disabled pane/静态boundary/桥请求都不替代可见picker，也不能将激活失败归为产品Open故障。
- 未执行后续Health/Stop/Start/普通关闭；normalExit=false、Studio fallback exit4294967295。finally9完整身份单PID回收、0helper reclaim、18helpers退出0、六终态齐全；四ports与三runtime释放。根failure-checkpoint只验失败证据/20文件≤512KiB/JPEG+输入hash/无输出ack/fresh端口/完整fallback引用与49记录身份无业务存活，独立review直接实际文件/JPEG同结论；不是四phase或正常生命周期PASS。
- 八文件最终完整restore/原级别format及前后freeze/hash/时间/精确命令、白名单stage/diff/本地commit见本片final-gates与commit-checkpoint；foreign/footer/parity和旧policy目录不碰。后续先确认桌面工具可观测/正常激活再接WB41真实四phase；未恢复前可从未完成VS Code队列选独立有界任务，不跳过或取消旧Workbench工作。整体未闭环、唯一heartbeat ACTIVE30分钟；本片不启动下一片。

### WB-40 合同与文件冻结（2026-10-07；本机默认/窄窗 Health 已验证）

- 依赖 WB-15 客户端与 WB-39 普通 native 生命周期，从实际 `db7f0ca1` 接续；文档/最终 SHA256、git/路线图/提交及三个完成代理核对。1440×920 默认 native 参数在本机 DPR 下可能 CSS≤1100，组件响应式当前隐藏 Health/state；≤720 整 toolbar 隐藏。仅修此已证入口缺口，不重做宿主或九模型页面。
- 文件归属冻结：`/root/wb34_ui` 仅 `web/src/components/StudioWorkspaceTabs.vue`、`web/e2e/studio-host-client.spec.ts`；`/root/wb34_runner` 仅 `web/e2e/run-studio-native-real.mjs`；`/root/wb33_ui` 只读源码/真实证据。helper/evidence modules、生产 Studio/Server、其它模型与宿主不改。根独占六共享文档、验证/审计/集成/完整最终门禁/stage/commit，只提交本任务文件且 HANDOFF 仅顶部任务 hunk。
- 验收先保五态/canStop/权限/身份/迟返合同，mock fixture 明确分开；真实默认不传 width/height，explicit narrow 仅正常 native 参数、实际 CSS≤1100，观察尺寸/DPR/可见操作位置与必要溢出。实际 native manifest/连接身份和普通 Health→Stop→Start→Health、普通 CloseMainWindow、旧新 Server/四端口/自有树回收必须分别核验；禁止 viewport/CSS 注入、mock bridge、fake 事件/私有组件入口/force click。认证 API/localStorage 不计登录 UI；不计对话框/安装/AOT/硬件/长稳/发行物或完整三宿主。
- 根新 `artifacts/wb40-validation-20261007` 至23:32Z、16长命令/3实际run/3构建项目；实施代理22:46Z、18短读各30秒/20命名文件/20搜索各60匹配15秒；复核23:15Z、16短读/24命名文件/20搜索。循环/搜索/等待/重试同时次数/项目及墙钟，先微输入核比较；长进程完整PID/creation/command/父链与finally仅核验回收任务树，临时绝对路径核验。PowerShell7、禁Graphify/广域扫描/未授权安装，两policy Temp不碰；foreign footer/blog/oschina/parity保留，无fetch/push/发布/部署/外部沟通。最终代码树完整restore及原级别format通过才本地commit；整体未闭环、唯一heartbeat ACTIVE30分钟，本片不启动下一片。
- 三源码最终freeze-final-2：仅native CSS保留身份/title、状态、Health与现有Start/Stop，≤1100换行、≤720保留native工具区；非native原断点与全部脚本/权限/事件合同不改。Runner分default省尺寸/narrow1000×800，最多六实测geometry与五DOM/PNG，不更改helper/evidence库或生产Studio/Server。
- 原fixture12skip/显式StudioNative9/3失败原样保留；已有收起Explorer导致准备helper失败，修为观测后必要普通点击，最终12/12零skip，1100/950/640/500及合同回归通过。最终Web build、Server/Studio Release通过，三index同SHA256；最初Web build非最终树不升级，chunk警告保留。
- 三实际run中首default被外层CIM空CommandLine检查中止，terminal缺失/生命周期未证；root核node96524/conhost37780及精确父PID/路径无存活、四端口释放。其三runtime删除被自动审查`blocked by policy`拒绝，原目录保留不绕过，不计cleanup PASS。Wrapper只对3次/2秒后已退出瞬时identity记录独立边界，不弱化活进程身份检查。后两rundefault125秒、narrow135秒exit0，原Program1440×920对应CSS946×556、1000×800对应652×476，DPR均1.5，各六geometry无横溢出、五DOM身份/状态/Health及active动作正常可见可达。
- 实测均自动collapsed0/1且preparation needed/clickedfalse，不声称展开状态；两普通Health→Stop→Start→Health、CloseMainWindow exit0/signalnull、old/new Server完整身份及Studio父链、四端口/零fallback/helper reclaim、六成功runtime删除与五terminal独立落盘通过。根accept/164身份审计和fixture profile不存在、独立源码及两actual/PNG复核通过。证据`artifacts/wb40-validation-20261007`保三run/失败/源及产物hash；最终九文件restore/原级别format/绑定/白名单提交/postcommit实际结果见final-gates/commit-checkpoint/git log。
- 尚不计认证登录UI、native库database同步/恢复（DOM控制库而bridge activeDatabase空）、Server优雅恢复、原生文件对话框、安装/升级卸载、完整Studio/VS Code/Extension Host、NativeAOT/硬件/长稳/发行物或三宿主整体。下一片原生文件对话框，再VS Code；本片不启动WB-41，唯一heartbeat ACTIVE每30分钟，旧任务继续。

### WB-39 合同与文件冻结（2026-10-07；本机宽窗原生生命周期已验证）

- 从本地`2a6eadbf`接续，完整文档读取记录与最新SHA256对拍无变化，git/路线图/提交/三个完成子代理已核对。只修真实Studio烟测的窗口关闭发现与有界终态落盘；WB-38三失败/16命令窗口不改不重置，生产Studio/Web/Server及旧fixture冻结。`windowsHide`仅为未证假设，正常关闭须重新核验准确任务身份及原生入口，不能将force kill/CDP关闭冒称正常退出。
- 专属`/root/wb34_runner`独占两个既有native runner/helper及必要新`studio-native-evidence.mjs`/`.test.mjs`；`/root/wb34_ui`只读独立审查；根独占六共享文档、全部验证/审计、stage/完整restore-format与本地commit。新`artifacts/wb39-validation-20261007`窗口至22:52Z、最多16长命令/3真实run、0生产构建项目，复用WB-38产物先核hash且单列旧构建证据。代理至22:10Z、24命名文件/24定向搜索（每次80匹配/15秒）/16短读各30秒，不启动长验证或实跑。
- 验收保真实native bootstrap/manifest与正常可见DOM Health→Stop→Start；普通原生关闭exit0/signalnull、旧/新Server身份消失、四端口释放且不需fallback才判生命周期通过。终态文件独立落盘，详细证据尺寸/凭据门禁不放宽；故障注入证明过大详细文件不能压掉cleanup/result，不假造旧缺文件。默认/窄窗、原生文件对话框、安装/AOT和三宿主整体仍另验。
- PowerShell7固定pwsh；禁Graphify/广域扫描/未授权安装；循环/搜索/等待/重试有次数、项目及墙钟，先微输入核比较退出；长进程PID/创建时间/完整command/父链及finally只回收核验任务树，临时对象绝对路径核验。两政策保留Temp不碰，外来HANDOFF footer/博客/oschina不stage、origin/parity-results保留；无fetch/push/发布/部署/外部沟通。最终树完整restore/原级别format通过后只提交本任务文件，整体未闭环、唯一heartbeat每30分钟ACTIVE，不启动下一片。
- 四源码最终freeze见source-freeze-final；普通visible Studio/.NET刷新主窗10次2秒、fresh core/父链不引interop。schema2最多256完整identity/父链keys、24events/64helpers/1秒，单份512KiB/wx/凭据门禁；三essential独立写，result留3秒，16纯内存故障注入/PS AST/3Node syntax通过。六旧构建产物hash匹配，0新生产构建项目。
- 首actual run1已接受普通CloseMainWindow，但root monitor逐历史PID查询超15秒batch后强停自有任务，原五terminal/stdout/stderr缺失保留，不能判生命周期PASS；18身份根核0存活，三runtime独立remediation清理。root只改两次batch CIM/复用完整行，原15秒/192进程/12层不延、单PID身份核验/退出证明/失败drain；不改变四源码或覆盖run1。
- actual run2`52a83a3b-3b32-4b19-b457-552bc85f82f8`145秒/exit0：同一实际WebView2 native manifest/DOM Health→Stop→Start→Health，Studio97792-owned Server41480→77576；普通HWND27267142/title SonnetDB Studio/1次8ms关闭，Studio退出0/null、四端口与旧新身份释放、零fallback/reclaim。normal-exit754/cleanup2275/result1268/process-events46989字节及bridge独立保存，33表身份/15helpers；typed引用/命令/父链/源码hash根核验通过，综合63身份0存活、六runtime不存在。root-native-acceptance/after-native-process-audit与独立成功复核为最终门禁，首run原失败另列。
- 十文件最终freeze在完整restore/原级别format前，前后hash/命令/时间绑定见final-gates；工具/成功独立复核及白名单diff通过才本地commit，实际见commit-checkpoint/git log。HANDOFF只顶部任务hunk，54外来footer/博客/oschina不stage。仅宽窗CSS1266×663/DPR1.5，不推断旧windowsHide原因/Server优雅恢复；下一片默认/窄窗Health，再native对话框/VS Code。整体未闭环、唯一heartbeat ACTIVE30分钟，不在本片启动下一片。

### WB-38 合同与文件冻结（2026-10-07；局部真实证据，正常退出未闭环）

- 集成接续：上轮完整restore/format退出0但根封装器在Git启动前因两条路径失败，未stage/commit，原窗口16/16长命令/3实跑及失败不重置。2026-10-07从`2b21279f`与八文件未提交hash核对接续，新`artifacts/wb38-integration-20261007-01`仅35分钟/6长命令/0实跑，根复核首Git路径/命名数组/未启动finally，read-only Git极小输入PASS。复用UI代理仅8命名文件/8只读shell、21:18Z硬限独立工具复核；根独占六共享文档/所有mutation，最终八文件树完整restore/原级别format、进程审计、白名单diff和工具复核通过后本地提交，实际见新证据/git log。两源码/生产冻结，不重复实跑，不启动WB-39。

- 从`2b21279f`接续，继承完整Raw读取并对拍最新HANDOFF/AGENTS/queue/ROADMAP；外来HANDOFF尾部已追加到54行，全部保留不stage；博客/oschina逐文件fresh baseline、origin/parity-results保护。WB-37及其旧失败窗口不重跑/改写，旧代理均完成，本轮只推进实际Studio native/bootstrap与Managed Local生命周期烟测。
- 实施代理`/root/wb34_runner`独占新增`web/e2e/run-studio-native-real.mjs`与必要的`web/e2e/studio-native-process.ps1`；`/root/wb33_ui`独立只读合同/源码/证据复核。根独占六共享文档、构建/真实run/审计/最终restore-format/stage/本地commit。生产Studio/Web/Server、旧mock spec与共享runner先冻结；实际失败若需生产修复先证据/最小放行，不猜测native双注册故障。
- 使用实际Studio exe/NativeWebApp/Win32/WebView2 Runtime154.0.4258.53，私有env CDP附着现有Playwright；实际native bootstrap/manifest和正常Workbench DOM身份、Health/Stop/Start、旧/新Server完整身份对拍，正常退出只用核验自有Studio CloseMainWindow。不得注入替换nativeWeb、fake事件/mock route/私有组件入口，API setup/login装实际token仅属认证准备、不计登录UI。正常退出与finally fallback清理分别记录。OS文件对话框/干净安装/升级卸载/全宿主旅程/三宿主完成均不在本片证据内。
- 新证据`artifacts/wb38-validation-20261007`根75分钟/16长命令/3真实run，两命名dotnet构建项目；各真实run600秒。子代理新窗口20:19Z至20:49Z，各25命名文件/25定向搜索（80结果/15秒）/12短只读shell各30秒，不自跑launch/build或写共享文档。全部循环/搜索/等待/重试有限次数/项目及墙钟、先微输入核比较退出；长进程PID/创建/完整command/父链、finally仅回收核验自有树，marker/绝对路径核验清理独占profile/data，审计/证据保留。PowerShell7固定pwsh；禁Graphify/广域扫描/未授权安装，两个政策保留Temp不碰，不fetch/push/发布/部署/外部沟通。
- 根按Web build→Server Release→Studio Release刷新产物，再冻结runner作最多三次真实烟测；不把旧binary/hash或fixture当本片新宿主证据。最终完整`dotnet restore SonnetDB.slnx`及原级别format通过后本地白名单commit，代码再改重跑，三宿主未闭环且唯一heartbeat ACTIVE每30分钟。本片不启动WB-39、不提前新建接续会话。
- 三run已用满：首轮CIM祖先查询超20秒，缓存只限当前helper、动作目标与父链仍fresh；第二轮默认窗CSS隐藏Health，另有JSON ISO日期被转DateTime的清理误拒；两个失败及原cleanup=false不改。实施代理12/12短读耗尽后明确转交两文件给`/root/wb34_ui`，仅修native宽窗参数/只读geometry/可见前提和`-DateKind String`；最终源码/hash与极小身份kill验证见source-freeze-final/final-source-review/micro-mutation-result。
- 第三run实际Edg154.0.4258.53，正常native bootstrap/manifest/connection identity、Health/Stop/Start与old/new Studio-owned Server PID通过；原生1920×1080对应CSS1266×663/DPR1.5，没有viewport注入、fake事件、mock route或强制点击。只证宽窗条件，默认/窄窗Health不可见仍为真实UI缺口。
- 第三正常关闭helper主窗口句柄为0、报`The owned Studio has no native main window.`，未执行CloseMainWindow；随后process-events超过512KiB使normal-exit/process-events/cleanup/result未保存，wrapper退出1。bridge/四DOM/截图/身份/Stop局部证据保留，根单独failure observation，不能把无终态/根回收改成实际生命周期PASS。独立review20:49Z硬截止只读到局部证据，完整终态未独立PASS。
- 根审计89记录身份0自有存活、三run九runtime目录均不存在，首两根remediation及第三28身份核验另存；原cleanup false/缺失不改，第三fallback数未知，短命捕获/外部祖先与缺process-events边界明确。Web build通过且保Vite chunk警告；两命名Release构建0警告/错误及三个index.html同hash。生产不改，八文件白名单/本HANDOFF仅顶部任务hunk，完整最终门禁与commit以证据/git log为准。
- 接续优先项：原生窗口关闭入口的可验证发现与终态证据分拆/保证落盘，独立有界切片重新真实验收；再默认/窄窗Health可用性与OS文件对话框。不得将helper无句柄直接判作Studio产品退出缺陷，不得在本耗尽窗口追加第四run。三宿主/安装/NativeAOT/硬件/长稳/发布仍分别未闭环。

### WB-37 合同与文件冻结（2026-10-07；本地切片已验证）

- 依赖WB-24/25既有Object读取/写终态、WB-16导航和WB-27/31隔离runner，从`4e7374ed`接续，不重做已有页面。初始三run窗口budget/失败/READ局部产物冻结；新接续`artifacts/wb37-continuation-20261007-01`19:57:17Z至21:12:17Z、根12长命令/3真实run，首run真实3/3（27.8秒/54秒wrapper）通过，旧三个失败不改为PASS。
- 原UI代理独占`web/e2e/object-real-permission.spec.ts`仅named parent最小追加，最终9EF343C4冻结后真实执行；原薄入口`web/e2e/run-object-real.mjs`不变。独立review新窗口25命名文件/30定向搜索/20:33Z截止、12只读shell各30秒（初始短命令提示歧义已修订），无自跑验证或另派代理。生产组件/API/Server/路由/共享runner/其它宿主冻结；根独占六共享文档/验证/审计/集成/git，八任务文件/HANDOFF只顶部任务hunk。
- 三serial正常UI旅程，无mock/skip/forced click/组件setup/prop/program-entry harness，API登录装普通真实非superuser token不计登录UI或宿主readonly props。READ151 seed/45秒、100/51 opaque continuation原key/typed DTO与管理员receipts相等、累计151，当前结果51/truncated；真实206 Range4096/8192和正常Download200/8192字节/hash/version、已有list/Range history相等，Download无自己的history。不称全bucket快照或Server预算；bucket合法小写，key原大小写/冒号保持。
- WRITE正常可见text/metadata/tags暂存→一次审批→实际PUT200完整Object DTO/69字节/MD5 ETag/SHA/opaque version；管理员独立Get实际bytes/hash/text与list DTO相等，原身份history1。Playwright Blob body不可观察明确unavailable/null，非null仍严格核原bytes，不称原始上传body观测。REVOKE旧审批实际403/管理员key404/先前批准值保持，清加载列表/selection/Range/可见草稿/审批；READ重授/同tokenSchema200/六tabs仍锁存，selectedBucket无新增请求/重放，Explorer可另刷新，不冒称显式恢复或未发生文件/image/Multipart草稿清理。
- 每test120秒/retries0、控制API10秒/browser响应与download事件15秒/生产Axios30秒分开；成功wx/manifest最多24份/1MiB/8MiB，两个明确ordinary named parents/直接run子目录/marker/realpath/凭据门禁。三JSON234671/13884/86826共335381，根typed结构比较与manifest核验；独立源码PASS、完整成功复核为提交门禁，实际见independent-success-review。
- 初始失败分别为正常Teleport class/Chart遗漏、实际未知提交PUT连接reset、正常Blob postDataBuffer=null观察假设；失败trace/typed PUT观察/1of3 manifest保留，reset原因未知/Vite stdio ignore限制保留。两次READ旧证据不计初始整体3/3，接续实际3/3单独记录；旧TS18环境诊断不计全typecheck。
- 真实后30记录身份0自有存活/无缺command，contentRoot/Chrome profile清理；旧wrapper短命/helper/外部父链限制保留。PowerShell7固定pwsh、禁Graphify/广域扫描/未授权安装，所有循环/搜索/等待/重试同时有项目/次数和墙钟、先小输入检查比较退出；长进程完整身份/父链和finally仅收核验自有树、临时绝对核验，两个policy Temp不碰。保护博客/oschina/HANDOFF外来尾hunk与origin/parity-results，无fetch/push/发布/部署/外部沟通。
- 最终八任务文件完整restore/原级别format/staged diff退出0才commit，代码改后重跑；实际门禁/树/提交见接续final-gates/final-tree-hashes/commit-checkpoint及git log。三宿主未闭环、完整权限/语义/Multipart/显式恢复/Server预算/OS对话框/安装/Extension Host/AOT/固定硬件/长稳/发行物另验；下一片优先Studio真实宿主/native bridge/Managed Local/文件对话框/生命周期，再VS Code开发者面。本片不启动WB-38，唯一heartbeat ACTIVE30分钟。

### WB-36 合同与文件冻结（2026-10-07；本地切片已验证）

- 依赖WB-35实际提交`9788645e1b47b538061f351f069479f7a0635c9f`；完整Raw接收最新HANDOFF/AGENTS/queue/ROADMAP/hash对拍，git/路线图/提交及旧代理核验，145记录身份0存活；3短command捕获缺口沿既有证据保留。博客三文件/HANDOFF尾hunk保留不暂存，WB-25～WB-35不重复，三宿主未闭环，唯一heartbeat ACTIVE每30分钟。
- 一片只修Graph精确顶点导出预算截断：先旧Release Server经正常UI导出maxElements151，保实际response/download/history与expected truncated=true的失败观察；生产源码放行前不得改Server/重建旧DLL。复用专属`/root/wb34_runner`只改`src/SonnetDB/Endpoints/Routes/GraphOperationsEndpoints.cs`和`tests/SonnetDB.Tests/GraphEndpointTests.cs`，先回归测试后等待根复现确认，最小边哨兵探测（remaining=0时PageSize=1，正remaining沿既有256，MaxResults=remaining+1；不称通用底层扫描预算）与最多8条有界Kestrel矩阵（空图/无边恰满/有边恰满/总量恰满/边不足/顶点不足/256页边界）验证。总元素上限、顶点优先、同read snapshot、JSON source generation、Graph Beta不改，不声称端到端Server扫描/字节/堆预算。
- 复用专属`/root/wb34_ui`仅改`web/e2e/graph-real-permission.spec.ts`：正常READ额外导出151，单独持久化精确预算观察（失败时仍保存实际false响应，不称PASS），成功max151必须151vertices/0edges/elementCount151/truncatedtrue及原身份history，与正常download对拍。保持10/1000、typed vertex、单Upsert/version+1和REVOKE锁存三旅程；只准named WB-35/WB-36两个绝对父目录/wx/manifest、24份/1MiB/8MiB，不扩大ID/导入/维护或其它宿主。薄入口/共享runner/生产Web/API/路由冻结不改。
- `/root/wb33_ui`专属只读复核最终Server/test/spec及真实观察/成功记录/进程证据，无源码或共享文档写入；根独立复核最小生产diff并独占六共享文档、构建/统一测试/真实复现/成功验证、进程审计、最终完整restore/原级别format、stage/commit。只九任务文件，HANDOFF只任务顶部hunk；完整最终门禁退出0，代码再改重跑，无重复commit。
- 根18:48:13Z起75分钟至20:03:13Z，最多16长命令/3真实run；代理35分钟至19:23:13Z、25命名文件/30定向搜索（每80匹配/15秒）、短语法最多2次各30秒，禁止自跑长验证/另派代理。PowerShell7固定`C:\Program Files\PowerShell\7\pwsh.exe`；禁止Graphify/广域工具扫描/未授权安装。循环/搜索/等待/重试同时限次数/项目与墙钟、先小输入核退出比較；长进程记录PID/创建/完整command/父链、finally仅回收核验自有树，临时绝对路径核验清自有对象，交付物保留。两政策保留Temp不删除/重试/绕过；fixture/真实Kestrel/API测试/新Web/Release分析/NativeAOT发布/安装/三宿主/固定硬件/长稳/发行物分开。保留当前origin/parity-results，外部fetch更新不当本任务改写或要求改回旧hash；不fetch/push/发布/部署/外部沟通。本轮不启动WB-37，完成后接Object新UI/Studio/VS Code剩余队列。

- 旧DLL真实READ151在truncated断言expectedtrue/actualfalse失败：200/snapshot1/count151/151顶点0边，response/download和原身份history保存202301字节独立观察，不完整manifest不称成功；WRITE/REVOKE未运行。根核旧源码/DLL不变后才放行两行生产修复，最终生产9905FDA6/test5BFB080F/spec318A8D44冻结且独立源码/旧观察复核PASS。
- 根Server Release0警告错误/trim-AOT分析通过（非NativeAOTpublish），GraphEndpointTests17/17含8条新增有界Kestrel矩阵全PASS；修复后真实首轮3/3、20.8秒测试/49秒wrapper/retries0，151导出实际truncatedtrue、151v/0e及原下载/history对拍，保10/1000、WRITE和REVOKE。成功四JSON1015208字节/manifest根核验；独立完整成功/进程复核为提交前置。根operational checker初次误把boundary缺requests当null请求，记录后仅修checker通过，源码不改，不增加true run。
- 真实后66身份0自有存活、4短command缺口保留，两contentRoots/两Chrome profiles清理；最终审计/完整restore与原级别format/staged diff/九文件本地提交见`artifacts/wb36-validation-20261007`的final-gates/final-tree-hashes/commit-checkpoint。旧WB-35全Web/fixture/build不算本片新证据；博客三文件、新oschina目录及HANDOFF外来尾hunk保留不暂存。下一片Object新UI真实旅程，WB-25写终态不重做，再接Studio/VS Code；三宿主未闭环，唯一heartbeat回读ACTIVE/30分钟，不提前迁新会话或启动WB-37。

### WB-35 合同与文件冻结（2026-10-07；本地切片已验证）

- 从WB-34实际提交`328378d8c0d7130b1dde302dd4a1d2c2eff8793f`接续；最新HANDOFF/AGENTS/queue完整Raw接收并与上一片final-tree hash对拍，路线图/git/已提交事项及旧代理核验，100记录身份0自有存活，旧两代理完成。博客三文件和HANDOFF的博客/OSChina外来尾hunk保留不暂存；WB-25～WB-34不重复，origin/parity-results保护，三宿主未闭环，唯一heartbeat ACTIVE每30分钟。
- 仅新增Graph Beta新Web→隔离Release Kestrel真实三旅程证据，依赖WB-23/WB-16和WB-27/31共享runner。复用专属`/root/wb34_runner`独占新增`web/e2e/run-graph-real.mjs`薄入口，冻结后兼任只读Server/API/spec/成功证据复核；根独立复核四参数薄入口。复用专属`/root/wb34_ui`独占新增`web/e2e/graph-real-permission.spec.ts`。不再次触发已知thread总数上限，不重复派单，最多三活动子代理。生产Graph组件/Node/API/Server/共享runner/路由/其它宿主先冻结；真实兼容缺口先回报根再冻结最小归属。
- 最多三个serial真实旅程，各120秒/retries0、控制API10秒、browser响应/下载事件wait15秒、生产Axios30秒分开，无mock/skip/prop/program-entry harness。普通非超级用户READ经真实API登录，最多151vertices/150edges管理员有界batch seed；合法MixedCase Graph保原名，Graph Beta标记保留，实际overview/vertex limit10与大窗≤1000、客户端总元素预算/边端点、typed元素读取，以及正常JSON导出maxElements10/1000的各真实snapshot/truncated与原database+Graph/profile历史对拍。画布可通过已正常加载ECharts的公开getInstanceByDom/getOption只读观察实际series，不调用组件setup/修改props或图实例，不提供测试执行器；导出独立Server snapshot不等于画布，不伪造browse history或全图分页/Server资源预算；具体已有history合同以只读复核确认。
- 普通WRITE经正常受限编辑器读已有安全ID/当前version，Stage Upsert并一次确认，实际sequence/isDuplicate终态与管理员Get原ID/属性/新version独立对拍，原身份history保持；只覆盖单vertex Upsert，不冒称完整Int64/edge/delete/import/maintenance矩阵。第三旧vertex审批实际REVOKE403清当前canvas/overview/element/editor/import/approval，管理员确认拒写属性未落库/已批准值保留；READ重授/同tokenSchema200与internal section tabs仍锁无selectedGraph模型请求/重放。真实import草稿仅通过正常可见文本框填写不staged，维护审批未发生则不称清理；显式恢复另片。
- 成功JSON仅核验绝对named`artifacts/wb35-validation-20261007`直接graph-real run目录/wx/manifest，最多24份/单1MiB/总8MiB，拒保存凭据；固定合法safe-number ID只计这一子集，不弱化Graph完整Int64身份待办。真实Server、fixture、三宿主/OS/安装/Extension Host/AOT/固定硬件/长稳/发行物分开，不改变Graph Beta/数据库逻辑资源合同。
- 根独占六共享文档、统一验证/审计/集成/最终完整restore/原级别format/stage/commit。18:13Z起75分钟至19:28Z，根最多16长命令/真实run3次；代理35分钟至18:48Z，最多25命名文件/30定向搜索，每次80匹配/15秒，短语法命令最多2次/单30秒，不自跑长验证或另派代理。共享runner10分钟/readiness120次60秒，Node每项5秒，fixture每项30秒。最终树完整restore/format必须退出0才commit，代码再改重跑；本轮不启动WB-36。
- 固定PowerShell7 `C:\Program Files\PowerShell\7\pwsh.exe`并核版本；禁止Graphify/广域工具扫描/未授权安装。循环/搜索/等待/重试同时限定项目或迭代数与墙钟，先极小输入人工核退出比较；长进程记录PID/创建时间/完整command/父链，finally仅回收核验自有树。临时绝对路径先核验后清自有对象，交付物保留，两政策保留Temp不删除/重试/绕过。共享文档/git/集成根串行，保护其它会话/保留origin/parity-results，无fetch/push/发布/部署/外部沟通。

- 只读Server复核发现独立候选缺口：GraphOperationsEndpoints.cs导出顶点数恰maxElements、仍有边时，written<maxElements条件不进入edge scan，可能误报truncated=false（源码级发现，尚未定向真实复现）。本片仅10/1000所测预算，不称通用export边界正确性、不用PASS固化错误行为、不扩改Server。下一片优先冻结此边界真实复现/最小修复，再接Object/Studio/VS Code剩余事项。元素version按expected+1，与事务sequence分离；GET/PUT元素端点不自行保证Content-Type，证据核实际有效JSON及headers，不假造额外HTTP合同。
- 最终本地证据：Node27/27、全Web327/327、TypeScript/Vite、Graph Chrome fixture16/16通过；新Web→隔离Release Kestrel第三轮3/3、20.1秒测试/47秒wrapper。前两run各READ失败/后两未执行：页签动态badge与precision0 ID正常提交遗漏；专属spec修正常可见label和fill/Enter/Tab/按钮enabled，不改生产或降业务断言，失败记录保留。最终016E49DB hash写入早于第三启动且启动前source-freeze实际匹配，后续冻结不改。
- 实际Canvas250/10/1000系列/端点、typed vertex和独立snapshot导出10/1000/download/history对拍；单vertex WRITE一次审批sequence/isDuplicate、管理员Get/version1→2；旧vertex审批REVOKE403清已加载画布/overview/typed editor/正常可见import文本草稿，重授READ/同tokenSchema200/五tabs保持锁存无selectedGraph重放。没有预填选中Canvas inspector，不能称移除了其已有载荷；无browse history/rowCount/影响数或实际import/maintenance矩阵声明。
- 成功run`graph-real-2026-10-06T18-31-45-914Z-c67b7272-c73b-4b2e-ad1c-222389768e75`三JSON共811899字节/manifest尺寸hash和凭据拒写根核验通过；独立完整源码/成功证据复核为提交前置。真实后96记录身份0存活，四Chrome profiles/三contentRoots不存在；累计3短descendant缺command/外部祖先与wrapper短命捕获限制单列。最终八文件完整restore/原级别format/staged diff退出0才提交，实际日志/树/hash/提交见`artifacts/wb35-validation-20261007`；本轮不启动WB-36，下一片优先Graph恰满预算导出候选缺口，再接Object/Studio/VS Code，三宿主仍未闭环。

### WB-34 合同与文件冻结（2026-10-07；本地切片已验证）

- 从`main / 6bc0fea1022562ca8128911d2e222217d52b55ee`接续；HANDOFF/AGENTS/queue完整读取并hash对拍WB-33最终树，路线图/git/已提交事项与旧代理核验，119身份0自有存活。博客三文件/HANDOFF尾hunk保留不暂存，不重复Object写终态WB-25及WB-26～WB-33；origin/parity-results保护，三宿主未闭环，唯一heartbeat ACTIVE每30分钟。
- 仅新增MQ新Web真实旅程证据，依赖WB-22/WB-16与WB-27/31共享隔离runner。专属`/root/wb34_runner`独占新增`web/e2e/run-mq-real.mjs`薄入口；`/root/wb34_ui`独占新增`web/e2e/mq-real-permission.spec.ts`；`/root/wb34_runner`在薄入口冻结后兼任只读Server/API/UI合同和最终成功证据复核；根独立复核薄入口（新建review及复用wb33_review均被thread总数上限拒绝，不再重试或重复派单）。生产MQ组件/Node/API、Server/路由/共享runner/其它宿主不改；真实缺口先回报根冻结最小兼容文件归属，不擅自扩写。根独占六共享文档、统一验证/审计/集成/完整restore-format/stage/commit；最多三个活动子代理，不重复派单。
- 最多三serial真实旅程，各120秒/retries0、控制API10秒/browser响应下载wait15秒/生产Axios30秒分开；无mock/skip/prop harness，API登录装入普通非超级用户的真实token，不计登录UI/readonly宿主props或OS对话框。151条有界admin种子（优先真实publish-batch）保合法MixedCase Topic原名/大小写；真实Server Topic拒绝冒号400，冒号保真仅既有fixture证据，不改Server，普通Browse fromOffset0/100与maxCount100取得100/51当前窗口，timestamp/header/Base64 payload/当前JSONL及原database+Topic/profile history逐项对拍；不把当前窗口当全实例快照或Server扫描/物化/解码/字节/堆预算。
- 普通WRITE通过正常UI分别审批一次Publish与Ack，实际响应topic/安全offset及consumerGroup/nextOffset匹配批准输入；管理员Browse/Offsets独立对拍，history保原身份/实际影响，不冒称完整Int64/Nack/重启恢复矩阵。旧Publish审批真实REVOKE后实际403清messages/header/metadata/trend/result/editor/approval；import仅真实常驻native file input为空、导入按钮disabled，未发生的导入草稿不称清理，管理员确认拒写payload未落库；READ重授/同tokenSchema200及页签保持锁存、selectedTopic路径不请求/重放；parent Explorer可刷新Topic列表，显式恢复另片。不调用私有程序入口伪造真实流程，若正常UI路径存在缺口先回报。
- MQ逻辑scope=database、identity含database+Topic、persistenceScope=instance与物理.system/mq基线不改。独占Server实例contentRoot/DataRoot仅本次验证，单库备份不冒称MQ恢复；fixture、真实新Web、Studio/VS Code/OS/安装/Extension Host/AOT/固定硬件/长稳/发行物分别记录。成功JSON在核验绝对runRoot/wx/manifest内落盘，最多24份/单1MiB/总8MiB，拒保存凭据；仅named`artifacts/wb34-validation-20261007`直接run子目录。
- 17:45Z起总75分钟至19:00Z，根最多16长命令/真实run3次；代理35分钟至18:20Z、最多25命名文件/30定向搜索，每次80匹配/15秒，不自跑长验证/另派代理。共享runner10分钟/readiness120次60秒，Node每项5秒，MQ fixture每项30秒/retries0。根完整最终restore及原级别format退出0才commit，代码再改重跑；不启动WB-35。
- PowerShell7固定`C:\Program Files\PowerShell\7\pwsh.exe`并核版本；禁Graphify/广域工具扫描/未授权安装。所有循环/等待/搜索/重试有项目/迭代数和墙钟、先极小输入核对比较条件；长进程记录PID/创建/完整command/父链，finally仅回收已核验自有树。临时路径绝对解析核验后仅清自有对象，交付物保留；两政策保留Temp不删除/重试/绕过。根串行共享文档/git/集成，保护其它会话与origin/parity-results，不fetch/push/发布/部署/外部沟通。

- 两源码已冻结，独立薄入口/Server/spec复核PASS；Node22/22、全Web327/327、MQ Chrome fixture16/16、TypeScript/Vite及真实首轮3/3（21.5秒/runner43秒）通过。READ100/51当前JSONL/history、WRITE正常两审批/管理员对拍、旧审批REVOKE403与READ重授/同tokenSchema200/四页签锁存；冒号Topic真实400、无实际文件导入/仅空input与禁用导入按钮、parent Explorer Topic列表可刷新等边界单列。成功三JSON195806字节/manifest根核验、独立成功复核为提交前置；证据artifacts/wb34-validation-20261007，51记录身份0自有存活/无缺command、contentRoot/两Chrome profiles清理，短命捕获限制保留。最终完整restore/原级别format及八文件提交以final-gates/commit-checkpoint/git log为准，代码再改重跑，博客三文件/尾hunk保留，本轮不启动WB-35。

### WB-33 合同与文件冻结（2026-10-07；本地切片已验证）

- 从 `main / 89744d29646463a365e019d39e74410229ba8853` 接续已闭环WB-32；HANDOFF/AGENTS/queue完整读取，前两共享记录与WB-32最终树hash一致，路线图/git/已提交事项及代理核验完毕。104条已记录身份无自有存活，复用PID保留；无活动旧子代理。博客三文件及HANDOFF尾hunk仍属其它会话，不暂存/覆盖；Object WB-25与WB-26～WB-32不重复。
- 本片只修Vector嵌入Measurement子页实际401/403或精确SQL权限终态未上行的问题。子页只在当前有效请求/原身份确认本地拒绝时发出有界typed通知，绑定database/原measurement与父级渲染代际，不携带正文/凭据；继承父permission prop不重复上行。父页仅接受当前child代际/身份通知，沿现有lock清hits/metadata/共享结果及子页载荷/草稿/审批；同tokenSchema/认证刷新、页签重挂、空身份ABA仍锁存，不自动恢复或重放。迟返、卸载、错误目标/代际事件不锁新身份；保留正常/readonly/已确认写进度及原身份history，已派写不声称回滚。
- 文件归属冻结：`/root/wb33_impl`独占`web/src/components/MeasurementWorkbench.vue`、`web/src/components/VectorSearchWorkbench.vue`、`web/tests/measurement-workbench-migration.test.mjs`、`web/tests/vector-workbench-migration.test.mjs`；`/root/wb33_ui`独占`web/e2e/vector-workbench-migration.spec.ts`与`web/e2e/vector-real-permission.spec.ts`最小增量；`/root/wb33_review`独立只读复核。真实spec复用WB-32前两旅程，第三改为child SQL403触发父锁/清旧hits与Schema200/重挂仍拒绝，旧成功证据保留；证据父路径仅明确允许WB-32/WB-33两个核验目录，沿绝对runRoot/wx/manifest/凭据门禁。不新增同页或重复包装整页，Server/API/共享runner/路由/其它宿主不改；根独占六共享文档、统一验证/审计/集成/restore-format/stage/commit。
- 2026-10-06T17:14Z起总75分钟至18:29Z、根最多16条长命令/真实run最多3次；代理35分钟至17:50Z、最多25命名文件/30定向搜索，每搜索最多80匹配/15秒，不自跑长验证/另派代理，最多三活动子代理。Node每测试5秒，fixture每测试30秒/retries0；真实三旅程每测试120秒、控制API10秒/browser响应下载等待15秒/生产Axios30秒分别记录，共享runner10分钟/readiness120次60秒。代码最终完整restore/原级别format通过后不改代码，改后重跑。
- PowerShell7固定 `C:\Program Files\PowerShell\7\pwsh.exe`；禁Graphify/广域工具扫描/未授权安装，所有循环/搜索/等待/重试同时限定次数/项目与墙钟、先极小输入人工核对比较退出。长进程登记PID/创建/完整command/父链、finally仅回收已核验自有树；临时路径绝对核验清理，交付物保留，两政策保留Temp不删除/重试/绕过。保留origin/parity-results，无fetch/push/发布/部署/外部沟通。真实Server与fixture、三宿主/OS/安装/Extension Host/AOT/固定硬件/长稳/AI/MCP/发行物继续分开，整体未闭环，更新后的唯一heartbeat保持ACTIVE。
- 六源码最终冻结、两实施代理停止写入，独立源码复核PASS；Node39/39、全Web327/327、Vector fixture13/13、独立Measurement fixture20/20与TypeScript/Vite通过。真实Vector首轮3/3：Top-K20/100/当前导出/原history与Search403回归保留，第三真实child SQL403清父旧结果/metadata及child草稿；READ重授/同tokenSchema200/子页重挂仍锁无请求重放。真实READ草稿与fixture写审批清理分开，不冒称真实写审批；超时写迟返拒绝不上行而保留原身份unknown。
- 根证据`artifacts/wb33-validation-20261007`，成功run `vector-real-2026-10-06T17-26-36-929Z-282e1447-f243-4385-84f1-da865a85ca76`三JSON146148字节/manifest根大小/hash与凭据门禁通过；独立成功证据复核PASS（1000单元/两CSV/102帧、25自有身份/264父链边/退出与清理核验），最终完整restore/原级别format/staged检查为提交前置，十二文件实际提交见final-gates/commit-checkpoint。门禁前70身份0自有存活、数据根/三Chrome profiles清理；短命捕获限制仍单列。Server复用WB-32Release不改，Profile/恢复/完整写终态/Server预算/三宿主另验，本轮不启动WB-34。

### WB-32 合同与文件冻结（2026-10-07；本地切片已验证）

- 从 `main / 16acfafc8811a16503fdf71aaddab5efd2863b22` 接续；最新HANDOFF/AGENTS/queue、路线图/git与三旧代理已接收核验，WB-31提交及135身份无自有存活已复核。WB-25 Object写终态与WB-26～WB-31不重复；三博客文件/HANDOFF尾hunk保留不暂存。只补Vector新Web→隔离本机真实Kestrel的原始向量Top-K/当前导出及实际撤权锁存，不重做页面，不实现索引Profile或显式权限恢复。
- 依赖WB-20、WB-16、WB-27共享真实runner及WB-28成功证据合同。文件归属：`/root/wb29_runner`独占新增`web/e2e/run-vector-real.mjs`；`/root/wb29_ui`独占新增`web/e2e/vector-real-permission.spec.ts`；`/root/wb28_review`独立只读复核真实Handler/SQL/API与最终成功证据。生产组件/API/共享runner/Server/路由/其它宿主不改，真实兼容缺口先回报根冻结最小归属。根独占六共享文档、验证/审计/集成/完整restore-format/stage/commit；最多三活动子代理，不再另派。
- 最多三真实旅程，每测试120秒、控制API10秒/browser响应与下载事件wait15秒（生产Axios30秒单列）、retries0、无mock/skip/prop harness；种子最多151行、维度3、Top-K20/100、保持database/measurement/column原名及既有深链接。检索实际响应/原始metadata、当前JSON/CSV下载和原身份history逐项对拍；不称全索引分页/快照/Recall/模型质量或Server扫描/物化/传输/字节/总堆预算。普通非超级用户READ会话实际REVOKE后search-preview403清旧hits/metadata/结果及子页；重授READ/同tokenSchema200仍锁存且不重放，独立读取拒绝路径按实际API可行性验证，不伪造恢复。
- 独立合同盘点确认Measurement子页无permission上行事件；第三旅程只验子页SQL403清本子页载荷/锁存，不能声称外层Vector同步锁存或清旧hits。外层search403由第二旅程验；子页拒绝后外层旧命中可仍显示的缺口保留到下一生产修复切片，不用text/Profile未就绪的本地门禁替代真实读取。
- 首轮真实3/3、全Web321/321、Vector fixture10/10、TypeScript/Vite与Server Release0警告错误通过；READ L2 Top-K20/100、原timestamp/TAG/FIELD/index参数、当前JSON/CSV与history逐项对拍，Search403清外层载荷且regrant READ/同tokenSchema200仍锁存；第三旅程仅当前child SQL403，外层旧hits仍保留，Schema/view重挂载后的子页局部锁不作覆盖。根验证三JSON146028字节及manifest一致，源码/完整成功证据独立只读复核为最终提交前置。证据 `artifacts/wb32-validation-20261007`；48身份0自有存活、真实数据根/记录Chrome profile清理，PID90972短命command捕获缺口和旧wrapper短命worker可能漏捕获单列。
- 控制API10秒、browser响应/下载事件等待15秒与生产Axios30秒分开；首Node缺VM modules参数的失败保留，修命令后321/321。生产/共享runner不改，八任务文件最终树完整restore/原级别format/staged检查退出0才本地提交，实际结果/hash见final-gates/commit-checkpoint及git log。下一片WB-33优先父子权限上行与锁存最小修复，索引Profile/恢复/Server预算/其它九模型真实UI与Studio/VS Code/发行物继续单列；本轮不启动WB-33。
- 2026-10-06T16:27Z起总75分钟、根最多16条长命令/真实run最多3次；代理35分钟/最多25命名文件/搜索至多30次，每搜索最多80匹配/15秒，不自跑长验证。共享runner10分钟、readiness120次60秒；代码最终门禁须完整restore及原级别format，变代码后重跑。成功证据核验绝对runRoot、wx落盘、24份/单1MiB/总8MiB与manifest，凭据不落盘。
- PowerShell7固定路径；禁止Graphify/广域工具扫描/未授权安装，所有循环/搜索/等待/重试有项目/次数及墙钟上限、先极小输入核对退出比较。长进程记录PID/创建/完整命令/父链，finally仅清核验自有树，临时文件解析绝对路径后清理；两策略保留Temp不删除/重试/绕过。保留origin/parity-results，不fetch/push/发布/部署/外部沟通。fixture/真实Server/三宿主/OS/安装/Extension Host/AOT/固定硬件/长稳/AI/MCP/发行物分开；三宿主未闭环，heartbeat ACTIVE。

### WB-31 合同与文件冻结（2026-10-06；本地切片已验证）

- 从 `main / 866dd04b`接续WB-30；08:04 heartbeat完整接收HANDOFF/AGENTS/queue并核对git/路线图/已存在提交/三旧代理及进程。Object WB-25与WB-26～WB-30不重复；三博客文件及HANDOFF尾hunk保留不暂存。只补Measurement新Web→隔离真实Kestrel的当前读取/导出、正常审批写终态子集和实际撤权锁存，不改生产组件、SQL/API、Server、路由或其它宿主。
- 文件归属：`/root/wb29_runner`独占新增 `web/e2e/run-measurement-real.mjs`、追加冻结 `web/e2e/run-workbench-real.mjs` 审计/日志最小修复与 `web/tests/workbench-real-runner-audit.test.mjs`；`/root/wb29_ui`独占新增 `web/e2e/measurement-real-permission.spec.ts`；`/root/wb28_review`独立只读复核源码及最终成功证据PASS。三个实施/复核源任务已停止写入，根独占六共享文档、验证/进程审计、集成/restore/format/stage/commit，仅十任务文件。没有重复源码派单或启动WB-32。
- 四真实旅程各120秒、HTTP10秒、retries0、无mock/skip/prop harness，真实runner10分钟/readiness120次60秒；有界501seed→100/500与分钟200～260的61行、原名/typed参数及当前JSON/CSV逐行比较。普通非超级用户WRITE经正常CSV解析/审批，一次batch两个INSERT完整end各affected1，管理员独立SELECT与原身份history2。旧导入审批实际REVOKE403拒写不落库、清点/monitor/Schema/editor/import/审批；重授READ/同tokenSchema200仍锁存且不重放；独立monitor403清载荷并拒后续模型请求。最终4/4，Document共享runner真实回归3/3单列。
- 沿用WB-30点/monitor最多500、auto12轮/60秒、审批1000语句/10批/60秒。SQL LIMIT/preview与当前导出不计全measurement快照/Server扫描/物化/传输/字节/总堆预算；API安装token不计登录UI/readonly宿主props，逐INSERT子集不计完整COMMIT/权威影响数/Int64/恢复或其它权限矩阵。成功证据在已核验runRoot独立wx落盘，24份/单1MiB/总8MiB、marker/凭据门禁及下载finally清理保持。
- 共享runner只将日志I/O移出3秒纯遍历：4096snapshot/128identity/16depth/300audit及PID复用/父链不放宽，发现时先登记身份与时间，异常已登记增量仍独立10秒日志落盘，磁盘完整parentChain/stdout短摘要，auditPending覆盖全程。顶层失败最多两安全原因，未知正文/stack/argv/凭据不输出。Windows受控stdout立即读0ms/延迟读5192ms证明原日志阻塞，append/console占比未分拆。审计Node10/10、全Web321/321；源码未变产品Measurement16/16、Chrome fixture20/20、TypeScript/Vite及Server Release0警告/错误复用。
- 最终Measurement成功run `measurement-real-2026-10-06T07-27-11-732Z-e1b21440-21ca-4d3c-8378-1cd60d3adf73`，四JSON396558字节/manifest根与独立复核一致；Document run `document-real-2026-10-06T07-02-00-347Z-56387898-27da-47da-991c-af068bbc43aa`。两pre-browser遍历失败及DDL DOUBLE/实际FLOAT、datetime-local零秒Malformed value失败保留，仅spec按实际合同修正后四旅程通过。合并131身份0自有存活、6contentRoot/4记录Chrome profiles清理；短命command、syntax身份与诊断父链捕获缺口继续单列，不冒称完整捕获。
- 根旧恢复验证7次，但UTC/本地DateTime比较错误造成43.49分钟超35分钟；修为DateTimeOffset.UtcDateTime、保留过期预算和错误记录。本次08:04 heartbeat另开30分钟/最多3长命令的final-gates-only窗口，只完整restore/原级别format与本地commit，不复跑已通过旅程。最终待提交十任务文件完整门禁和staged diff须退出0才提交；实际命令/退出/树hash/提交与最终进程审计见 `artifacts/wb31-validation-20261006/final-gates.json`、`final-tree-hashes.json`、`commit-checkpoint.json`及 `audit-closeout-after-commit.json`，提交说明 `test(m47): verify Measurement workbench against real Server`，哈希以git log为准。
- PowerShell7；禁止Graphify/广域工具扫描/未授权安装，搜索/循环/等待/重试明确项目/次数与墙钟并先小输入。长进程记录PID/创建/完整命令/父链，finally仅清核验自有树，临时路径绝对核验；两政策保留Temp不删除/重试/绕过。保留origin/parity-results，不fetch/push/发布/部署/外部沟通；真实Server、fixture、三宿主/OS/安装/Extension Host/AOT/固定硬件/长稳/AI/MCP/发行物分别记录。三宿主仍未闭环，heartbeat ACTIVE。

### WB-30 合同与文件冻结（2026-10-06；Measurement 客户端切片已验证）

- 从 `main / 46d3f513` 接续，完整接收最新HANDOFF/AGENTS/queue且哈希与WB-29最终树一致；WB-25～WB-29及旧代理均完成，不重复派单。三博客文件与HANDOFF末尾博客hunk继续保留不暂存。源代码盘点发现Measurement权限仅从错误文本推导，监控错误不进入统一锁存，读取/写审批/文件迟返缺少完整会话代际；本片只补客户端权限/请求/审批隔离与现有预览上限，不重做页面或完整写执行器。
- 专属实施代理独占 `web/src/components/MeasurementWorkbench.vue` 与 `web/tests/measurement-workbench-migration.test.mjs`；UI代理独占新增 `web/e2e/measurement-workbench-migration.spec.ts`，以及必要的 `web/e2e/management-workbenches.spec.ts` Measurement夹具兼容hunk；第三代理独立只读复核真实SQL/helper合同与最终diff。已存在SQL helpers支持signal，保持共享API/Server/路由/其它宿主不改；根独占六共享文档/验证/集成/完整restore-format/stage/commit。每代理25命名文件/35分钟，不自跑长验证或另派代理，最多三个活动子代理。
- 原database/measurement/monitor目标与六态、旧SQL/深链接保留。HTTP401/403或精确授权错误来自点读取、监控或写均清点/监控/Schema可见载荷、结果、editor/import/审批并锁存；错误正文固定脱敏，同身份Schema/auth刷新、空身份往返不解锁，显式恢复另片。readonly保留读取/导出，所有暂存/文件/确认程序与按钮入口禁写。
- 冻结实际API/endpoint/auth/token/profile/database/measurement与同步epoch，隔离迟返/跨库同名/ABA/新读/卸载；监控model/target/limit亦独立代际，旧finally不得清新busy。新请求使用既有optional signal及SQL previewMaxRows，分派前取消可查adapter证据；已派写取消不冒称回滚，历史保留原目标与unknown，不恢复旧审批。上下文/权限/readonly失效清草稿，文件选择迟返不填新身份；正常同上下文主动停止的已确认导入进度与重新审批兼容另行核对，不扩为完整COMMIT/影响数/Int64写终态矩阵。
- 点与监控沿用最多500行，先截断再map/显示/导出，保留Server截断及当前预览范围；非全measurement快照，扫描/物化/传输/字节/总堆预算另验。监控最多12轮/60秒，timer归属与finally清理；审批导入准入最多1000语句、最多10批、60秒新批次窗口，不扩完整文件字节/解析内存预算。浏览器夹具每测试30秒/retries0，根同仓库Vite/build/Playwright串行、每runner10分钟/本片验证35分钟窗口；fixture与真实新UI Server/三宿主/安装/Extension Host/AOT/硬件/长稳/发行物分别记录。
- 仅PowerShell7；禁止Graphify/广域工具扫描/未授权安装；搜索/循环/等待/重试设置明确项目/迭代及墙钟、先极小输入检查退出比较；长进程记录PID/创建/完整命令/父链，finally仅回收已核验自有树，临时绝对路径核验清理。两处策略保留Temp不删除/重试/绕过；完整最终树restore、原级别format/staged diff通过才本地提交，保留当前origin/parity-results、不fetch/push/发布/部署/外部沟通。三宿主仍未闭环，heartbeat保持ACTIVE，本轮不启动WB-31。
- 最终组件/Node/UI四文件冻结，第三只读复核PASS。专属Node16/16（实际Axios同tick取消adapter0与独立同结构Schema clone）、全Web311/311、TypeScript/Vite、Chrome新fixture20/20及旧Measurement/Vector子页/monitor8/8通过；既有真实Kestrel SQL预览端点3/3属共享SQL/关系表兼容，不计Measurement新UI真实权限。点/monitor501→500、当前导出、主动stop后100确认+fresh1审批、unknown原身份与auto12/60有行为证据。
- 首Node0/15为夹具Vue exports静默前100截断漏ref，修为256项/1秒完整绑定；独立复核另发现生产同结构Schema引用刷新不失效，已增加引用watch+保留JSON。首Chrome18/20仅loading accessible name定位不匹配，修两locator并保留busy/no secret/gate断言；最终20/20。日志/失败trace与源码hash在artifacts/wb30-validation-20261006，门禁前46身份/0自有存活，三Chrome profiles和测试数据根/编译响应目录清理，短命Node command捕获缺口单列。根完整restore/原级别format/staged diff退出0才提交十任务文件，实际结果见final-gates/commit-checkpoint；raw markRaw defaults无信号endpoint ABA、新UI真实权限、完整写终态/Server预算/三宿主分别待验。

### WB-29 合同与文件冻结（2026-10-06；本机 KV Web 旅程已验证）

- 从 `main / f03bbfd9` 接续，完整读取最新HANDOFF/AGENTS/queue且SHA256与WB-28最终工作树一致；WB-25～WB-28与三个旧代理已完成，不重复派单。三博客文件及HANDOFF末尾博客hunk属于其它会话，保留不暂存。只补KV新Web→隔离本机Kestrel的真实cursor/Get/round-trip、条件写/交换终态子集及撤权锁存，依赖WB-21、WB-16、WB-27共享真实runner与WB-28成功证据合同；不重做页面，不改Server/路由/其它宿主。
- 专属实施代理独占新增 `web/e2e/run-kv-real.mjs`；专属UI代理独占新增 `web/e2e/kv-real-permission.spec.ts`；第三代理独立只读复核。真实兼容缺口先回报并追加冻结，候选只限同实施者 `web/src/components/KvKeyspaceWorkbench.vue` 与 `web/tests/kv-workbench-migration.test.mjs` 最小修复；不得预先扩大成完整atomic合同。根独占六共享文档/验证/集成/审计/完整restore-format/stage/commit，每代理25命名文件/35分钟，不自跑长验证或另派代理，最多三个活动子代理。
- 三真实浏览器旅程，各120秒、HTTP10秒、retries0、共享runner10分钟/readiness120次60秒，根最多三次真实run/35分钟验证窗口。最多151种子key、一项条件写目标和一项拒写目标；原database/keyspace/key大小写及冒号保持。Scan最多100每窗沿实际opaque cursor加载尾页，Get实际Base64/版本，round-trip只导出已加载记录；不称全keyspace快照、Server扫描/物化/传输/字节/总堆预算。
- 普通非超级用户的数据库WRITE会话经正常UI分别批准NX成功、NX未应用和交换（需要时精确get-and-delete收口）最多四操作，每审批一次消费；实际响应/versionText/previousVersionText/mutationVersionText、原目标history及管理员独立Get共同查证，不把HTTP200/applied=false称影响一项或重放审批。完整atomic/Int64精度/TTL/CAS矩阵另片。
- 已读载荷和待批准写操作遭实际REVOKE后确认403，清值/统计/cursor/结果/写草稿/导入/审批；重授READ及同tokenSchema刷新保持锁存不重放。显式安全恢复另片，API登录不计登录UI/host readonly props。成功响应/下载/history按核验绝对runRoot单独落盘，最多24份、每份1MiB、累计8MiB，拒写凭据并生成SHA256 manifest。
- 仅PowerShell7；禁止Graphify/广域工具扫描/未授权安装；所有搜索/循环/等待/重试明确项目/迭代上限与墙钟，先极小输入检查退出比较；长进程记录PID/创建/完整命令/父链，finally仅清核验自有树。临时绝对路径核验回收，两处策略保留Temp不删除/重试/绕过。fixture/真实服务/三宿主/安装/Extension Host/AOT/硬件/长稳/发行物分开；最终树完整restore/原级别format/staged diff退出0才本地提交，保留origin/parity-results、不push/发布/部署/外部沟通。heartbeat保持ACTIVE，本轮不启动WB-30。
- 首轮 `kv-real`真实3/3、退出0、无retry/skip；Node17/17、全Web300/300、Chrome fixture12/12、TypeScript/Vite与Server Release0警告/错误通过。Scan100+opaque cursor尾51/Get2/JSONL逐行对拍，NX成功versionText2、未应用影响0/管理员Get不变、交换previousVersionText2/mutationVersionText3及history绑定；旧导入审批REVOKE403清载荷，重授READ/同tokenSchema200仍锁存不重放。三成功JSON95740字节+manifest根核验，48身份0自有存活，数据根/Chrome profiles清理；证据 `artifacts/wb29-validation-20261006`。生产组件/API/Server/共享runner不改，八任务文件最终完整门禁为本地提交前置，实际结果/提交见final-gates/commit-checkpoint；瞬时原子结果表展示、完整atomic/Int64/TTL/CAS/显式恢复/Server预算/三宿主另验。

### WB-28 合同与文件冻结（2026-10-06；本机 FullText Web 旅程已验证）

- 从 `main / 53cb9df4` 接续，WB-25～WB-27均已提交、旧代理均结束；三博客文件及HANDOFF末尾博客发布hunk属于其它会话，保留不暂存。本轮只补FullText新Web UI→隔离真实本机Kestrel的Top-K/当前结果导出、同步重建审批终态及实际撤权锁存，依赖WB-19、WB-16与WB-27共享真实runner，不重做页面，不改Server、路由或其它宿主。
- `/root/wb28_runner`独占新增 `web/e2e/run-fulltext-real.mjs`，只调用既有共享runner的FullText配置；若真实证据揭示兼容缺口，先回报后冻结同一代理的候选 `web/src/components/FullTextSearchWorkbench.vue` 与 `web/tests/fulltext-workbench-migration.test.mjs` 最小修复。`/root/wb28_ui`独占新增 `web/e2e/fulltext-real-permission.spec.ts`；`/root/wb28_review`独立只读复核真实Handler/既有测试/最终diff。根独占六共享文档、验证runner、集成/审计、完整restore/format、stage和commit；最多三个活动子代理，每代理25命名文件/35分钟，不自跑长验证或另派代理。
- 真实Release Server使用任务独占contentRoot/DataRoot与loopback HTTP，关闭非HTTP协议和外联；缺配置直接失败、不skip。最多三浏览器旅程，各120秒、retries=0，总runner10分钟，readiness120次/60秒、HTTP请求10秒；最多151种子文档，Top-K沿用1～100，验证实际search-preview/Find/Analyzer响应、当前结果下载与原身份历史，不把本地分页称全匹配或Server扫描/物化/字节/总堆预算。
- 重建由非超级用户的数据库Admin通过正常UI暂存、审批确认一次消费（现有/maintenance要求数据库Admin，WRITE不足），精确核对真实同步维护终态与原database/collection/index，超级管理员独立查证；实际撤权后旧审批或读取403清旧命中/文档/Token/结果/导入草稿/审批，重新授READ及同tokenSchema刷新仍锁存且不重放。显式安全恢复另片，不凭regrant解锁；API登录装入真实token不称登录UI/host readonly props验收。
- 成功运行的脱敏实际响应/下载/history证据须在已核验绝对runRoot内单独落盘（最多24份、每份1MiB、累计8MiB），不能依赖list reporter的内存attach；禁止保存token/密码。fixture、真实Server、其它宿主/OS对话框/安装/Extension Host/AOT/固定硬件/长稳/发行物分别记录。
- PowerShell7；禁止Graphify、广域工具扫描与未授权安装。所有循环/搜索/等待/重试有项目/迭代和墙钟上限并先小输入；长进程记录PID/创建/完整命令/父链、finally仅回收已核验自有树；临时绝对路径先核验，两处策略保留Temp不删除/重试/绕过。最终树完整restore、原级别format和staged diff退出0才本地提交，保留origin/parity-results，不push/发布/部署/外部沟通。三宿主仍未闭环，heartbeat保持ACTIVE，本轮不启动WB-29。
- 最终真实 `fulltext-real-final` 3/3、退出0、无retry/skip；Node15/15、全Web300/300、Chrome fixture8/8、TypeScript/Vite与Server Release0警告/错误通过。151种子→20/100精确命中与导出，数据库Admin一次重建sync_touch/planned=false/151且history权威151，撤权403后重授READ/Schema200刷新仍锁存。首run1通过/1失败/1未运行为spec误授WRITE导致维护403，只修授权/说明，不改生产。成功三JSON+manifest大小/哈希核验一致，两独占数据根清理；根证据 `artifacts/wb28-validation-20261006`，独立复核及八文件完整restore/format/staged检查为提交前置，实际退出/提交见final-gates/commit-checkpoint。显式恢复、typed全文分页、Server预算与三宿主继续另验。

### WB-27 合同与文件冻结（2026-10-06；本机 Relation Web 旅程已验证）

- 起点 `main / b3b2d3cd`，WB-25/WB-26提交已核验，原三代理均结束；三博客文件及HANDOFF末尾博客发布hunk属于其它会话，保留且不暂存。本轮只补Relation新Web UI到真实本机Kestrel的分页/当前结果导出、审批批次完整终态及真实撤权清载荷，依赖WB-17、WB-16与WB-26隔离runner，不重复页面迁移。
- `/root/wb27_runner`独占新增 `web/e2e/run-workbench-real.mjs`、`web/e2e/run-relation-real.mjs` 及既有 `web/e2e/run-document-real.mjs` 的薄入口抽取；真实证据发现兼容缺口时，独占候选 `web/src/components/RelationalTableWorkbench.vue` 与 `web/tests/relational-workbench-migration.test.mjs`，先回报并冻结最小修复。`/root/wb27_ui`独占新增 `web/e2e/relational-real-permission.spec.ts`；`/root/wb27_review`独立只读复核。根独占六共享文档、验证runner/集成/审计、最终restore/format与git。最多三个活动子代理，各25命名文件/35分钟，不自行长验证或另派代理。
- 复用真实Release Server的隔离contentRoot/DataRoot与loopback HTTP，关闭非HTTP协议和外联，缺配置直接失败。runner最多10分钟、readiness120次/60秒、请求10秒，浏览器三测试各120秒、retries=0；新Relation与既有Document真实回归串行。普通用户实际API登录产生token，装入浏览器会话，不称登录UI/宿主readonly props验收；无API mock、prop harness或Server改动。
- 本任务最多251条初始关系行、两条批准插入、一条重复主键失败尝试与一条被撤权拒绝插入；有效MixedCase表/列与原名深链接保留。真实分页沿LIMIT/OFFSET50、200取得实际窗口/尾页，导出只含当前已加载结果，不称全表快照或Server扫描/物化/字节/总堆预算。批准两条插入须对应BEGIN/两语句/COMMIT四个完整终态，并由管理员独立读查和原身份历史佐证；真实执行冲突可能HTTP200加NDJSON error，须记error且清审批，不将HTTP200计为写成功，管理员查证数据未新增。
- 实际UI静态复核发现暂存立即打开modal，而返回编辑清空全部暂存，无法通过正常操作积累两项批次。追加同一实施者独占Relation组件/Node最小修复：返回编辑/Escape/遮罩仅关闭预览保留当前上下文草稿，工具栏提供显式重新预览和丢弃暂存；重新预览仍核验身份/权限，确认前一次消费，撤权/身份/卸载清除隐藏预览与草稿。共享WriteApprovalPanel不改；既有fixture由UI代理追加必要兼容断言（冻结 `web/e2e/relational-workbench-migration.spec.ts`）。不通过强制穿透modal或私有状态注入伪造两项真实旅程。
- 真实Server源合同复核确认两insert终态affected为[0,1,1,2]，COMMIT已包含实际提交2；旧Relation求和会误报4。追加同一组件/Node的最小统计修复：完整无错误批次使用末尾COMMIT权威影响数，缺失/错误COMMIT不得把事务内暂存数计作已确认持久写入；历史仍保留error/unknown及不完整性，不改变Server帧合同。真实spec与必要fixture/Node相应终态按此对齐。
- 旧审批暂存后真实REVOKE，确认获得实际403（HTTP或NDJSON权限终态按现有Handler核验），旧表行/结果/草稿/审批与DDL隐藏，管理员查证未写入；重新授READ及同身份Schema刷新不能解除本地锁存、不能重放写。Relation未提供显式安全恢复，本片不新增恢复入口或把重授/刷新计为解锁证据。unknown/fixture回归仍独立记录。
- PowerShell7；禁止Graphify、广域工具扫描与未授权安装。所有循环/搜索/等待/重试有限次数/项目及墙钟，先小输入；长进程记录PID/创建时间/完整命令/父链，finally只回收已核验自有树。临时路径绝对核验，两处策略保留Temp不删除、不重试或绕过。保留origin/parity-results，不push/发布/部署/外部沟通；三宿主、安装/Extension Host/AOT/固定硬件/长稳/发行物分别待验，heartbeat保持ACTIVE。
- 三代理已冻结停止写入，独立复核PASS。最终Relation真实 `relation-real-final2` 3/3、Document共享runner真实3/3、Node20/20、全Web300/300、Chrome fixture12/12、TypeScript/Vite与Server Release0警告/错误通过。真实两insert四end [0,1,1,2]，history权威2；重复PK在COMMIT为HTTP200+table_unique_violation、error/partial/0，管理员253行未变；撤权403后regrant READ/schema刷新仍锁存。首两run断言分别误要求合法名双引号、泛sql_error，未启动项与失败trace保留，最终只按真实合同修spec。证据 `artifacts/wb27-validation-20261006`，门禁前134身份0存活、4隔离数据根和Chrome profiles清理；完整restore/原级别format/staged检查为13任务文件本地提交前置，实际退出/提交见final-gates/commit-checkpoint。

### WB-26 合同与文件冻结（2026-10-06；本机 Document Web 旅程已验证）

- 干净起点 `8a4c64c8b96536f67f0b124471790e05ef441bdb`；WB-25三代理已结束且提交已核验，不重复Object。仅补Document新Web UI连接真实本机Kestrel的权限/显式恢复与高级读取输出预算旅程，依赖WB-12/WB-18和既有Server控制面/Document API；组件只补真实Distinct兼容，不改生产Server、路由或其它宿主。
- `/root/wb20_vector_impl`独占新增 `web/e2e/run-document-real.mjs`，真实合同复核后追加 `web/src/components/DocumentCollectionWorkbench.vue` 与 `web/tests/document-workbench-migration.test.mjs` 的Distinct满窗完整性修复；`/root/wb20_vector_ui`独占新增 `web/e2e/document-real-permission.spec.ts` 与既有 `web/e2e/document-recovery-budget.spec.ts` 的必要请求上限断言；`/root/wb19_fulltext_impl`独立只读复核。根独占六共享文档、Server build、验证/runner/审计、集成、完整restore/format、stage/commit。每代理最多25命名文件、35分钟；必要修复另开15文件/15分钟有界turn，不自行长验证/git/另派代理。
- Runner仅启动本任务隔离contentRoot/DataRoot的真实Release Server，loopback HTTP、关闭MQTT/CoAP/UDP/Modbus/语义外联，复用现有Vite/Playwright入口与真实代理。必需配置缺失直接失败，不skip；最多三浏览器测试、各120秒、总10分钟，readiness最多120次/60秒、HTTP请求10秒，不安装工具。API响应不得mock，不能用prop harness替代真实路由。
- 动态普通用户先获写权限：浏览/暂存插入审批，真实撤权后确认获得403并清旧载荷/草稿/审批；恢复尝试仍403保持锁存；重新授READ后普通Refresh不解锁，显式恢复仅一次空条件Find100/skip0/无旧cursor，同token/原名目标并校验实际返回，恢复不重放旧写。管理员独立查证被拒绝文档未落库。真实只读Server拒写与routed host的readonly props证据分开。
- Aggregate保留用户pipeline并追加limit1001，真实返回哨兵后预览/导出最多1000；Distinct真实Server把limit封顶1000，故请求min(cap+1,1000)，仍保留用户1～1000预览。cap<1000的哨兵证明truncated；cap=1000且实际返回恰1000时，页面/历史明确完整性unknown，不写complete或虚构下一页；返回不足cap为complete，意外超返仍先截断。真实Distinct场景覆盖500/501哨兵及1000满窗，最多1001个本任务种子文档。不称扫描/中间物化/字节/总堆预算、进程重启恢复或全部九模型验收；登录API产生真实token并装入浏览器会话，不称登录UI验收。
- PowerShell7；禁止Graphify、广域工具扫描与未授权安装；循环/搜索/等待/重试有次数/项目数及墙钟，先小输入；长进程记录PID/创建/完整命令/父链，finally仅回收已核验自有树。临时路径绝对核验，隔离数据结束后只清本任务目录；两处策略保留Temp不删除不绕过。fixture、真实新UI、三宿主/安装/Extension Host/AOT/固定硬件/长稳/发行物分别记录。保留origin/parity-results，不push/发布/部署/外部沟通；三宿主仍未闭环，heartbeat保持ACTIVE。
- 真实证据发现空IDs发送[]会选择空目标集，追加同一组件/Node最小修复：空输入省略ids，显式IDs仍保留原值；路径$.site及501/1000断言不变。三代理已冻结停止写入，独立复核PASS。最终专属Node21/21、全Web295/295、TypeScript/Vite、Chrome fixture11/11、真实Web→本机Kestrel `document-real-final4` 3/3（无skip/retry）通过；Server Release0警告/错误。
- 首四run依次为非法集合名setup400、编辑区定位、2/3真实空IDs兼容缺口、并行验证首次Find连接中断；日志/trace保留，不计整体PASS。相同冻结源码单独第五run3/3；第四runVite中断根因未确认，后续同仓库Vite/Playwright验证串行。门禁前合并162条进程记录0存活，五个隔离contentRoot均清理；证据 `artifacts/wb26-validation-20261006`。完整restore/原级别format/staged diff为本地提交前置，实际门禁与11任务文件提交绑定见final-gates/commit-checkpoint；三宿主与服务端资源预算继续另验。

### WB-25 合同与文件冻结（2026-10-06；本地切片已验证）

- 干净起点 `a2bc3f1a6af69df5e4716f0f41c80013ca7c64e3`；WB-24 三代理已结束，不重复派单。本轮仅 Object 现有写审批执行器终态兼容切片，依赖 WB-24 的身份/权限门禁，复用 22 个暂存入口，不改 Server、路由、其它模型或宿主代码。
- `/root/wb20_vector_impl` 独占 `web/src/components/ObjectBucketWorkbench.vue`、`web/tests/object-workbench-migration.test.mjs`，复核后追加 `web/src/api/objectStorage.ts` 的必要 DELETE HTTP终态验证；`/root/wb20_vector_ui` 独占新增 `web/e2e/object-write-terminal.spec.ts`；`/root/wb19_fulltext_impl` 独立只读复核真实 Handler/DTO 与最终 diff。根独占六共享文档、runner、验证、集成、完整 restore/format、stage/commit。最多三个活动子代理；初轮25命名文件/35分钟，必要修复另开15文件/15分钟有界 turn，代理不自跑长验证或 git。
- 审批在派发前一次消费；冻结原身份/API/输入，最多1000个操作，60秒客户端新操作启动窗口与现有30秒单请求超时。只有实际响应的目标、必要完成字段和批准参数相符才记 success；批删逐项匹配批准 key，缺/重复/外来终态为 unknown，完整明确拒绝为 error。已开始操作的缺失/损坏/断连/408/5xx/身份变化为 unknown，历史保留原目标、已确认影响和不完整性，不重放审批；401/403继续锁存并清载荷。started计数为客户端执行入口，不冒称实际网络发送计量。
- Void DELETE 与 Copy/Part 响应沿用真实 HTTP/DTO 能力，不能伪造 Server 未回显的目标。图片处理/backfill 的入队接受不称异步处理完成；完整 Multipart/语义/字节/Server预算、新UI真实权限与三宿主另验。验收为专属/全Web Node、TypeScript/Vite、Chrome fixture、既有Object/语义浏览器和真实Kestrel兼容分别记录，再做独立复核及最终代码门禁。
- PowerShell7；禁止Graphify、广域工具扫描与未授权安装；循环/搜索/重试同时有次数/项目数及墙钟，长进程记录PID/创建时间/完整命令/父链并finally仅清自有树，临时绝对路径先核验。两处策略保留Temp不删除、不重试或绕过；保留origin/parity-results，不push/发布/部署/外部沟通。三宿主未闭环，heartbeat继续ACTIVE。
- 最终专属Node32/32、全Web292/292、TypeScript/Vite、新Chrome5/5、既有Object/语义浏览器4/4、既有真实Kestrel Object4/4+Multipart2/2和独立复核PASS。DELETE校验204/对象marker+version+ETag，nullable setter匹配实际Server省略null合同；Multipart写后刷新拒绝不推翻已证明终态。证据 `artifacts/wb25-validation-20261006`，门禁前24条身份0存活；完整restore/原级别format/staged检查退出0才本地提交，最终门禁与实际提交绑定见final-gates/commit-checkpoint，提交说明 `feat(m47): validate Object write terminal outcomes`，仅本任务10文件。

### WB-24 合同与文件冻结（2026-10-06；本地切片已验证）

- 干净起点 `cb32050b54e5f5d3b767f09b7593460ce18152d1`，WB-23 三代理均结束；本轮只推进 Object 桶浏览/选中对象/Range 的兼容隔离切片，复用现有六页签、v2 continuation、版本/下载、native dialogs、Multipart 与图片语义。不改 Server、路由、其它模型或宿主代码。
- `/root/wb20_vector_impl` 独占 `web/src/components/ObjectBucketWorkbench.vue`、`web/src/api/objectStorage.ts` 的读取 optional signal、必要 `web/src/api/semanticSearch.ts` 读取 optional signal 和新增 `web/tests/object-workbench-migration.test.mjs`。`/root/wb20_vector_ui` 独占新增 `web/e2e/object-workbench-migration.spec.ts`；`/root/wb19_fulltext_impl` 独立只读复核。根独占六共享文档、runner、兼容证据、集成、完整 restore/format、stage/commit；最多三个活动子代理，每代理25个命名文件/35分钟，不另派代理或自行长验证。
- 保留 database/Bucket/key/version 原名与旧 `bucket:` key。同步实际 API/endpoint/Authorization/token/profile/database/Bucket 代际，列表 prefix/continuation 和选中 key/version 各自请求快照；全部自动伴随读取及其错误/finally/URL/历史隔离迟返、ABA、新读取与卸载。401/403 清载荷、派生 URL、结果、写草稿和审批并锁存；同身份刷新/空身份往返不解锁，显式恢复另验。readonly 保留浏览/读取/下载，全部写暂存/确认程序与按钮入口门禁；六态与固定脱敏错误。
- 列表每页1～1000、累计预览1000，先截断再映射；校验响应 bucket/prefix、条目目标和 token 严格推进，不复用超返导致跳项的游标。结果/历史保留实际数量与不完整性，不称全桶快照。Range start/length/end 为安全整数，长度最多4096，冻结格式模式与版本，响应 Blob 先 slice 再 arrayBuffer/格式化；明确 Range/客户端截断，不称传输、扫描或总堆预算。
- Server 兼容复核：prefix 按现有 `TrimStart('/')` 规范化，首 continuation null/空串等价，opaque token 只验证非空/变化/不重复，不要求字面 v2。`getObjectBlob` 可兼容增 optional status/contentRange，验证真实206/Content-Range与冻结版本；head.bucket/key 是客户端回填，sizeBytes 是 Range Content-Length，不用这些值虚构 Server 目标回显或全对象长度。
- Multipart/图片语义仅补读取归属和权限隔离，不声称完整分页/语义预算；写执行器终态、未知结果和批次预算另开切片，当前补审批身份失效与门禁且禁止旧上下文回写。fixture、既有真实 Kestrel 兼容、新 UI 真实权限、三宿主/安装/Extension Host/AOT/硬件/长稳/发布分别记录。
- PowerShell7；禁止 Graphify、广域工具扫描与未授权安装；循环/搜索/重试同时有项目/迭代上限及墙钟，长进程记录 PID/创建时间/完整命令/父链并 finally 仅清自有树，临时路径先核验。两处策略保留 Temp 不删除、不重试或绕过。最终树完整 restore、原级别 format 与 staged diff check 通过才本地提交；保留 origin/parity-results，不 push/发布/部署/外部沟通。
- 最终第三生产树专属Node20/20、全Web280/280、TypeScript/Vite、Chrome15/15、既有Object/语义浏览器4/4、既有真实Kestrel兼容4/4及独立复核PASS。补安全Range差值/声明长度与两Web picker ABA；URL诊断只排除已证明的MapLibre全局worker，Object图片/未知URL回收断言保持。53条进程身份0存活；证据目录 `artifacts/wb24-validation-20261006`。提交说明 `feat(m47): isolate Object reads and bounded previews`，实际哈希见git log；根最终完整门禁通过才提交。本轮不启动下一片，优先后续Object写终态/一次消费/unknown与批次预算，整体三宿主继续ACTIVE。

### WB-23 合同与文件冻结（2026-10-06；本地切片已验证）

- 干净起点 `759f36912fdb6a82edc008db6c02d181b66ebb9e`，WB-22 三代理均结束；本轮只推进 Graph 权限/有界画布兼容切片，复用原有 Canvas、Schema、元素编辑、JSON transfer 与维护审批，不重做 M40 引擎或 Object 复杂子页。Graph 保持 Beta、database/Graph 原名与旧 key/入口。
- 实施代理 `/root/wb20_vector_impl` 独占 `web/src/components/GraphWorkbench.vue`、`web/src/api/graphs.ts` optional signal、`web/tests/graph-workflow.test.mjs` 必要兼容断言及新增 `web/tests/graph-workbench-migration.test.mjs`。UI代理 `/root/wb20_vector_ui` 独占新增 `web/e2e/graph-workbench-migration.spec.ts`；`/root/wb19_fulltext_impl` 独立只读复核。根独占六共享文档、runner、兼容证据、集成、restore/format、stage/commit；最多三个活动子代理，每代理25个命名文件/35分钟，不自跑长验证、不另派子代理。
- 补 normal/empty/error/permission/readonly/longContent 六态，401/403 来自 overview/visualization/element/audit/export/write 均清旧画布/metadata/元素/导入草稿/维护载荷/审批并锁存。readonly 浏览/元素读取/导出保留，全部暂存/确认/导入文件/维护批准拒绝的按钮与程序入口禁写；同身份刷新和空身份往返不解锁，显式读取恢复另验。错误正文固定脱敏，不入历史。
- 固定实际 API/endpoint/Authorization/token/profile/database/Graph 与同步 epoch，隔离迟返、跨库同名、ABA、新读和卸载；API optional signal 支持分派前取消，已派写取消不冒称 Server 未执行。消费 overview 的 boundedVisualization 能力，缺失或 false 不请求/不显示画布。画布按所选 10～1000 总元素上限先截断再映射/渲染，边必须指向保留顶点；保留 Server truncated 且客户端超限明确不完整，不虚构分页/全图。属性检查器预览有界，不改完整已加载编辑/JSON round-trip 合同，不称传输/扫描/字节/总堆预算。
- 审批 dispatch 前一次消费并绑定发起身份/冻结输入；元素写需完整 mutation 终态，维护需匹配 database/Graph/审批身份和实际 state，不把 staged/paused/applying 称执行完成。缺失/错目标/传输异常为 unknown 且不重放，历史保留原目标。完整长期维护、服务端预算、新UI真实权限与三宿主另验。
- 复核追加最小安全门禁：现有 JSON number 的 unsafe ID/elementVersion/edge endpoint 不可用于元素读取/编辑/写审批，阻止四舍五入后的错误目标；不实施全 Graph Int64 字符串合同。画布容器重建时重建 ECharts 并更新 ResizeObserver 归属，真实 DOM 实例须由浏览器证据验证。
- 验收为专属及全 Web Node、TypeScript/Vite、真实 Chrome fixture、既有 Graph 浏览器和既有真实 Kestrel Graph 合同分开记录，另由只读代理复核。Server/路由/其它模型/宿主代码不改；旧策略保留 Temp 不删除、不重试或绕过。所有循环/搜索/重试同时限制项目数与墙钟，长进程记录身份/父链并 finally 仅清自有树，临时路径先解析核验。最终树完整 restore 与原级别 format、staged diff check 通过才本地提交，不 push/发布/部署/外部沟通。
- 最终本地验证：专属 Node `27/27`、全 Web `260/260`、Graph Chrome `16/16`、既有 Graph 浏览器 `3/3`、TypeScript/Vite、真实 Kestrel Graph 兼容 `4/4`，独立只读复核 PASS；证据目录 `artifacts/wb23-validation-20261006`，最后进程审计 `43` 条且 `liveOwned=[]`。完整 Graph Int64 字符串、真实新 UI 权限/恢复、三宿主与发行证据仍分别待验；提交说明 `feat(m47): isolate Graph canvas and approval outcomes`，实际哈希以git log为准；最终树完整restore/原级别format与staged diff check均退出0才提交。

## 每个任务的闭环

1. 读取 HANDOFF、AGENTS、当前 git 状态与本队列，选择有证据的最小切片；记录任务 ID、负责人、文件归属、依赖和验收条件。
2. 为独立任务分配子智能体。主智能体最多并行三个子任务；只在文件范围和依赖不重叠时并行。共享队列、ROADMAP、CHANGELOG、HANDOFF 与 git 操作由主智能体串行维护，子智能体不得自行提交。
3. 设计切片先补可评审原型与状态规范；已有行为的兼容修复和合同可自主实施。最终视觉基线仍待确认的页面继续做设计，不把整个初稿当作用户已验收。
4. 实施后用适合变更的窄测试、必要构建和 UI 验证证明验收条件；由另一智能体独立复核关键合同及 diff。静态/mock、真实服务、三宿主和发布证据分开。
5. 完成最终待提交树的 `dotnet restore SonnetDB.slnx` 和 `dotnet format SonnetDB.slnx --verify-no-changes --no-restore --severity warn --exclude extensions/`。失败先修正，再重新验证；通过后再改代码须重跑。未通过不得 commit。
6. 主智能体仅 stage 本任务改动，更新 CHANGELOG/HANDOFF/任务验收，执行 Conventional Commit，记录实际哈希并复核提交内容。不得 stage 其它会话文件、构建产物或凭据。
7. 有依赖的下一项在前项闭环后启动；独立项可已在进行。失败保留证据并派修复，不重复创建同一任务。没有可执行事项时等待定时唤醒，不能写成已全部完成。

## 初始任务

| ID | 任务与交付 | 文件归属与依赖 | 验收 / 状态 |
|---|---|---|---|
| WB-00 | 接收设计包、核对现状与提交设计交付基线 | 主智能体串行维护根文档与 design 包；不动生产代码 | 现有 7/30/9/166/60 目录、MQ 逻辑/物理范围、链接、证据可追溯；restore、Format Check、脚本语法与差异检查通过；已提交 `3484aafd` |
| WB-01 | 原型工作区页签、草稿与键盘交互闭环 | prototype/app.js、styles.css；先完成 WB-00。实施者独占这两文件 | 已完成并提交 `88fc7914`：active 页签滚入可见、内存草稿标记/关闭后保留、只读/规划/错误动作门禁、对话框焦点恢复、箭头与 Home/End、桌面/390 Playwright 回归；未接真实服务。 |
| WB-02 | 页面能力、字段与状态逐页定稿 | catalog.js、task-details.js、screen-specs.md；可与 WB-01 并行，各自不改对方文件 | 首批五页已提交 `11de7131`（SQL、KV、MQ、对象 Bucket、Graph Beta）；WB-02B 再完成五个模型页，详见下一行。其余页面和真实宿主接线留待后续批次。 |
| WB-02B | 下一批五模型状态合同 | `prototype/catalog.js`、`task-details.js`、`screen-specs.md`；依赖 WB-02，主会话串行维护测试与共享文档 | 已提交 `c3bfc2f1`：measurement、table、document、vector、fulltext 各补 capabilities 与 normal/empty/error/permission/readonly/longContent 六态，并注入对应任务页；Node 合同测试 2/2、VM 5 页×6 状态、动作/边界 30/30、语法与 diff 检查通过。仅为 REVIEW_DRAFT 原型设计，不代表真实 Server、权限、质量、三宿主或生产迁移。 |
| WB-02C | 五个全局页面状态合同 | `prototype/catalog.js`、`task-details.js`、`screen-specs.md`；依赖 WB-02B，主会话串行维护测试与共享文档 | 已提交 `2ebc6978`：`database-catalog`、`connections`、`notebook`、`history`、`metrics` 各补 capabilities、六态和规范字段并注入任务页；Node 合同测试 3/3、与 WB-02B 联合 5/5、VM 7/9/30/39 guard、语法与 diff 检查通过。仅为 REVIEW_DRAFT 原型设计，不代表真实 Server、权限、三宿主或生产迁移。 |
| WB-02D | 观测与数据流状态合同 | `prototype/catalog.js`、`task-details.js`、`screen-specs.md`、`web/tests/m47-observe-flow-state-contract.test.mjs`；依赖 WB-02C，主会话串行维护共享文档与提交 | 已提交 `07506a99`：`events`、`slow-queries`、`alerts`、`runtime`、`modbus` 各补 capabilities、六态和专用字段，并注入全部对应任务页；Node 合同测试 3/3、两个 JS `node --check`、独立复核 PASS、`git diff --check`，提交前 `dotnet restore` 与 `dotnet format` 通过。保持视图暂停不暂停服务器、慢查询恢复不重跑、告警/诊断不发送或伪造健康、Modbus Runtime/Pending/Audit 与审批边界。仅为 REVIEW_DRAFT 原型设计，不代表真实 Server、权限、现场写入、三宿主或生产迁移。 |
| WB-02E | 五个工作台/数据流页面状态合同 | `prototype/catalog.js`、`task-details.js`、`screen-specs.md` 由子智能体 `/root/wb02e_global_flow_contracts` 独占；主会话串行维护测试与共享文档；依赖 WB-02D | 已提交 `5cbd759a`：固定页面 `summary`、`recent`、`imports`、`transfers`、`jobs`，补 capabilities、六态、专用字段和任务页注入；新增合同测试 3/3，与 WB-02B/C/D 联合 11/11，两个 JS 语法检查与 `git diff --check` 通过，提交前 `dotnet restore` 与 `dotnet format` 通过；复核确认 MQ、恢复、导入、传输及异构位点边界。静态原型仍保持 REVIEW_DRAFT 与真实服务边界。 |
| WB-02F | AI 与 MCP 页面状态合同 | `prototype/catalog.js`、`task-details.js`、`screen-specs.md` 由子智能体 `/root/wb02f_ai_mcp_contracts` 独占；主会话串行维护测试与共享文档；依赖 WB-02E | 已提交 `4f9720e6`：固定页面 `ai-connect`、`copilot-settings`、`rag`、`tool-permissions`，补 capabilities、六态、专用字段和任务页注入；新增合同测试 3/3，与 WB-02B/C/D/E 联合 14/14，两个 JS 语法检查与 `git diff --check` 通过，提交前 `dotnet restore` 与 `dotnet format` 通过；复核确认 typed HTTP/stdio bridge、Provider 质量/成本、RAG profile/generation/revision、权限交集、默认只读和数据外发边界。生产迁移继续冻结。 |
| WB-02G | 治理页面状态合同 | `prototype/catalog.js`、`task-details.js`、`screen-specs.md` 由子智能体 `/root/wb02g_governance_contracts` 独占；主会话串行维护测试与共享文档；依赖 WB-02F | 已提交 `c6781b4d`：固定页面 `users`、`grants`、`tokens`、`approvals`、`backup`，补 capabilities、六态、专用字段和任务页注入；新增合同测试 3/3，与 WB-02B/C/D/E/F 联合 17/17，两个 JS 语法检查与 `git diff --check` 通过，提交前 `dotnet restore` 与 `dotnet format` 通过；复核确认控制平面权限、MQ database grant/实例 Store、一次性 Token、审批终态与错误不重试、单库备份不覆盖共享 `.system/mq` 的边界。生产迁移继续冻结。 |
| WB-02H | 设置与发布页面状态合同 | `prototype/catalog.js`、`task-details.js`、`screen-specs.md` 由子智能体独占；主会话串行维护测试与共享文档；依赖 WB-02G | 已完成并提交 `175cf674`：固定页面 `preferences`、`server-settings`、`studio-host`、`capability-matrix`、`about`，补 capabilities、六态、专用字段和任务页注入；合同测试 3/3，WB-02B–H 联合回归 20/20，两个 JS `node --check`、`git diff --check` 和独立只读复核 PASS。保持宿主范围/服务器配置只读边界、Studio 安装证据独立、版本 manifest/签名未就绪不显示 PASS、真实发行版本与敏感诊断边界；仍为 REVIEW_DRAFT 原型设计，生产迁移继续冻结。 |
| WB-03 | 统一资源身份与能力合同的兼容切片 | `web/src/management-core/resourceDescriptor.ts`、`capabilityRegistry.ts`、`index.ts` 与 `web/tests/management-core-contract.test.mjs` | 已完成并纳入本地提交：MQ identity 含 database + topic，通用工厂强制显式 topic 且 `name === topic`，逻辑 `scope` 与 `persistenceScope` 分开，保留原始拼写和现有 key/路由；实例 MQ 明确 `.system/mq`、共享边界与单库备份排除；Graph 始终 Beta；未知能力安全返回 `unavailable`；Node 6/6、TypeScript、`git diff --check` 通过。仅为静态/内存合同，不代表真实权限、Server、存储迁移、AOT 或三宿主证据。 |
| WB-03C | Explorer 兼容基线证据 | web/tests 下专属 contract 测试与自有 fixtures；只读消费当前 managementExplorer.ts，可与 WB-01/WB-03 并行 | 已完成并提交 `828b7638`：`web/tests/management-explorer-compat.test.mjs`，`node --experimental-vm-modules --test ...` 5/5；覆盖 keys、大小写/冒号、index/backup、MQ 旧 key 与外层 database 选择上下文。发现并记录 index-only fallback 与跨库 MQ key 兼容边界，未改源码。 |
| WB-04 | 按已确认基线迁移全局壳和一级/二级导航 | 预检仅新增 `web/tests/navigation-compat.test.mjs`；生产壳/路由文件仍冻结 | 迁移前兼容预检已完成并通过本地提交门禁：5/5 静态断言覆盖 `/admin`、`/admin/app`、Studio/databases/trajectory-map legacy redirects、现有路由与管理员 meta、7 项 baseNavigation、5 项 adminNavigation、secondaryNavigation 管理员条件、设置/关于入口、setup/auth/admin guards 及 trajectory query→SQL。用户已确认设计基线；本项生产壳迁移仍待独立切片，不把预检写成运行时或三宿主 PASS。 |
| WB-04B | Explorer → SQL 深链接兼容预检 | 仅新增 `web/tests/explorer-routing-compat.test.mjs`；只读消费 `useSqlExplorerRouting.ts`，不改生产源码 | 已完成并通过本地提交门禁：5/5 静态断言覆盖九模型 `tool/model/node` 深链接、database selection、index/backup `{model,node}` fallback、Open-in-SQL allowlist 与 KV/MQ/Bucket 排除、route-only 不自动执行。仅证明源码兼容基线，不代表 Vue 运行时、真实路由、Server 或迁移完成。 |
| WB-05 | 结果、草稿、历史和审批工作流迁移 | 主会话冻结共享文档与集成；WB-05A 独占 `sqlConsole.ts`、`workbenchHistory.ts`、结果/历史组件和自有测试；WB-05B 独占 `WriteApprovalPanel.vue`、`writeApproval.ts` 和自有测试；根会话接线 `useSqlExecution.ts`、`SqlQueryWorkspace.vue`、`SqlConsoleView.vue` 与自有集成测试；依赖 WB-04 兼容预检 | 已完成并提交 `e5fc4668`：结果最多 10,000 行且保留截断标记；关闭草稿可恢复/丢弃且不自动执行；历史保留 unknown/completeness 并对文本、JSON/JSONL 敏感值脱敏；SQL 取消、传输中断、缺少终态写入 unknown；审批绑定连接/端点/数据库/草稿指纹并在失效时拒绝确认；共享 SQL 工作区已接恢复入口。WB-05 窄测 9/9、全 Web Node 回归 94/94、SQL 回归 11/11、TypeScript、Playwright 3/3 通过；仍不代表真实 Server、三宿主、AOT、安装、发布或全量页面迁移验收。 |
| WB-06 | 按确认基线迁移生产外壳与一级/二级导航 | `/web/src/views/AppShell.vue`、`web/src/router/index.ts`、`web/tests/workbench-shell-migration.test.mjs` 与更新后的 `web/tests/navigation-compat.test.mjs`；依赖 WB-03 资源合同、WB-04/WB-04B 兼容预检；子智能体独占生产壳/路由/迁移测试，根会话串行维护共享记录与提交 | 已提交代码 `f5c5cf53`，交接记录 `503cb6fe`：一级 rail 固定七模块，设置置于 footer；旧查询/数据/Studio 同义按钮不再重复；新增模块 aliases 并保留 legacy redirects、trajectory query、setup/auth/admin guards；active 映射覆盖现有二级/管理员路由；flows/govern 按现有 admin 边界隐藏，flows→Modbus、settings→About、ai→RAG 仅兼容落点；Explorer 九模型与 MQ database+Topic / instance `.system/mq` 语义未改。迁移/兼容/Explorer 路由 13/13、管理 Explorer 5/5、TypeScript、`git diff --check` 与独立复核通过；不代表真实 Vue runtime、Server、三宿主、AOT、安装、发布或全量页面迁移。 |

后续模型与三宿主切片从 M47-U01~U09 的实际差距继续选取，复用现有交付，不重做 M29/M32/M34，也不自动改 MQ 存储为 KV/关系表。

## 有界执行与定时检查

定时检查建议每 30 分钟唤醒同一新会话。持续执行不等于单次运行无限长：每次选择一个有界集成切片；子任务一般 20–45 分钟并写清最大项目数、重试次数及墙钟超时。未完成时保存检查点，下一次唤醒接续。任务运行期间不得再次派同一文件；主智能体保持有意义的进度更新。

每次委派必须重复：PowerShell 7；禁止 Graphify、广域编译器扫描和未经授权安装；循环/搜索/重试同时有迭代或项目上限与墙钟超时，先小输入试运行；长进程记录 PID/创建时间/完整命令/父链，成功/失败/取消/超时后仅清理任务自有树；临时文件/下载/日志/目录有归属并在核验绝对路径后回收，保留交付物。禁止按进程名批量终止。

提交、分支切换与统一文档串行。保留 origin/parity-results，禁止合并；不可 reset、清除或覆盖不明来源的改动。当前用户未授权自动推送、发布、部署或外部沟通。机器资源异常时停止新增并行工作，清理归属明确的失控进程，报告检查点。

定时检查在未变化时保持安静；仅有实质完成（含提交哈希）、失败、阻断或用户需要处理的事项才通知。用户2026-10-07追加：之前任务继续，三宿主确实闭环后保存交接并创建SonnetDB本地新会话，将唯一workbench heartbeat迁移过去保持ACTIVE、每30分钟检查各类后续任务。没有在执行的任务时从仓库待办/已确认缺口主动选一项有界任务，冻结归属/依赖/验收后交给专属子智能体；没有现成待办时先有界盘点，主智能体负责监督/验收/共享文档/最终验证和本地提交。不限任务类型，不重复包装完成项，不因一时无待办或单片完成暂停。既有授权边界仍有效，不自动push/发布/部署/外部沟通，不删除成果或自动归档会话；当前三宿主未完成，仍在原会话接续。

## WB-07 当前切片（2026-10-05）

- 状态：已完成并提交 `3ad20c6a feat(m47): project canonical resource identity into workspace tabs`；负责人：根会话集成，子智能体 `/root/wb07_resource_identity` 独占生产 Explorer/页签文件与自有测试。
- 范围：Explorer item 投影统一 `ResourceDescriptor`，页签携带 database/resource/legacy key；保留旧 `tool/model/node` 深链接和 route-only 语义。
- 边界：MQ 仍为 database + Topic，`scope=database`、`persistenceScope=instance`、`.system/mq`、单库备份不覆盖；Graph 保持 Beta；不接入 CapabilityRegistry 权限判定，不改博客改动或共享文档。
- 验收：WB-07 自有测试 5/5、Explorer/路由/壳联合回归 18/18，独立复核全 Web Node 回归 102/102，TypeScript/Vite build、`git diff --check`、最终树 `dotnet restore SonnetDB.slnx` 与 `dotnet format SonnetDB.slnx --verify-no-changes --no-restore --severity warn --exclude extensions/` 均通过。直接浏览器旧 `tool/model/node` URL 的 `node` 选择仍是后续切片边界。

## WB-08 当前切片（2026-10-05）

- 状态：已完成并提交 `418c21d3 fix(m47): restore legacy explorer route selection`；负责人：根会话集成，子智能体 `/root/wb08_route_selection` 独占实现，`/root/wb06_shell_reviewer` 独立复核。依赖 WB-07 `3ad20c6a`。
- 范围：修复旧 `tool/model/node` 直达 URL 的 `node→activeExplorerKey` 回选；保持旧 query 形状、当前 active/default database、Explorer 点击路径和 route-only 不自动执行。
- 独占候选文件：`web/src/utils/managementExplorer.ts`（route node→legacy key helper）、`web/src/views/SqlConsoleView.vue`（route/db/schema/management 就绪后一次性选择 watcher）、`web/tests/explorer-route-selection.test.mjs` 与必要兼容测试。不得接入 CapabilityRegistry 或改变 MQ/Graph 资源合同。
- 验收：覆盖 measurement/table/document/kv/mq/vector/fulltext/bucket/graph/index/backup、大小写/冒号、缺失/未知 node 回退、tool-only、MQ database+Topic、Graph Beta、metadata 等待、watcher 顺序和 route-only；自有 Node 测试 5/5，Explorer/路由/壳/导航/页签定向回归 20/20，独立复核全 Web Node 107/107，TypeScript/Vite、`git diff --check`、最终树 `dotnet restore SonnetDB.slnx` 与 `dotnet format SonnetDB.slnx --verify-no-changes --no-restore --severity warn --exclude extensions/` 均通过。URL 无 database 参数的 active/default DB 边界单独记录。

## WB-09 当前切片（2026-10-05）

- 状态：已完成并提交 `09f5711b feat(m47): add database context to explorer deep links`；负责人：根会话集成，子智能体 `/root/wb09_database_route` 独占实现，`/root/wb06_shell_reviewer` 独立复核。依赖 WB-08 `418c21d3` / `9c3ace1f`。
- 范围：为新的 Explorer 深链接补可选 `database` 上下文，同时保留旧无 database 的 `tool/model/node` query、旧 redirects、当前 active/default DB 回退和 route-only 不自动执行。
- 独占候选文件：`web/src/composables/useSqlExplorerRouting.ts`（生成链接携 database，保留旧字段）、`web/src/views/SqlConsoleView.vue`（校验 route.query.database，数据库列表加载后选择指定库，再按现有 helper 回选）、必要时 `web/src/composables/useSqlExplorer.ts`、新增 `web/tests/explorer-database-route-selection.test.mjs` 与兼容测试。不得接入 CapabilityRegistry 或改变 MQ/Graph 资源合同。
- 验收：九模型、index、backup、大小写/冒号、MQ database+Topic、Graph Beta；有效 database 等待列表后选择，缺失/未知 database 安全回退 active/default，database-only 与 A→未知→恢复路径有 token 保护，用户手动切库不被同一旧 URL 强制切回；旧 query 与 route-only 全回归。自有 Node 4/4，Explorer/路由兼容定向 10/10，独立复核全 Web Node 111/111，TypeScript/Vite、`git diff --check` 和最终 restore/format 通过。手动切库后只改变同 database URL 的 model/node query 由 WB-10 补齐；真实 Server、权限、三宿主和发布证据分开。

## WB-10 当前切片（2026-10-05）

- 状态：已完成并提交 `ee44d7bb34b0b9a95ae3e7914b82a65021bf97f3`；负责人：根会话集成，独占实现与独立复核已完成。依赖 WB-09 `09f5711b` / `674b675a`。
- 范围：补齐用户手动切换数据库后，URL 保留旧 `database` 但只改变 `model/node` 时的投影策略；同一旧 URL 不强制切回，新的 model/node 在当前手动数据库解析或安全回退，不误选其它数据库。新 database query 仍优先切库。
- 独占候选文件：`web/src/views/SqlConsoleView.vue`、`web/tests/explorer-database-route-selection.test.mjs`（必要时新增小型手动切库回归）；不改资源 descriptor、CapabilityRegistry 或 MQ 存储。
- 验收：旧/新 tool/model/node/database query、metadata/token 等待、A→未知→恢复、MQ database+Topic、Graph Beta、route-only 全回归；定向 Node 5/5、Explorer/路由/管理兼容 15/15、全 Web Node 112/112，TypeScript、Vite build、`git diff --check`、最终 `dotnet restore SonnetDB.slnx` 与 `dotnet format SonnetDB.slnx --verify-no-changes --no-restore --severity warn --exclude extensions/` 均通过。策略和未覆盖的真实 Server/权限/三宿主边界写入记录。

## WB-11 当前切片（2026-10-05）

- 状态：已完成并提交 `390ff526`；用户已确认 Measurement Workbench 页面基线，根会话负责共享文档、集成、门禁与提交，子智能体 `/root/wb10_manual_switch` 独占页面组件与专属迁移测试。依赖 WB-10 `ee44d7bb` / `d6aa81d8`。
- 范围：沿用已确认五区外壳与现有 measurement 路由/旧深链接；中心区域呈现数据点、导入、监控、Schema，右侧 Inspector 继续由工作台外壳承载，底部保留结果/状态；查询、刷新、导出可用，写入/删除继续进入 WriteApprovalPanel。
- 身份与边界：保留 database、measurement 原名/大小写和旧 key；不改 MQ 存储、Graph 语义、Server API 或三宿主发布，不把本地 UI/定向测试写成真实服务验收。
- 验收：normal、empty、error、permission、readonly、longContent 六态有可验证生产组件合同；旧路由/深链接、查询/刷新/导出、写审批、跨库同名资源和权限错误载荷清理已覆盖。专属 Node 5/5、全 Web Node 117/117、TypeScript、Vite build、Measurement Playwright 8/8、`git diff --check`、最终 `dotnet restore SonnetDB.slnx` 与 `dotnet format SonnetDB.slnx --verify-no-changes --no-restore --severity warn --exclude extensions/` 均通过。代码提交为 `390ff526 feat(m47): migrate measurement workbench`。真实 Server、三宿主、安装、发布和全量九模型验收仍待补。

## 新三宿主阶段（2026-10-06）

用户已授权继续实现独立 SonnetDB Studio、VS Code Workbench 与 Web Admin。三项并行任务先按确认基线做有界合同/页面切片；共享文档、集成、stage、restore/format 和 commit 由根会话串行维护。

| ID | 任务与文件归属 | 依赖与验收 / 状态 |
|---|---|---|
| WB-12 | Web Admin Document Workbench；`web/src/components/DocumentCollectionWorkbench.vue`、`web/tests/document-workbench-migration.test.mjs` | 依赖 WB-11。补齐 database + collection 原名身份、六态、旧路由/深链接与 WriteApprovalPanel 边界；专属Node10/10、全Web127/127、TypeScript/Vite、Document浏览器7/7与独立复核通过；已提交 `54c787551186f74036a1845e25a937d1a268ddc1`，最终restore/format通过。Find预览1000；403显式恢复与Aggregate/Distinct预算另验。 |
| WB-13 | SonnetDB Studio 宿主合同；三个 Studio 源文件、`StudioConnectionLibraryTests.cs`、`StudioHostContractTests.cs` 与现有 `StudioManagedServerHostTests.cs` 的生命周期断言 | 已完成实现并冻结；Release定向34/34（真实bridge与ManagedLocal/external）、独立复核与diff check通过。宿主/端点/profile/数据库原名身份、URL校验、source-generated JSON与canStop已验证；已提交 `7483584771ba7164b30047eff59f2c6f2e97e96f`，最终restore/format通过；客户端展示、安装和发布另验。 |
| WB-14 | VS Code Workbench 资源/深链接合同；扩展 `src/core/types.ts`、`workbenchResource.ts`、`src/extension.ts`、`package.json`、`src/test/host/index.ts` 与专属测试 | 依赖现有 Remote-first 扩展；补齐 database/resource 原名、旧 key 兼容与九模型 Workbench 入口，不扩大治理权限；TypeScript/Node20/20、本机Code.exe真实Extension Host注册smoke与独立复核通过；已提交 `e7cf2fe512974a1cd9bdbac3f767529f2b065e1a`，最终restore/format通过；浏览器最终回选/认证和VSIX另验。 |

## 三宿主后续队列（本地切片通过不等于整体完成）

| ID | 有界下一项 | 依赖 / 验收 / 剩余边界 |
|---|---|---|
| WB-15 | Studio Web 客户端消费真实 bridge 身份与生命周期合同，复用现有 native bridge、连接库和 Managed Local UI | 已提交 `e1f93a6995e6e1e24e1badfaefb2eb918f31393a`；五客户端文件与专属 Node 测试由 `/root/wb15_client_inventory` 实施，`web/e2e/studio-host-client.spec.ts` 由专属夹具代理实施，另一代理独立只读复核PASS。依赖WB-13；原名身份、external/owned/stopped/failed/canStop与未知合同保守门禁、bootstrap/status/save迟返、同ID endpoint认证同步和目录ABA均已收口；保存串行、宿主确认且无回授循环。专属Node10/10、全Web137/137、Studio三类定向40/40、TypeScript/Vite和StudioNative浏览器夹具8/8通过；代码最终完整restore/Format Check与staged diff check退出0。真实Server、干净Windows/WebView2、安装升级卸载、未使用旧Header及完整Explorer异步组合仍独立验收。 |
| WB-16 | VS Code → Web Workbench 的最终资源回选与认证边界证据 | 已提交 `0bb628adde45ad160f05a67057f098fa982c4f85`，依赖WB-14与WB-08～WB-10。九模型/index/backup、原名/冒号、同名MQ、登录返回、显式base/SSE及index分组收口；Node9/9、全Web146/146、扩展20/20、根/代理构建、两部署浏览器各17/17、真实Host13节点命令调用、独立复核和代码最终完整门禁通过。真实Server权限、真实代理部署/远程SSE/同origin存储隔离与VSIX发布单列。 |
| WB-17 | Relation Table Workbench 的身份、六态与预览/审批 | 已提交 `f19782638789279e8099d7cbdf6f82b40cac12c0`，依赖 WB-11/WB-12/WB-16；复用分页/设计器/审批。原名、200 行预览、会话/Schema/迟返隔离、403 锁存、一次审批及 unknown 终态收口；Node15/15、全Web161/161、TypeScript/Vite、Chrome12/12、既有设计器2/2、独立复核及代码最终完整门禁通过。readonly 仅安全 DDL/浏览/结果导出；真实Server权限、物化/字节预算、三宿主和发行物另验。 |
| WB-18 | Document 权限修复后的显式安全恢复入口与剩余高级读取/预算证据 | 已完成并提交 `1d9465fb`；干净起点 `5dec4282`，依赖 WB-12。恢复与读取预算合同、专属文件归属和验收条件见下方检查点。 |
| WB-19 | FullText Workbench 上下文、权限载荷与预览预算 | 已提交 `2f9a5477`，从干净 `1d9465fb` 接续；专属Node15/15、全Web184/184、TypeScript/Vite、Chrome8/8、既有真实Kestrel兼容4/4、独立复核与最终完整门禁通过。真实权限/写终态、读取恢复与三宿主/发行物另验。 |
| WB-20 | Vector Workbench 原始向量检索上下文、权限载荷与Top-K预览 | 从干净 `2f9a5477` 接续，依赖WB-11/WB-16与既有Vector API；原名/六态、请求快照、401/403及空Schema锁存、Top-K100与子页门禁完成本地切片。专属Node17/17、全Web201/201、TypeScript/Vite、Chrome10/10、既有导入回归1/1、真实Kestrel兼容3/3与独立复核通过；本轮提交说明 `feat(m47): isolate Vector preview context and permission payloads`，实际哈希以git log为准，最终完整门禁为提交前置。索引Profile、真实权限/恢复/预算与三宿主/发行物另验。 |
| WB-21 | KV 权限锁存、六态与有界预览 | 本地切片已验证；专属Node17/17、既有KV12/12、全Web218/218、TypeScript/Vite、Chrome12/12、既有浏览器5/5、真实Kestrel兼容6/6与独立复核通过。合同/归属见下方；最终完整restore/format及staged检查为提交放行条件，实际哈希见git log。真实新客户端权限/恢复、完整atomic响应、字节/堆预算与三宿主另验。 |
| WB-22 | MQ 权限锁存、实际Topic/请求隔离与有界预览 | 本地切片已验证，合同/归属见下方；专属Node22/22、全Web240/240、TypeScript/Vite、Chrome16/16、既有MQ浏览器3/3、真实Kestrel兼容2/2及独立复核PASS。最终完整restore/format和staged检查为提交前置，实际哈希见git log。新UI真实权限/恢复、完整metadata/解码/总预算、三宿主另验。 |
| WB-23 | Graph 权限/请求隔离与有界画布 | 已提交 `cb32050b`，专属Node27/27、全Web260/260、Chrome16/16、既有浏览器3/3、真实Kestrel兼容4/4、build及独立复核通过。Graph Beta、完整Int64字符串/新UI真实权限/三宿主边界见顶部冻结。 |
| WB-24 | Object 读取隔离、权限门禁与有界列表/Range | 本地切片已验证，顶部冻结；Node20/20、全Web280/280、Chrome15/15、既有浏览器4/4、真实Kestrel兼容4/4、build与独立复核PASS。最终完整门禁为提交前置，实际哈希见git log。写终态/unknown/一次消费、完整语义/Multipart/Server预算、新UI真实权限/OS/三宿主另验。 |
| WB-25 | Object 写终态、一次审批消费与批次预算 | 本地切片已验证，顶部冻结；Node32/32、全Web292/292、Chrome5/5、既有浏览器4/4、真实Kestrel Object4/4+Multipart2/2、build与独立复核PASS。最终完整restore/format/staged检查为本地提交放行条件，实际哈希见git log；完整语义/Multipart/Server预算、新UI真实权限/OS/三宿主另验。 |
| WB-26 | Document 新Web UI真实权限、显式恢复与输出预算 | 本机Web→Kestrel 3/3、Node21/21、全Web295/295、Chrome fixture11/11、build与独立复核PASS；空IDs省略，Distinct满1000完整性unknown。最终完整门禁与实际11文件提交见证据目录；API登录、READ拒写与真实登录UI/readonly props分开，Advanced/Server资源预算及三宿主另验。 |
| WB-27 | Relation 新Web UI真实分页/导出、事务终态与撤权锁存 | 本机Relation→Kestrel3/3、共享runner Document真实3/3、Node20/20、全Web300/300、Chrome fixture12/12、build和独立复核PASS；返回编辑保留暂存并显式丢弃，COMMIT权威影响数避免重复，真实冲突error/partial/0与撤权403保持锁存。13文件最终完整门禁/实际提交见证据目录；显式恢复、Server预算/完整SQL矩阵、其它宿主及发行物另验。 |
| WB-28 | FullText 新Web UI真实Top-K/当前导出、同步重建终态与撤权锁存 | 本机Web→Kestrel3/3、Node15/15、全Web300/300、Chrome fixture8/8与build通过；非超级用户的数据库Admin一次批准sync_touch重建，REVOKE403后重授READ/刷新不解锁或重放；成功三JSON和manifest独立落盘核验。八任务文件完整门禁/实际提交见证据目录；生产组件/Server不改，显式恢复/typed全文分页/Server预算/三宿主另验。 |
| WB-29 | KV 新Web UI真实游标/Get/JSONL、条件写/交换终态子集与撤权锁存 | 本机Web→Kestrel首轮3/3、Node17/17、全Web300/300、Chrome fixture12/12和build通过；普通WRITE三审批NX成功/未应用影响0/交换，真实版本+原身份history与管理员Get对拍；REVOKE403清旧导入审批、重授READ/同token刷新仍锁存。三成功JSON+manifest独立落盘核验；八任务文件最终完整门禁/实际提交见证据目录，生产组件/Server/共享runner不改，完整atomic/恢复/Server预算/三宿主另验。 |

WB-15～WB-35已按各自本地范围验证，实际提交以git log与各证据目录为准；M47-U01～U09 仍需完整九模型适配器、三宿主真实旅程、AI/MCP 入驻和版本/安装/发布矩阵；没有发布授权时保留可复核的 NOT_READY 边界，不因本机切片 PASS 暂停整体研发。下一次接续WB-35实际检查点，优先受控复现/最小修复Graph恰满顶点预算导出候选缺口，再推进Object新UI真实旅程和Studio/VS Code剩余合同；不重复WB-25～WB-35，本轮不启动WB-36。

### WB-20 合同与文件冻结（2026-10-06；本地切片已验证）

- 实施代理独占 `web/src/components/VectorSearchWorkbench.vue` 和新增 `web/tests/vector-workbench-migration.test.mjs`；UI代理独占新增 `web/e2e/vector-workbench-migration.spec.ts`；独立复核只读。根独占六个共享文档、验证runner、完整restore/format、stage和commit；最多三个活动子代理，每代理最多25个命名文件、35分钟、无自跑长验证，根统一执行有界验证。
- 保留database、measurement/column原名和现有内部`${measurement}:${column}`选择键；外层Explorer的`vector:measurement:column` key不改。六态、只读/无权限、发起profile/实际endpoint/auth/Schema/epoch/API和参数快照隔离迟返、同名跨库、ABA、新查询和卸载；401/403清命中/metadata/生成向量/结果，保留用户raw/text/filter输入，同身份刷新不解锁，显式安全恢复另验。
- 原始向量须有限且维度匹配；Top-K沿用Server1～100，先截断再格式化/显示/导出，历史绑定发起身份及实际preview count/完整性，不新增哨兵或continuation。服务端扫描/中间物化/字节/总堆、Recall和真实模型质量独立验收。
- 既有`embed-preview`只返回vector/dimension且未给出所选索引的显式Profile绑定。本轮不伪造Profile，不用图片语义搜索status代替，不调用未验证的隐式文本embedding；文本入口明确Profile未就绪并保留raw路径，Profile合同留后续，不改变Server或Provider实现。
- 数据编辑/导入继续复用MeasurementWorkbench；按资源/身份/Schema代际key重建子页，使旧草稿/审批不能跟随新上下文；传递readonly/permission门禁，不重新迁移子页写执行器或声称其完整写终态/权限旅程已验收。Server、路由、其它模型、三宿主代码与策略保留Temp目录不改。
- 两名实际专属代理分别独占组件/Node与UI spec；UI代理在自身交付冻结后独立只读复核组件/Node，根集中取得最终运行证据。空Schema→同资源恢复不解除deny；最终Node17/17、全Web201/201、Chrome10/10、既有Vector导入1/1、build与独立复核通过。初轮UI9/10、下一轮8/10均为抽屉定位/Chart模式夹具缺口，修后真实打开Raw、断言旧载荷可见及deny后目标panel0，未降低断言。真实Kestrel兼容3/3单列，证据与门禁/清理见validation-report。

### WB-19 合同与文件冻结（2026-10-06；本地切片已验证）

- 实施代理独占 `web/src/components/FullTextSearchWorkbench.vue` 与新增 `web/tests/fulltext-workbench-migration.test.mjs`；浏览器证据代理独占新增 `web/e2e/fulltext-workbench-migration.spec.ts`；第三代理独立只读复核。根独占共享文档、验证 runner、集成、完整 restore/format、stage 与 commit，最多三个活动子代理，25 个命名文件、45 分钟、代理测试最多两次；根每条长命令另设超时。
- 保留 database、collection/index 原名、现有 `fulltext:collection:index` key 与 route-only 不执行；补六态与只读/无权限门禁，搜索、Analyzer 和 Find 按发起时 profile/endpoint/auth/epoch/API 与参数快照隔离迟返、ABA 和卸载。
- HTTP 401/403 清命中、文档、Token、结果、写草稿和审批，保留检索输入；同身份 Schema/认证刷新不得解锁。安全读取恢复入口留后续独立切片，不凭刷新或本地按钮宣称权限恢复。错误正文固定脱敏。
- 沿用既有 search-preview API，Top-K 限 1～100；返回先截断到发起 Top-K 再 Find、格式化、分页和导出。超返明确 truncated，历史写实际 preview count/完整性；本地分页只覆盖该结果，不代表所有匹配文档、不新增哨兵请求或虚构 continuation。服务端扫描/物化/字节/总堆预算仍待验收。
- 重建/导入仍复用一次审批；冻结原上下文、API、模式和项目，批次前后校验 epoch，已派请求不声称未执行，缺终态/传输断连记 unknown 且不重放旧审批。不改 DocumentAdvanced、Server API、路由和其它宿主；fixture、真实 Server 兼容、三宿主/安装/AOT/发布证据分别记录。
- 重建终态匹配真实 Handler 的 `rebuild_index/ok/success`、completedUtc、index check 与 document/原 owner/name/fulltext/sync_touch/planned=false；planned、缺失或错目标不得记 success。导入最多1000文档，文件10MiB、文本10MiB字符，批次窗口60秒并使用既有30秒请求超时。首次已派批次后ABA记原身份unknown，后续批次为0；真实Axios分派前同步切身份adapter为0属于独立本地证据。
- 最终专属Node15/15、全Web184/184、TypeScript/Vite与Chrome8/8通过；真实Kestrel既有FullText与Maintenance兼容4/4，未连接新UI执行真实权限/写旅程。根证据目录 `artifacts/wb19-validation-20261006` 保留最终规格/日志与自有进程核验；初轮14/15夹具复制问题已修正，reviewer早期无完整身份记录的阶段运行不计最终证据。提交以最终完整restore/format与diff门禁为前提，真实宿主/AOT/安装/硬件/长稳/发布继续分开。

### WB-18 合同与文件冻结（2026-10-06；已完成）

- 恢复合同：本地 401/403 锁存后仅显式“重新验证读取权限”可发起一次全新 Find，默认空过滤、100 行、无旧游标。运行期间保持 permission 且隐藏载荷；仅当前 database/原名集合/profile/endpoint/auth/epoch 的有效成功响应解除本地锁存。失败、迟返、卸载和身份 ABA 不解除；外部 permissionDenied 不能被本地动作覆盖。恢复不还原旧写草稿/审批、不自动执行写入，不恢复旧高级查询或 Change Feed。
- 高级读取合同：Aggregate 保留用户 pipeline 并在末尾追加 `$limit: 1001`；Distinct 将用户预览上限限制为 1～1000，请求多一个哨兵值。两条路径最多格式化/展示/导出 1000 项（Distinct 为所选上限），超限明确 truncated，历史记录实际预览数与不完整性。不得把末尾 limit 称为服务端扫描/中间物化/字节/总堆预算；不提供虚构分页。
- 实施代理独占 `web/src/components/DocumentCollectionWorkbench.vue`、`web/tests/document-workbench-migration.test.mjs`；UI 证据代理独占新增 `web/e2e/document-recovery-budget.spec.ts`；独立复核只读。`DocumentAdvancedWorkbench.vue` 的更新/索引/Change Feed、Server API、路由与其它宿主保持独立验收，不重复包装 WB-12。
- 根独占共享文档、兼容证据盘点、验证 runner、完整 restore/format、stage 和 commit。最多三个活动子代理；每代理最多 25 个命名文件、25 分钟、测试最多两次，根整体验证各命令另设超时。静态/fixture、真实 Server、三宿主/安装/AOT/发布证据分别记录；旧策略保留目录只读复用 runner，不触碰删除。

### WB-21 合同与文件冻结（2026-10-06；本地切片已验证）

- 干净起点 `08bd329f`，只推进 KV 权限与有界预览兼容切片，依赖既有 KV API、WB-05/WB-16 共享结果与原名导航。复用游标浏览、JSONL round-trip、NX/XX、交换/删除、精确版本字符串与一次审批，不重做整页。
- 实施代理 `/root/wb20_vector_impl` 独占 `web/src/components/KvKeyspaceWorkbench.vue`、新增 `web/tests/kv-workbench-migration.test.mjs` 和既有 `web/tests/kv-workflow.test.mjs` 的必要合同断言；必要 API 响应校验仅在先回报并追加冻结后进行。UI代理 `/root/wb20_vector_ui` 独占新增 `web/e2e/kv-workbench-migration.spec.ts`，`/root/wb19_fulltext_impl` 独立只读复核。根独占六个共享文档、runner、完整restore/format与git。最多三个活动子代理，每代理最多25个命名文件、35分钟，不自跑长验证。
- 保留 database/keyspace 原名、旧 `kv:keyspace` key、真实 prefix/cursor；补 normal/empty/error/permission/readonly/longContent 六态、外部readonly/permission门禁，身份/实际endpoint/auth/API与同步epoch隔离迟返、ABA和卸载。401/403来自Scan/Stats/Get/Write均锁存、清值/统计/游标/结果/编辑与导入草稿/审批，固定脱敏提示；同身份刷新及空资源往返不解锁，安全恢复另验。
- 扫描每页1～1000，累计与批量Get预览1000；先截断再映射/格式化/显示，达到上限停止Load more，超返不可复用会跳过未保留项的cursor。历史/结果记录实际预览数与完整性，不把加载页或客户端截断称为全keyspace、快照或服务端资源预算。
- Inspector按原始字节数提示，Text/JSON/Hex/Base64最多格式化4096字节，明确截断；完整原始值仅用于既有round-trip导出，不把截断预览自动填入写草稿。readonly浏览/Get/统计/导出保留，TTL/写入/删除/导入禁用；所有程序入口亦检查门禁。
- 审批仍一次消费并绑定发起身份；缺失/传输异常的既有unknown路径在历史保持unknown，不重放。全量atomic响应形状、导入/写字节预算、CAS及真实新客户端权限/写旅程留独立切片。Server、路由、其它模型与宿主不改。静态/fixture、真实Kestrel兼容、三宿主/安装/AOT/硬件/长稳/发布分别记录，旧策略保留Temp不删除、不重试或绕过。
- 三代理已停止写入；独立复核首轮发现空数据库占位解锁及缺真实Axios证据，修复并补测试后第二轮PASS。最终专属Node17/17、既有KV12/12、全Web218/218、TypeScript/Vite与Chrome12/12通过；既有真实Kestrel兼容6/6单列。完整Base64仍解码，不能称字节/总堆预算；raw markRaw defaults无信号的独立ABA不在证据内。根证据目录 `artifacts/wb21-validation-20261006`，本轮提交说明 `feat(m47): isolate KV permission state and bounded previews`，实际哈希以git log为准；最终完整restore/原级别format与staged diff check为提交放行条件，日志/退出值见验证记录。

## 会话与自动检查（2026-10-06已转移）

### WB-16 文件与依赖冻结（2026-10-06；已完成的本轮记录）

- 干净起点 `941550a5`，WB-15三个代理已结束。只推进WB-16，不并行启动WB-17/18。发现Web router、API默认地址与Vite固定根路径；登录守卫的已认证分支丢失redirect，需同一兼容切片验证修复。
- 实施代理独占 `web/src/router/index.ts`、`web/src/views/LoginView.vue`、`web/src/api/client.ts`、`web/src/stores/connections.ts`、`web/vite.config.ts`，新增 `web/src/utils/workbenchNavigation.ts` 和 `web/tests/workbench-navigation-auth.test.mjs`。复核发现AppShell自动SSE订阅仍请求根路径，追加冻结 `web/src/api/events.ts` 的最小部署base修复及实际构造URL证据；不扩大remote profile SSE语义。未改旧导航测试。保留默认根部署，代理子路径使用显式 `SONNETDB_WEB_BASE_PATH` 构建配置，不从不可信URL猜测部署基址。
- 浏览器/Host证据代理独占新增 `web/e2e/vscode-workbench-navigation.spec.ts` 与扩展 `extensions/sonnetdb-vscode/src/test/host/index.ts`；真实消费WB-14链接生成器，验证最终页面/资源与登录返回、同名MQ、仅导航字段及不执行SQL。Host的外部浏览器开启边界可拦截，仅作为真实命令调用合同证据。
- 浏览器首轮14/17发现索引id的 `table:` 前缀使Sidebar错误展开Tables，追加实施代理独占 `web/src/components/ManagementExplorerSidebar.vue` 的精确资源group匹配；不改原名、key、路由或选中索引语义。另两项登录失败为等价URL编码断言误差，仅修fixture完整decoded字段比较。复验和独立复核按最终树重跑。
- 独立复核代理全程只读并PASS；根独占共享文档、验证runner、集成、完整restore/format、stage与commit。最终代码提交12文件（八Web生产文件、专属Node测试、浏览器spec、Host测试与CHANGELOG），不修改Server/资源身份/存储/发布合同。初轮14/17不计完成证据；修复后根与代理浏览器各17/17，无skip。Measurement/Relation允许既有只读预览，保存草稿不执行；index/backup保留SQL工作区落点。fixture认证、SSE构造和真实Host捕获分别记录，不能计作真实Server/外部OS浏览器/VSIX验收。
- 根临时目录 `C:\Users\mysti\AppData\Local\Temp\sonnetdb-wb16-73be578e6aa94e93aee31d340ef55950` 删除被自动审查拒绝，仅返回blocked by policy；58个PID身份核验无任务存活，runner/日志和cleanup-status.txt保留，不重试删除或绕过。原交接保留目录亦不触碰；研发从WB-17继续，不因临时文件保留阻断。

当前会话：**SonnetDB Workbench 三宿主研发与验收**，ID `01a10d27-d964-7550-9b8e-066447122527`，host `local`，SonnetDB本地项目。已从`d773a62e`接管唯一写入并提交WB-15～WB-17；同一个heartbeat `workbench`（Workbench 三宿主持续研发与闭环）保持ACTIVE、每30分钟，没有创建重复自动化。旧thread `01a10862-bcd5-7d82-ab22-c916c00221a3` 在最后交接提交后停止仓库写入；下一次从WB-18接续，无需重新确认已有授权。

### WB-17 文件与依赖冻结（2026-10-06；已完成的本轮记录）

- 干净起点 `fefcc72e`，WB-16 三代理均结束；本轮只推进 Relation Table Workbench，依赖已提交 WB-11/WB-12/WB-16。
- 实施代理独占 `web/src/components/RelationalTableWorkbench.vue` 与新增 `web/tests/relational-workbench-migration.test.mjs`。复用已有 SELECT 分页、行编辑、设计器、索引、导入导出、ER/DDL 和 WriteApprovalPanel；补 database/原名身份、六态、读请求迟返隔离和写审批上下文门禁。子工作台权限合同不足时保守隐藏，不能把本页门禁计作其独立迁移。
- UI 证据代理独占新增 `web/e2e/relational-workbench-migration.spec.ts`，覆盖实际组件状态、跨库同名、权限载荷清理、分页与审批；另一个代理只读复核。根独占共享文档、验证 runner、完整门禁、stage 和 commit；不改路由、Server、MQ 或其它宿主代码。
- 自动只读 SELECT 预览仍允许；打开历史/SQL 草稿不执行。静态、fixture 与真实 Server、三宿主、安装/AOT/发布证据分开。两处已被策略保留的临时目录不触碰。
- 最小差距已盘点：旧行浏览没有上下文/卸载迟返校验，审批没有只读/权限/连接门禁，batch 结果不足也可能记 success。迁移合同为读取快照与 epoch 隔离；403 清理载荷/草稿/审批；只有请求语句数与完整终态全部匹配才记写成功，断连/不完整为 unknown 且旧审批不可再确认；历史始终保留发起时上下文。只读保留本页 SELECT 与结果导出，未具备只读合同的子工作台暂隐藏。
- 最终纯 DDL 经只读复核进入 readonly allowlist；403 仍隐藏全部子页。Node15/15（含真实 Axios/SQL API 分派 adapter）、全Web161/161、Chrome12/12和既有设计器2/2；初轮UI10/12的实际 FAB 遮挡及 selector 歧义已修复并复验。代码提交 `f1978263` 前完整 restore/Format Check/staged diff check 通过，根维护实际哈希、门禁和共享记录，不把 fixture 写成真实 Server/三宿主完成。

### WB-22 合同与文件冻结（2026-10-06；本地切片已验证）

- 干净起点 `92c73a5baa3bfd9aff7a4553eba24382e6420ba5`；只推进 MQ 权限、请求隔离与有界预览兼容切片，依赖 WB-16、既有 Browse/Publish/Ack API 与共享结果/审批。复用 Overview/Messages/Consumers/Configuration、JSONL、真实 offset 窗口和趋势，不重做页面。
- 实施代理 `/root/wb20_vector_impl` 独占 `web/src/components/SonnetMqWorkbench.vue`、`web/src/api/mq.ts` 六 helper 的 optional signal、`web/src/api/management.ts` 仅 `fetchMqTopics` 的 optional signal、新增 `web/tests/mq-workbench-migration.test.mjs`；UI代理 `/root/wb20_vector_ui` 独占新增 `web/e2e/mq-workbench-migration.spec.ts`；`/root/wb19_fulltext_impl` 独立只读复核。根独占六共享文档、runner、集成、restore/format、stage/commit；最多三个活动子代理，每代理最多25个命名文件、35分钟，不自跑长验证。
- 保留 database/Topic 原名、旧 `mq:Topic` key、scope=database、identity=database+Topic、persistenceScope=instance 与 `.system/mq`。补六态、readonly/permission 程序与按钮门禁；冻结实际 API/endpoint/auth/profile/database/Topic 和同步 epoch，隔离迟返、ABA、同名跨库、新读与卸载。Topics/Browse/Stats/Offsets/Retention/Write 的401/403清旧 topics/消息/header/metadata/trend/结果/写草稿/审批并锁存；同身份刷新、空身份往返不解锁，显式安全恢复另验。错误正文固定脱敏。
- Browse请求1～1000，返回先截断再map/分类/显示/导出，历史写实际 preview count 与窗口/截断完整性；下一页沿最后保留消息真实offset前进，不使用超返尾项、不虚构cursor或全Topic快照。Inspector payload最多格式化4096原始字节，header有界预览；完整已加载payload仅为既有JSONL round-trip。完整Base64解码/传输/服务端扫描/字节/总堆预算单列。unsafe JSON整数offset不得用于Ack/分页/Seek，不伪造字符串精度。
- Seek保持最多25窗口，每窗最多1000消息，补60秒墙钟、严格前进、请求快照/取消；自动采样最多12轮/60秒且单飞，用户可显式重启。审批绑定原API/身份/项目，dispatch前一次消费；批次最多1000项/60秒，缺失/错目标/传输异常终态记unknown且不重放，401/403仍锁存。Publish真实终态topic+安全非负offset，Ack为topic+原consumerGroup+安全nextOffset；客户端abort不能声称Server未执行。
- Server、路由、其它模型和三宿主代码不改。Fixture/真实Kestrel既有兼容、新UI真实Server权限旅程、三宿主/安装/Extension Host/AOT/硬件/长稳/发行物分别记录；旧策略保留Temp不删除、不重试或绕过，禁止Graphify、广域工具扫描及未授权安装。
- 三代理已停止写入，第三冻结独立复核PASS；high-water自身safe门禁、Seek最终Browse/selection与总deadline、auto请求归属及fallback实际Topic同步epoch已补行为证据。最终专属Node22/22、全Web240/240、Chrome16/16、既有MQ浏览器3/3、TypeScript/Vite通过；既有真实Kestrel兼容2/2单列。初轮Chrome13/16发现结果抽屉内共享Panel缺inline的实际组合问题，修复后未降低Raw/deny断言。根证据目录`artifacts/wb22-validation-20261006`，门禁/清理见validation-report；完整metadata/解码/传输/总堆预算与真实新UI权限/三宿主另验。


## WB75（2026-10-08）：当前代码本地集成后暂停

唯一切片从0/5开始、闭合计1/5；修hidden.git读取并先执行全面真实本机不提交preflight，再冻结15路径/private H、完整restore/原CI format、独审、observed退出与本地commit/post。只原15路径，不重跑38/14/41，不启动Native实际旅程；WB74全部失败/过程false保留。证据artifacts/wb75-local-integration-20261008，最终以closed/Git绑定为准，不预填PASS。用户要求提交后暂停，heartbeat PAUSED，无push、新产品片或接续创建；夜间等明确继续。
