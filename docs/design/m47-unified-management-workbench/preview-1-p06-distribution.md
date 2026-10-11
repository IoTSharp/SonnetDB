# M47-P06：预览说明与独立分发

2026-10-11（Asia/Shanghai）。**P06 本地说明、四附件准备及限定分发合同完成；产品 NOT_READY，P07/P08 待验。** [发行说明](sonnetdb-workbench-web-4.5.0-preview.1.10-notes.md)覆盖实际 R1、首次使用、限制、权限/unknown/预算、同候选快照回退与 Issues 反馈；没有公开下载、tag 或 Release。

## 实物与不可变边界

[分发补记](preview-1-distribution-10.json)是候选10的独立附加记录，绑定原[候选](preview-1-candidate-10.json)、[审计纠正](preview-1-candidate-10-acceptance-audit.json)、原 build run `38104431383` / attempt1、artifact ID/digest，以及恰好四个附件的大小/SHA256。包源码 `54eefc816f133a805b0d38f0cbe930d2c17650b6`、版本 `4.5.0-preview.1.10` / preview-1 / R1 / win-x64 保持。

在 `artifacts/m47-p06-20261011/delivery-10` 独立准备四附件：原 ZIP 与 manifest 逐字复制、添加 notes、生成只覆盖 ZIP/manifest/notes 的三行 SHA256SUMS。原 P05 delivery-10 的三文件及两个候选记录保持原 hash/size，原 checksum 未覆盖。ZIP仍为46,634,305 bytes / `d01729b97a8db906ffb78bf88138cfa58d91b708efaf03fa408c0b59aaab51ed`，279个包内 entry 逐项路径/大小/hash与两个内嵌身份独立通过。

本片仅增加包外说明、分发工具与新的附加清单，不重包、不改产品源码/依赖/profile/配置/包内说明，所以使用候选10原版本。原 manifest 和包内 START-HERE 的 P05/P06 NOT_RUN 是构建时快照，后续验收单独补记。工具工作流源码使用自己的完整 `github.sha`，与 package source 的完整 SHA checkout 分开记录。任何重建或包内输入/字节变化须新候选 N、新版本和受影响 P03/P05/P07 验收；不能重新构建10或把分发工具提交 SHA冒充包来源。

## 独立入口与上传行为

[workbench-preview-publish.yml](../../../.github/workflows/workbench-preview-publish.yml)仅 `workflow_dispatch`，默认 `prepare`，没有 push/tag/schedule 自动发布。prepare job 只有 contents/actions read，按指定 run 下载原 artifact、核最新 attempt、仓库/来源/workflow/ID/digest/有效期、干净 full-SHA checkout/tree/gitlinks、原候选/P05/hash、精确四附件及 ZIP库存；只保留 Actions 审阅产物。当前没有运行这一远端入口，本片实证是本地准备及只读来源核对。

publish 必须先经过只读 P07 gate job，再进入 `workbench-preview-p08` 环境。环境须由仓库维护者配置 required reviewers 和 prevent_self_review；代码读回实际规则，缺失则拒绝。只有 publish job 获 contents write；必须人工选择 publish 并输入 `P08:<完整保留tag>:<分发补记SHA256>`，该输入是 P08 操作确认，不代替本聊天以外的实际发布授权。P06未创建环境、未授权或派发发布。

[controller](../../../eng/workbench-preview/dispatch.mjs)在不可 breakaway 的 Windows Job内执行，最多64条外部/API命令、600秒总窗口、660秒外层上限、取消文件和finally清理。调用原 verifier 的完整参数，P07 gate 与 publish 前各重新取真实结果；[旧policy](../../../eng/release-readiness-policy.ps1)和[旧verifier](../../../eng/verify-release-readiness.ps1) SHA封存在补记，内容改变先重新评审。12个workflow、五Workbench job、全部step/artifact及其余既有必选要求不削减；七天scheduled Parity仍非阻断观察。

[publisher](../../../eng/workbench-preview/release.mjs)只使用显式四文件数组：一次建立非 `v*` tag到包source、一次建立 draft prerelease、逐个上传、按原ID下载核全部SHA256、一次公开draft，再核tag、公开API字节与latest状态。prerelease=true、make_latest=false，无clobber、tag移动、Studio/VSIX/NuGet/容器/系统包副作用。API回读只授认证API字节证据；P08还需独立匿名公开下载和首次运行，不能称完整生产验收。

已有tag/Release先只读核来源、附件/大小/hash、完整notes和分发SHA。已完整公开且匹配时零写返回；错配、只存在tag、缺附件、已有draft或未知结果一律停留对账，不续传、不自动重试或覆写。任一写请求效果未知保存phase、已知releaseId/uploadId和UNKNOWN；原记录不改成功，必要修正分配新版本。

## P07 接续接口

P07需交付 `preview-1-p07-10.json`（本片未创建或伪造READY），schemaVersion=1、milestone=M47-P07、status=READY、source=github-and-installed-package，以及 repository/candidate/version/sourceCommit/distributionSha256。产品来源是上述54eefc81，不能填分发工具SHA。

另记录已审查工具的 `distributorCommit` 和 `distributorInputs`：workflow、distribution.mjs、release.mjs、github-api.mjs、dispatch.mjs、Invoke-Distribution.ps1、Verify-Distribution.ps1、Invoke-Owned.ps1、JobProcess.cs、package.mjs，共10条相对路径/SHA256。controller核当前完整文件库存与hash；P07文档提交可以晚于工具提交，不能产生“P07文件必须引用含自身的SHA”循环。每次执行还记录实际github.sha。

`checks` 必须恰好覆盖 C01～C05、D01、P05-installed-R1、licenses 八项，各含 id/status=PASS/file/sha256。file为单个安全JSON文件名；原始验收JSON必须匹配candidate/version/sourceCommit/check/status，不能用旧来源PASS、FAIL/UNKNOWN/NOT_RUN或空文件放行。P07须把原证据及归一验收记录放入已完成成功Actions evidence artifact，记录 `evidenceArtifact` 的 repository/runId/attempt/id/name/digest，及其run源码 `toolingCommit`。入口按最新attempt、artifact ID/digest/所属run/有效期/生成时间重新核，再下载逐证据校验。归一记录需指向实际原始结果，P07评审负责查验原始旅程与243依赖/16待审许可证；不能把测试夹具当真实证据。

现有 verifier 还必须在上传前实时返回同package SHA/version 的READY；不接受用户提供的本地READY作为替代。任何必选项不足会拒绝；scheduled观察不足不额外阻断。尚未完成P07、环境审批和P08授权前，只能准备/审阅。

## 本轮验证与未验收项

本地35项合成合同通过，包含正常集合、混入Studio/旧VSIX、缺四类资产、版本/source/tree/profile/RID错配、hash/原checksum漂移、旧attempt/过期artifact、P07缺项和非READY、P08缺授权/自批、已有远端错配零写、未知写不重试，以及GH响应解析/显式上传URL。模拟发布器不连接真实发布API，不是实际公开发布证据。实际ZIP及附加说明另行独立校验，不冒称这些测试运行产品Server。

只读GH API重核run38104431383/attempt1/success及artifact未过期；直连被关闭的原FAIL、代理后的PASS分别保留。生成补记工具首轮相对import路径错误，原脚本/FAIL保留，修正后新收据PASS；没有改变候选字节或真实安装结果。旧DateKind空窗按effective FAIL_EMPTY_WINDOW保持，日期字符串通过DateKind String或Node JSON解析后显式比较，不沿用错误空窗PASS。

原始证据、合成测试双流、受控Job收据及四附件在 `artifacts/m47-p06-20261011`。最终树restore/原CI format、精确owned stage、正常提交/推送、保护外来dirty与parity、收口身份审计的实际结果在本轮交接和delivery收据；本文件本身不把尚未执行的门禁记PASS。

Workbench自动化继续PAUSED。P07完整门禁/许可/放行、P08实际授权/tag/Release/匿名下载/首次运行、其它宿主/七模型/生产/崩溃/任意升级降级均未验收。下一片只接P07，不提前发布或恢复自动化。
