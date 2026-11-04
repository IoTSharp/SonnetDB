# 5.0 发布恢复候选审查与后续 Issue

日期：2026-11-04（Asia/Shanghai）。本轮按用户授权为 SonnetDB 未完成项建立后续 Issue；生产脚本、工作流、根 HANDOFF/CHANGELOG、Git index、main/tag 和发布状态保持本轮只读范围。

## 当前决定：不采用当前恢复候选

保留 [codex/resume-tag-publication-20261102](https://github.com/IoTSharp/SonnetDB/tree/codex/resume-tag-publication-20261102)，冻结审查提交为 [8bf0e81222784db8b6a014619eaa9eb97d37cb31](https://github.com/IoTSharp/SonnetDB/commit/8bf0e81222784db8b6a014619eaa9eb97d37cb31)。当前不合入、不 dispatch 该恢复候选，将其作为未来增强交由 [Issue #402：补齐 tag 写前复核、外部结果对账与已验证资产白名单](https://github.com/IoTSharp/SonnetDB/issues/402)继续。

依据为 [#230 的跨机器交接评论](https://github.com/IoTSharp/SonnetDB/issues/230#issuecomment-6612860491)和已推 Git blob。本次没有假定另一机器的 ignored artifacts 可读，也没有把作者本机结果当作本机或 Actions 已验证结果。

恢复入口适用于最终 tag/run/source 已冻结、构建和平台合同成功而发布阶段失败的场景。当前正常新 tag 发布已读取新运行的 NUGET_API_KEY、验证新十包并等待平台消费者合同。当前仍应逐项完成 #389、MCP #391、WorkBuddy #390 的统一新 source/SHA 与全部十二类门禁，再走正常新 tag 发布；恢复旧 455bf3b5 制品不能解决新包名和新源码要求。本增强不改变该路线，也不作为当前发布新增阻断门槛。

## 只读范围与兼容结论

审查最多八个明确文件：候选的 resume-tag-publication.yml、tag-publication-policy.ps1、test-tag-publication-policy.ps1、CHANGELOG/HANDOFF 差异，以及已推 63af4c98579276f029ab5246a232ea30fc4c126a 的 publish.yml、verify-release-artifacts.ps1、verify-release-readiness.ps1。相对共同基线 78c2c746，候选自有差异为五个路径；8bf0e812 是合并 main 的提交，其单次 merge stat 不能直接当作 owned 文件清单。

来源策略核对 repository、push-tag 事件、完整 SHA、workflow 路径、completed 状态与最新有效 Publish/Connectors 的三加三合同。发布逻辑没有写死旧 SHA 或旧 Testcontainers 包名；旧 SHA 位于测试夹具/历史交接。当前十包验证器包含 SonnetDB.Protocol、SonnetDB.Client、SonnetDB.Testcontainers。

策略要求 tag push，因此不能把当前或最终 preflight workflow_dispatch 的 artifacts 直接晋级为 tag-push 产物。未来若采用恢复入口，仍须绑定最终新 tag 的真实 push run 和精确制品身份。

## 四项需在后续闭合的代码证据

1. **tag 写前复核**：[候选 workflow L69](https://github.com/IoTSharp/SonnetDB/blob/8bf0e81222784db8b6a014619eaa9eb97d37cb31/.github/workflows/resume-tag-publication.yml#L69)仅在开头核 frozen HEAD。后续 L131 推包及 L147/L151 写 Release 前没有重新核远端 tag 的实际 commit；对应 Actions concurrency 不能阻止外部移动 tag。
2. **已存在/未知外部结果对账**：[L131](https://github.com/IoTSharp/SonnetDB/blob/8bf0e81222784db8b6a014619eaa9eb97d37cb31/.github/workflows/resume-tag-publication.yml#L131)的 skip-duplicate 未核公开同版本包 source/payload；[L146](https://github.com/IoTSharp/SonnetDB/blob/8bf0e81222784db8b6a014619eaa9eb97d37cb31/.github/workflows/resume-tag-publication.yml#L146)将任意 release view 失败送入创建分支；[L151](https://github.com/IoTSharp/SonnetDB/blob/8bf0e81222784db8b6a014619eaa9eb97d37cb31/.github/workflows/resume-tag-publication.yml#L151)使用 clobber，缺少已有 asset ID/digest 对账。未知 create/upload/push 应先独立读回。
3. **已验证资产白名单**：[L88](https://github.com/IoTSharp/SonnetDB/blob/8bf0e81222784db8b6a014619eaa9eb97d37cb31/.github/workflows/resume-tag-publication.yml#L88)起下载两个 run 的全部 artifacts，[L107](https://github.com/IoTSharp/SonnetDB/blob/8bf0e81222784db8b6a014619eaa9eb97d37cb31/.github/workflows/resume-tag-publication.yml#L107)起只验证已知库存目录；[L149](https://github.com/IoTSharp/SonnetDB/blob/8bf0e81222784db8b6a014619eaa9eb97d37cb31/.github/workflows/resume-tag-publication.yml#L149)却收集全下载树匹配扩展名的文件。上传集合应只来源于已验证 artifact ID、attempt、files/digest 清单。
4. **current/frozen 政策边界**：[L87](https://github.com/IoTSharp/SonnetDB/blob/8bf0e81222784db8b6a014619eaa9eb97d37cb31/.github/workflows/resume-tag-publication.yml#L87)实际执行 frozen verifier，不能把它称为执行恢复时 main 上后来更新的政策。需明确二者职责、库存/版本兼容及发生漂移时的拒绝条件。

作者在 #230 报告 13 项 source-policy unit tests、actionlint、完整 restore 和规定 Format 通过。13 项策略单测主要验证来源对象与 job 清单，不能证明完整恢复 workflow 或外部副作用闭环；本轮没有重跑这些命令。真实 Actions 恢复、受控失败恢复、NuGet/Release 独立公开读回仍为 **NOT_RUN**。

## Issue 去重、创建及独立回读

先通过 GitHub 查询完整开放 Issue 清单（十二项，hasNextPage=false）及中文“恢复”、英文 recovery/publication 标题，并搜索唯一跟踪标识。最新 #392–#401 已存在；#399 属固定硬件、#400 属 MQ 恢复，与本恢复入口不重复。相关 release recovery Issue 和唯一标识均未存在。

使用准确 UTF-8 body-file 单次执行 gh issue create，创建结果为 [#402](https://github.com/IoTSharp/SonnetDB/issues/402)，label 为 enhancement；创建时间为 2026-11-03T20:01:17Z。随后对该 Issue 执行独立 REST GET，确认 OPEN、标题、完整正文与冻结稿一致、真实换行完整、跟踪标识仅出现一次。再独立读取开放 Issue/PR 列表（十五项，未达到分页上限），唯一匹配仍仅为 #402。

唯一标识：`sonnetdb-publication-recovery-8bf0e812-20261104`。

Issue 完成条件包括：写前重新核 tag；NuGet 同版本 nuspec/source commit/实际 DLL 和有效 payload 对账（允许 NuGet 签名改变整包字节）；Release 无盲 clobber；只上传已验证 ID/attempt/files/digest；明确 current/frozen；禁止直接晋级 preflight dispatch 制品；受控预发布失败 tag 的实际 Actions 恢复及独立公开读回/全新缓存消费者。#230 仍保持其完整公开交付关闭条件。

## 本轮验证与执行边界

- PASS：去重查询、唯一 enhancement Issue 创建、独立 GET 完整正文/OPEN/label/marker 回读及仓库内唯一标识核对。
- PASS：本审计及 body-file 的 UTF-8/LF/真实换行与限定空白检查。
- NOT_RUN：任何主树 build/Format、恢复脚本执行、workflow dispatch、tag 变动或包/Release 发布；本轮只创建上述已获授权的 Issue。
- 直连认证检查失败后按机器规则用本地代理复核，现有 keyring 登录正常；没有要求或进行凭据更换。
- 所有外部命令使用 PS7 与 Cordonix 结构化 argv，均有 30/45 秒上限；创建只发出一次，结果由独立 GET 确认。Cordonix 对创建/读回均返回 cleanup_complete=true、active_processes_after_cleanup=0。
- 本任务 body、JSON 请求和审计回执保留于 artifacts/release-5.0-20261104-publication-recovery，属于交付证据；没有新增服务、长期进程、下载制品或外部写入重试。
