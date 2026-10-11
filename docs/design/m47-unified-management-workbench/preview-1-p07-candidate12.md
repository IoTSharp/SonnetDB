# M47-P07 候选12实物、分发与原始证据冻结

2026-10-11，源码 `5bcd3b811f2dae5005fc28fd70f6ff934e454c7a`，tree `2cebe49ba46b77bd0fedbad52c1f4156eca4c7c3`，版本 `4.5.0-preview.1.12`。源码已正常推至不可变 `codex/m47-preview12-source`；本地交付分支 `codex/m47-preview12-release`，main 和 parity-results 保持。候选11发布预检失败及所有旧 FAIL/UNKNOWN 保留。

原 P04 run38145580702 / attempt1 success，两 Actions 归档 size/digest 独立通过；ZIP 46,781,896 bytes，SHA256 `27787c8ce6af47327cd54f975e4b6eaede30d66c345aea9c6d051391e88ae824`。290 项库存逐字节、NativeAOT/PE、Server/Web/version/source/profile 和 VerifyOnly 全部通过。

实际套件 172 秒，通过 first/reopen/rollback/legacy 四阶段共37项记录；正常停止、61文件快照恢复与五类错配/端口/权限拒绝通过。Chrome 150.0.7871.115、Edge 155.0.4283.45 来自本轮原 About 记录。独立审计17回执/244身份、零残留任务进程/监听、ACL恢复和两个1000行导出通过；只证明本机Windows R1及合成数据/明示浏览器故障注入，不证明干净机器或生产。

实物243依赖、215项原许可文件引用已核；5项build-only实际无发出模块/二进制，Mapbox 2.0.3 MIT、自有指令源码、四项VC runtime及微软原文/Community用户资格记录/下游保护完整绑定。许可输入审查PASS，整体仍须原P07评审。新候选和接受审计绑定本轮非空实测；保留candidate10历史空窗作为排除记录，不伪造candidate12空窗。

[分发冻结](preview-1-distribution-12.json)只新增外部notes与独立三行checksum，原P04包/清单/checksum不变。[原文库存](preview-1-p07-evidence-12/inventory.json)的18份原JSON以gzip无损保存，原文SHA复核；不归档密码、环境/spec或截图。新远端prepare与P07尚待执行；十二同源门禁正在运行，当前NOT_READY、P08未发布/匿名首跑NOT_RUN、workbench PAUSED。后续结果追加原报告，不改冻结记录。

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
