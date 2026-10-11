# M47-P07：同候选门禁与放行评审

2026-10-11（Asia/Shanghai）。**当前结论 NOT_READY，不放行公开发布。** 本片建立原证据收集、同候选验收映射、工具库存和真实远端 prepare 验证；收集成功与门禁通过分别记。P08 仍需另获明确授权，Workbench 自动化保持 PAUSED。

## 不可变候选与原证据

包来源 `54eefc816f133a805b0d38f0cbe930d2c17650b6`，candidate-10 / `4.5.0-preview.1.10` / preview-1 / R1 / win-x64。原 build run `38104431383` / attempt 1、ZIP 46,634,305 bytes / `d01729b97a8db906ffb78bf88138cfa58d91b708efaf03fa408c0b59aaab51ed`，以及 candidate、manifest、原 checksum、审计补记均保持字节。包外工具提交与包源码分别记录；未重建或覆盖候选10。

[原证据库存](preview-1-p07-evidence-10/inventory.json)按冻结 candidate/P05 中的 SHA 无损保存真实 first/reopen/rollback/legacy、suite、两项独立审计和原失败，不复制密码、token、Cookie、环境/spec 或截图。仓库用gzip保留原CRLF字节，收集器先解码并核原size/SHA，再把原JSON放入Actions artifact；不规范化原收据。原空窗审计有效状态仍为 `FAIL_EMPTY_WINDOW`；纠正审计必须至少15份收据/100身份、0live/0监听，不能用空集合代替。

[P07收集入口](../../../.github/workflows/workbench-preview-p07.yml)仅手动、contents/actions read。不可 breakaway Windows Job 约束660秒；controller最多30条外部命令/600秒，原API收集最多100请求/390秒，检查取消文件并保留双流/身份/清理收据。原 verifier/policy 未修改。原 JSON 与验收归一记录分开，归一记录保存原文件/完整SHA及选中记录名，缺原文、错源、错hash或非PASS不得放行。

## 原完整发布门禁

本地真实原 verifier 于 `2026-10-11T05:21:00Z` 返回 NOT_READY：ci、codeql、docs-pages 和 management-workbench-smoke 四项同来源通过；parity、document-soak、ecosystem-soak、m19-capacity-evidence、m39-trigger-evidence、publish、connectors-release、docker-publish 八项没有该完整包源码的有效运行。五个 Workbench job、原具名step、有效artifact均按原policy检查，未删减。

本次先完成证据与放行评审；没有为已确认许可阻断的不可变包启动八套额外构建/容量任务。未来取得这些证据必须仍在包的完整SHA/version/latest attempt上执行原门禁，不得以工具HEAD、历史4.0.0、旧attempt或夹具替代。若许可修复改变包内说明或文件，须新候选/version及受影响验收，不能补写候选10成功。

七天scheduled Parity仅作非阻断观察。初次原观察因gh JSON解析失败为UNAVAILABLE；原错误保留，不把它变为阻断，不削减当前候选light/full。

## 同候选验收与许可

| 检查 | 当前证据 | 边界 |
|---|---|---|
| C01～C05 | 冻结P05真实Chrome/Edge R1记录，逐名/状态/源码/hash核验 | 延期七模型拒绝、真实权限/审批/撤权/版本错配等；保留合成fixture和浏览器故障注入分类，不推断生产、干净机器或Server扫描停止 |
| P05-installed-R1 | 原suite10记录、正常停止/重开/61文件同候选快照、独立审计 | 仅记录Windows主机的实际解压包；不升级旧失败或空窗 |
| D01 | 本地四附件合同已有，真实prepare首次失败；修正后待远端验收 | 精确四附件/279 ZIP entry/原hash；不等于公开下载或发布 |
| licenses | [原审查无损归档](preview-1-p07-evidence-10/license-review.json.gz)，解码后的license-review.json进入artifact，NOT_READY | 243记录、16原缺口；5个构建工具排除于本包，7个运行时npm通知缺口与4个Microsoft条款条件未闭环 |

Microsoft原Enterprise/Professional 2026及VC++ V14 runtime条款已从官方页面取得原DOCX，并保存下载URL、SHA和文本SHA；原构建使用Hosted Enterprise登记CRT来源，不能拿本机Community许可替代。[官方许可证索引](https://visualstudio.microsoft.com/license-terms/)及[再分发指导](https://learn.microsoft.com/en-us/cpp/windows/redistributing-visual-cpp-files)是来源。仍须核登记版本适用redistributable-list和向分发者/终端用户提供的保护条款，不将四个Microsoft DLL归为MIT。

npm审查以准确版本、原integrity与registry元数据/固定gitHead核上游原文。`css-render`与BEM插件固定上游MIT正文含07akioni版权通知，包中没有该原文；naive-ui/TuSimple的另一条通知不能替代。其它未取得原文或未证明包内覆盖的运行时条目维持NOT_READY，另存原获取失败。其余227记录只验证声明许可文件在包库存及原hash存在，不冒称逐条法律清关。

## prepare与工具评审

已实际单次dispatch prepare：run `38114436815` / attempt 1 / tooling `eb244cfd3a339c4e0733c70fb4dec15f957a7acf`，结论failure。原controller为REFUSED、Frozen record drift、0 API命令、mutationStarted=false，原artifact ID `11692363372` / digest `sha256:9c132d2e8ec42430ef4bba61138b0835ef8fdc32d16be78fd4106085d425d7a6`保留。

本地候选/审计/notes/两原gate与Git blob逐字一致。Windows工作流在checkout前设置core.autocrlf=false，避免Git换行转换破坏字节冻结；原candidate/包不改。该工具修正须正常提交后，以新tooling run真实重验，不把原失败回写成功。

收集器只有成功当前工具prepare才生成D01归一验收。最终P07机器记录需绑定成功收集run/attempt/artifact ID/digest与toolingCommit，并记录指定10个distributor输入和5个collector输入SHA；NOT_READY记录不会满足既有发布控制器。远端结果与最终提交/保护检查以本报告后续补记和本轮delivery收据为准，尚未执行的结果不计PASS。

本机合同测试是合成验证，真实原旅程/远端workflow另外核验。证据根 `artifacts/m47-p07-20261011` 保留所有原FAIL/UNKNOWN，临时对象仅按任务归属/绝对路径回收。没有tag、公开Release/附件、匿名下载首跑、恢复自动化或跨聊天发送。

第一次owned暂存被默认whitespace检查拦住原CRLF收据及新增交接行尾，原FAIL双流保留、没有commit。仅撤回经核38个自有index条目，原JSON副本按核实hash无损压缩，原P05文件不动；HEAD交接旧字节保留，仅新增owned段在index用LF。重新执行合同测试、完整restore/原CI format及默认staged whitespace，未降低原格式门槛。

第二次stage仍发现原Rollup许可证中的Markdown引用空行尾空格；该原文也不能改写，遂对全部上游原文本使用同一无损归档/解码/SHA核验，原FAIL另存。收集器进一步直接把每个旅程/执行结果/suite/audit的SHA对到冻结candidate/审计补记，把243依赖原JSON SHA对到已核包manifest，防止只重写归一库存就改变原收据。新增替换hash拒绝场景；再次最终树门禁后才允许commit。

远端工具修正第一次复验：run38116277262/attempt1/tooling69e6705d，工作流真实failure；controller实际PREPARED_NOT_READY、9命令/12.57秒、mutation=false，原Job PASS/exit0/cleanupComplete=true。失败位于Invoke-Distribution的LASTEXITCODE判定：Windows API启动不会给新PowerShell进程赋该变量，null -ne 0误报。两调用层改为核原Job JSON并只在PASS/exit0/cleanup=true后设置0；6项包装合同验证null、残留55、真实子进程exit7拒绝，均保留原收据，仅controller夹具，不计真实分发通过。再次完整restore/原format/owned-only/default whitespace后正常提交推送，新的实际prepare/P07结果仍待取得。

## 真实远端复验与最终评审（2026-10-11）

prepare run38116860908 / attempt1 / tooling 1f3709945f97bfd656538570d1d8b8cf3b0cba24整体success，prepare job及三具名步骤success，publish job skipped。原controller PREPARED_NOT_READY、9命令/9.016秒、mutationStarted=false；原Job PASS/exit0/cleanupComplete=true。四附件下载后逐size/SHA核验，ZIP仍46,634,305bytes/279entry，candidate、manifest、原checksum和审计补记不改。两个原prepare failure保留原结论。

P07 run38117134746 / attempt1 / 同tooling整体success；collect job和具名收集/保留步骤success，原Job PASS/exit0/cleanupComplete=true，收集31.974秒/7外部命令。成功artifact workbench-preview-p07-10 / ID11693529921 / digest sha256:f9cec7973d7c6a0afde07a99fdbcb89b70e2a5fb22d2b077dc0bc787cd3d8208 / expiresAt 2026-11-10T06:10:27Z。独立下载全部三份prepare/P07 archive并核SHA256与API digest/size一致；核20原JSON、7上游原文、8归一检查的原size/SHA、冻结candidate/审计、243依赖manifest绑定及10分发/5收集工具Git blob。原本地独立核验因p07收据文件名写错而失败，原FAIL/双流/脚本保留；纠正后完整复核PASS，未更改远端原文。

[最终机器记录](preview-1-p07-10.json)来自该真实artifact。C01～C05、D01、P05-installed-R1共7/8检查PASS，licenses仍NOT_READY；原verifier再次4/12通过、8项缺同package源码的有效运行，9组blockers保留，**评审完成、未放行**。原full policy/light/full不降低，NOT_READY记录已实际核验被既有发布校验器拒绝。不能把工作流success或D01归一PASS称为产品READY。

七天scheduled Parity此次原report为READY、7/7、100%；light/full原profile各51场景。原初审UNAVAILABLE不覆盖，最新观察仅适用于report中的历史来源，不替代candidate10源码54eefc81的parity gate。观察始终nonblocking。11项运行时许可条件未闭环，5项build-only排除不等于243项全部清关；如修复包内通知，必须新candidate/version、重新构建及受影响验收，再取全部同包来源门禁及新的P07评审。P08未执行，仍须所有门禁通过与独立实际发布授权。

本轮44项Node合同及6项真实Job包装合同已通过，最终文档/机器记录再完整restore、原CI format与默认staged whitespace后精确owned提交并正常push；实际最终SHA、远端回读和非空本机身份收口以artifacts/m47-p07-20261011/delivery.json为准。GitHub Hosted身份不用于本机审计。原FAIL/UNKNOWN、外来dirty、根HANDOFF原字节与parity-results保持；workbench仍PAUSED。

## 候选12 P07放行评审完成（2026-10-11）

**P07 READY：原十二门禁12/12、八项验收8/8全部通过。** 包source 5bcd3b811f2dae5005fc28fd70f6ff934e454c7a / 4.5.0-preview.1.12，分发工具 13cb7a78bb9d0a0ea097444d20865f34232b988a；原候选10/11及所有旧FAIL/UNKNOWN保持。prepare 38147374946 / attempt1 success，P07 38148424602 / attempt1 success；原P07 artifact 11705379779 / digest sha256:1f29a0b1209d84b7a75c742ba69791c6331f7ce75399d94b2ab6d9ac65a023f1，到期 2026-11-10T14:45:50Z。

独立核三份原Actions archive size/digest、18份冻结原JSON解码SHA、8份归一检查/原始证据、10分发+5收集工具Git blob与原policy/verifier字节；D01原controller PREPARED_NOT_READY/mutationStarted=false及Job PASS分列。原verifier READY绑定全部12同source workflow、job、具名step、最新attempt和artifact，并验证真实light/full原Parity证据；七天scheduled只观察，不替代本候选或作为阻断。新[P07记录](preview-1-p07-12.json)绑定真实原artifact，[无损原文](preview-1-p07-review12-originals/inventory.json)保持原字节。

同候选实物37记录、17回执/244身份、两个1000行导出及许可输入审查沿用本轮已冻结实际记录；243依赖/215原文引用、五项未随包发出的构建工具、Mapbox2.0.3、自有指令源码和微软Community/原条款/下游保护绑定。无生产、干净机器或Microsoft资格认证推断。包/manifest/原checksum、冻结candidate/审计/notes/distribution及源码分支不变；原NOT_READY/NOT_RUN快照不回写为READY。20个具名运行期目录中仅实际存在的任务包副本、合成数据/快照与浏览器profiles已回收，原回执/导出和发行物保留；无按名杀进程。

P08明确未完成：当前凭据/必审人均maikebing，环境prevent_self_review=true/can_admins_bypass=false。精确发布请求已在[P08记录](preview-1-p08-12.json)绑定四附件与distribution SHA；需另一有权账户dispatch，再由maikebing审批。tag/Release当前404；本轮未发publish、未上传，匿名下载/公开包首跑NOT_RUN。后续通过一次实际发布、匿名四附件/ZIP验证及真实首跑后才称P08完成。main及parity-results保护，外来dirty保留，workbench PAUSED。

用户最新决定覆盖此前禁止自批及需另一账户的规定：允许 maikebing 发起并实际审批，required reviewer maikebing 与 can_admins_bypass=false 保留。GitHub环境已一次PUT并独立GET核验 prevent_self_review=false。原候选12、包、四附件、source分支和旧P07原文保持；变更后的工具需重新prepare及P07原评审12/12与8/8，再依已冻结请求一次publish、实际环境审批、匿名四附件和公开包首跑。本段为策略实施状态，不预写发布完成。

## 允许自批后的P07工具复验通过（2026-10-11）

用户明确允许maikebing自批，环境独立GET核prevent_self_review=false、maikebing必审/can_admins_bypass=false。修改后的分发工具ca717fd40912ee583247d95dfad0c55345c63159已通过86项合同、完整restore/原CI format并正常提交推送；真实prepare 38150203876/P07 38151053382均attempt1 success。原12/12同源码工作流、8/8验收全部READY，三archive及10+5工具Git blob和18原文独立核验PASS；新P07 artifact 11706990099/digest sha256:0e4f59a3cf1a2f496dd62fbc20236c1ee9f346f5e32e3f4fe3902a246ae70e83。旧政策评审与所有FAIL/UNKNOWN保持。包source5bcd3b811f2dae5005fc28fd70f6ff934e454c7a、4.5.0-preview.1.12及四附件逐字冻结。P08下一步按原精确请求一次publish，由maikebing实际审批，随后匿名下载四附件及公开包首跑；当前均NOT_RUN。

## 允许自批及上传修复后的P07工具复验通过（2026-10-11）

用户明确允许maikebing自批，环境独立GET核prevent_self_review=false、maikebing必审/can_admins_bypass=false。修改后的分发工具4ff3e6219768aaae5344254d5503d2f2cb902ff4已通过94项合同、完整restore/原CI format并正常提交推送；真实prepare 38153124333/P07 38153356012均attempt1 success。原12/12同源码工作流、8/8验收全部READY，三archive及10+5工具Git blob和18原文独立核验PASS；新P07 artifact 11707043927/digest sha256:e69096502649bea8a64b69e47924a27d6ed1e2d1993476a6a3733e5b24903088。旧政策评审与所有FAIL/UNKNOWN保持。包source5bcd3b811f2dae5005fc28fd70f6ff934e454c7a、4.5.0-preview.1.12及四附件逐字冻结。P08下一步按原精确请求一次publish，由maikebing实际审批，随后匿名下载四附件及公开包首跑；原publish38151505875失败、409754336空草稿保持；恢复发行和公开首跑未完成。

## 原草稿ID读取修复后的P07工具复验通过（2026-10-12）

用户明确允许maikebing自批，环境独立GET核prevent_self_review=false、maikebing必审/can_admins_bypass=false。修改后的分发工具67b263b431d0b23a565546a90bf74da00d4f22d7已通过94项合同、完整restore/原CI format并正常提交推送；真实prepare 38154883708/P07 38155021627均attempt1 success。原12/12同源码工作流、8/8验收全部READY，三archive及10+5工具Git blob和18原文独立核验PASS；新P07 artifact 11708600894/digest sha256:b6f21ea9e50eeefc5cb0cf2e53ed77d585302fe9823b0e463dff5b4402e0f136。旧政策评审与所有FAIL/UNKNOWN保持。包source5bcd3b811f2dae5005fc28fd70f6ff934e454c7a、4.5.0-preview.1.12及四附件逐字冻结。P08下一步按原精确请求一次publish，由maikebing实际审批，随后匿名下载四附件及公开包首跑；原publish38151505875失败、409754336空草稿保持；恢复发行和公开首跑未完成。

## M47-P07、M47-P08候选12实际收尾完成（2026-10-12，Asia/Shanghai）

P07更新工具原评审38155021627 attempt1 success，原12/12同源码门禁、8/8验收全部通过。P08实际publish 38155325276 attempt1 success，工具8a24edab4a00f82c40912248fa37fdbe1de4bb3a、已审分发工具67b263b431d0b23a565546a90bf74da00d4f22d7；maikebing按用户允许自批的明确决定实际批准原环境24006677696，无管理员绕过。沿原tag/Release409754336完成精确四附件公开，Release 409754336/tag workbench-preview-1.12/published 2026-10-11T16:27:58Z，[下载页面](https://github.com/IoTSharp/SonnetDB/releases/tag/workbench-preview-1.12)；tag不可变指向5bcd3b811f2dae5005fc28fd70f6ff934e454c7a。原notes/manifest/ZIP及四附件SHA与冻结distribution完全一致，preview/prerelease且未成为latest。

独立核三份发布Actions archive size/digest、两个发布前实时完整P07/原verifier、原Release/四asset ID/digest、tag/notes/latest/审批环境全部通过；真实匿名HTTP页面、四附件、三行checksum/290项ZIP库存及NativeAOT/Web身份通过。匿名公开下载包Chrome/Edge本机首跑26条全部PASS，真实UI登录/权限、预算导出/单行审批与明确故障注入分列，正常Ctrl+C/Server退出0、Job清理通过。172回执/1573身份独立核零活精确身份/零5080或5081监听；任务包副本、合成数据和浏览器profiles已回收，原四下载物、回执、导出及全部旧失败保留。

这是冻结Windows x64 Web Admin+匹配NativeAOT R1预览的公开交付；Studio/VS Code、干净机器、其它宿主、生产与SonnetDB4.5正式版不由本结果推断。243依赖许可输入与Community用户资格沿冻结记录，不是Microsoft资格认证。新[P08机器记录](preview-1-p08-12.json)及[无损原文库存](preview-1-p08-12-originals/inventory.json)绑定实际ID/SHA；原候选10/11、旧禁止自批评审/用户覆盖决定、EOF/空行失败和所有FAIL/UNKNOWN保持。

本次收尾docs及HANDOFF自有增量将在最终完整restore/原CI format/default cached whitespace通过后精确提交并正常推codex/m47-preview12-release；最终SHA/tree及独立远端回读以delivery.json为准。冻结source/main/parity-results与外来dirty保持，workbench PAUSED；没有新的工作树/智能体、跨聊天、生产部署或服务安装。P07/P08范围已完成。

早期辅助CI observer watch-ci12-b曾因ENOSPC留下0字节terminal，原状态/当时回收仍UNKNOWN。初次严格审计因此拒绝，未补写旧PASS；新独立审计逐原六文件SHA限定该历史例外，核原root/controller及非空ledger当前身份已不存在或不同创建/命令/父链，同时核全部完整回执及公开首跑零残留。此观察不补授该旧任务成功或未记录子进程证明；无损历史原文与初次拒绝纳入P08库存。
