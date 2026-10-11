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
