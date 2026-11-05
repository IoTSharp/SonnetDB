# 发行恢复策略库（#402，尚未接入发布）

本库位于 `eng/tag-publication-policy.ps1` 与 `eng/tag-publication-reconciliation.ps1`。它提供来源合同、本地字节对账、状态分类及写前fence；没有新增恢复workflow，既有 `recover-tagged-release.ps1 -Resume` 不会自动调用它。实际恢复和公开回读仍未运行，#402保持开放。证据见[2026-11-05审计](../audits/issue-402-publication-recovery-policy-20261105.md)。

- `Assert-TaggedPublicationSource`：原tag-push的repo/tag/SHA/workflow及唯一成功平台合同；本函数不把workflow_dispatch当成发布来源。
- `Get-TaggedNuGetPayloadIdentity`、`Compare-TaggedNuGetPublication`：核对唯一nuspec的id/version/commit及全部有效文件，排除根`.signature.p7s`后比较内容，保留不同整包hash。不提供签名证书信任或发布权限验证。
- `Get-TaggedPublicationReadState`：区分FOUND、权威NOT_FOUND、未确认404、AUTHORIZATION_ERROR、TRANSPORT_ERROR、RATE_LIMITED、REMOTE_ERROR及UNKNOWN_WRITE_PENDING。未知写入之后的404不允许重新提交。
- `Read-TaggedPublicationManifest`、`Assert-TaggedPublicationArtifactSet`：先核对独立批准的manifest字节hash，再校验精确run/attempt/artifact ID/ZIP digest及全部文件；返回的uploads仅是显式assetName记录，`externalWriteAuthorized=false`。不解压、不按后缀搜集、不上传。
- `Compare-TaggedReleaseAssets`：固定Release id/tag和asset id/name/size/digest，同内容跳过、冲突停止。缺digest或未知上传状态先读回，不覆盖。
- `Assert-TaggedPublicationPolicyBoundary`：current-main政策与frozen-tag合同分别有确切来源，均满足当前十包合同；额外provenance字段需要真实适配器生成，不是现有readiness已执行的证明。
- `Invoke-TaggedPublicationWriteFence`：内容复核后重新调用fresh tag resolver，再执行明确提供的callback；返回成功仍需读回，callback异常只记录UNKNOWN且禁止重试。本轮callback全部是本地mock。

调用离线测试：`pwsh -NoProfile -NonInteractive -File eng/test-tag-publication-policy.ps1` 和 `pwsh -NoProfile -NonInteractive -File eng/test-tag-publication-reconciliation.ps1`。测试有90秒总预算、固定case/文件/字节上限、取消和标记目录finally清理；外层仍使用有界进程执行器。

未来接线必须由可信原始来源固定批准manifest。GitHub artifact列表没有独立可信的attempt字段，不能只用时间猜测或把制品自声明当证明。正常5.0.0已公开交付；禁止为验证本库重打正式tag、重推既有包或恢复旧候选。
