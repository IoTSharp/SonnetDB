# M47-P08：发布执行、匿名回读与前置闭环

2026-10-11（Asia/Shanghai）。**发布工具和审批环境已准备，产品仍 NOT_READY，尚未公开发布。** [机器记录](preview-1-p08-10.json)分列远端配置、合成合同、真实只读请求和未执行的发行验收。候选10、原包、P07记录及历史FAIL/UNKNOWN不改写。

## 真实审批环境与执行身份

用户在本聊天同意配置环境并指定GitHub用户`maikebing`。实际创建`workbench-preview-p08`，独立API回读environment ID24006677696、required reviewer `maikebing`（ID3445167）、prevent_self_review=true；随后关闭GitHub默认的管理员绕过，并独立核can_admins_bypass=false。上传器同步拒绝可绕过的环境，不降低原审批和完整verifier。创建工具第一次相对import错误在任何API/写入之前失败；原脚本/日志保留，纠正后仅一次create PUT及一次保护强化PUT，未发起发布工作流。

当前凭据对应`maikebing`，不能批准由自己发起的发布。门禁全部通过后，需要另一个有权限的GitHub账户发起冻结候选的publish，再由`maikebing`完成环境审批。这里没有配置第二账户、发消息、绕过审批或替人批准。

## P08发布和回读工具

复用P06的独立四附件发布工作流；不触发`v*`渠道，不增加公开资产类型。仍须P07 READY、实际冻结授权串`P08:<tag>:<distribution SHA256>`、环境审批及上传前原完整live verifier READY。原policy与verifier的内容/SHA不变。七天scheduled Parity始终是非阻断观察。

上传器现在在每次写入之前和取得原ID之后，保存不可覆盖的`publish-journal-NNN.json`。即使后续上传未知或执行器中断，已知tag、releaseId、asset ID和当前phase可供只读对账；每次写请求仍只尝试一次，不clobber、移动tag、续传或盲重试。磁盘写journal失败时停止下一写入。新日志不把原UNKNOWN改为成功。

[Invoke-P08Readback.ps1](../../../eng/workbench-preview/Invoke-P08Readback.ps1)提供两个有界Windows Job阶段：

```powershell
# 仅在候选N已通过完整门禁、授权并实际公开之后；两个阶段使用同一个新输出根。
./eng/workbench-preview/Invoke-P08Readback.ps1 -Mode download -Number N -Output D:/p08-evidence-N
./eng/workbench-preview/Invoke-P08Readback.ps1 -Mode first-run -Number N -Output D:/p08-evidence-N
```

download全程匿名：不用gh登录、Authorization或Cookie；每请求最多一次直连和一次127.0.0.1:7890代理fallback，最多48次请求/480秒，单次45秒、最多5次redirect。只允许HTTPS GitHub及明确release资产域名，保存响应host/path而不存签名query；传输异常也只保留固定安全原因，避免异常消息泄漏签名URL。流式读取有字节/读取次数/时间/取消上限；失败partial保留。先校验安全冻结清单，再取公开release/tag/latest/资产列表/页面及四份browser_download_url，核精确notes、轻量tag source、prerelease且非latest、资产ID/大小/sha256、checksum和全部ZIP entry/两个内嵌身份。不把API状态码当成文件证明。

first-run只消费上述匿名下载且重核字节/库存，解压到新任务目录，复用实际NativeAOT Server＋Chrome/Edge R1真实UI旅程。合成数据准备、浏览器故障注入与实际密码登录/导出/审批继续分列；正常停止、原Job成员清空及自有运行目录回收分别记录。缺进程终态/清理证明时保留文件。没有公开包时不得写首跑PASS；不能升级为干净Windows、其它宿主、生产或崩溃恢复。

## 许可修复准备与未解决项

准确版本原registry tar的七份SHA512完整性已核。固定css-render monorepo commit的MIT原文覆盖plugin-bem、vue3-ssr和css-render三个同gitHead包；vue-devtools原文覆盖准确6.6.4；murmurhash-js 1.0.0完整README内有Gary Court原MIT正文，从已核integrity的原tgz取得。五项通知作为三份无损gzip原文及版本/integrity/source/SHA清单进入`eng/workbench-preview/licenses-supplemental`。组包时核原文、压缩文件与准确已安装依赖，再写入包内并追加provenance；错误版本、integrity、缺依赖或buildOnly标记均拒绝。

这只是继任包的五项修复准备。**候选10包仍有原11条运行时条件，不能把新工具测试迁移为候选10许可PASS。** 尚有两个npm缺口：`@mapbox/jsonlint-lines-primitives@2.0.2`的准确gitHead根tree没有许可原文、metadata也无声明；`vdirs@0.1.8`原tar/README未找到许可原文，尝试固定gitHead来源不可用。没有从另一项目的同名MIT正文推断授权。原匿名API403及认证Git tree原回执保留；须取得适用原文/覆盖证据，或另行实施有来源和验收的替换/剔除。

Microsoft 2026官方再分发清单已取得，明确包含VC/redist中的未修改文件，并列明有效Visual Studio许可及适用条款前提；原包登记路径/四个DLL版本/hash保持。清单没有替发布者证明许可资格，也没有补齐要求分发者和终端用户同意的保护条款。原VC V14终端runtime条款还明确限制再分发，不能把它单独当成分发授权。未取得这些条件前不声称Microsoft许可清关。

任何包内补通知、条款、配置或依赖变更均须下一未使用candidate/version、干净同SHA重包、受影响P04/P05验收及完整同源P07。当前没有浪费新版本重建仍未许可闭环的包；没有修改candidate10/profile/ZIP/manifest/checksum/原P07。

## 真实验证与接续

最终源码86项Node组包/P07/分发及新增P08合同通过；五项PowerShell补件合同（准确复制及四类拒绝）通过，fixture已回收。第一次补件测试发现PowerShell自动变量`Matches`被regex覆盖，原两次FAIL保留，改用专用变量后新收据PASS。最终复核纠正vue-devtools provenance中的css-render说明，原收据不回写。五份PowerShell源码解析无错误；四种不安全URL、三种含签名内容的异常及两个安全原因保留合同通过。

真实匿名仓库页面stream微验证通过，四种不安全URL被拒绝；这不是Preview公开发布证明。候选10匿名release请求实际返回404，原Job FAIL/控制器REFUSED、mutation=false、firstRun=NOT_RUN，另记录预期拒绝；没有把该FAIL覆写为PASS。

原完整verifier再次实际运行：首次匿名配额不足收据保留；使用现有凭据仅在进程内重新取证后仍4/12通过，八项同package source54eefc81没有有效运行。七天scheduled观察本轮因原gh JSON解析错误为UNAVAILABLE/nonblocking；P07此前7/7 READY和此次错误各保留。未降低light/full、五个Workbench job、具名step/artifact或其它必选项；未将新工具HEAD当包来源。

下一顺序：两个npm原文/替换及Microsoft适用资格/保护条款闭环 → 新候选/版本/干净构建与受影响实物验收 → 同包完整12workflow（Parity both且publish_results=false；三个发布workflow只dispatch preflight）→ 新P06/P07冻结和artifact → 精确实际授权、另一账户dispatch及maikebing环境审批 → 一次发布 → 匿名download与真实first-run → 确认完成后归档。未通过前保留NOT_READY。

本轮证据根`artifacts/m47-p08-20261011`；归档仅保留适用非凭据收据。完整restore/原CI format、owned-only暂存、默认whitespace、正常提交/推送/独立remote回读及非空本机进程审计的实际结果以本轮delivery.json为准，未取得结果不预写PASS。外来dirty/交接旧字节、parity-results和PAUSED自动化保持；无工作树/子智能体、生产部署、服务安装、其它发布渠道或编辑器重启。
