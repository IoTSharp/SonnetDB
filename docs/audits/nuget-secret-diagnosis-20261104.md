# 5.0 发布凭据：重跑与新运行的独立诊断

日期：2026-11-04（Asia/Shanghai）。正式源码保持 `84433f9edcceb84605520ebecc4b2879e3c4aa79`；`v5.0.0` annotated object 保持 `c8f41918cedf624d486b0bd0e62e2d7bdd5b5cc9`。本次修改只增加手动凭据诊断入口，不重编、移动标签或采用未完成的 #402 恢复分支。

## 已确认的事实

- 用户确认已删除仓库级 `NUGET_API_KEY`，组织级 Key 未过期，并要求先检查重跑取值。
- 仓库 Secrets 接口确认同名覆盖值不存在。仓库侧 `GET /repos/IoTSharp/SonnetDB/actions/organization-secrets` 可读取当前可见组织 Secret 的 metadata，无需 `admin:org`；该值最后更新于 `2026-11-02T03:22:41Z`（北京时间 11 月 2 日 11:22）。没有读取 Secret 明文。
- 正式 Publish [41721062306 attempt 2](https://github.com/IoTSharp/SonnetDB/actions/runs/41721062306/attempts/2) 于北京时间 11 月 4 日 14:30 实际再次返回首包 `403 API key has expired`；该作业有非空的掩码凭据。成功的打包与两平台合同沿用，GitHub Release 跳过。
- 运行前十个准确公共 NuGet 5.0.0 地址均为 404，GitHub Release 为 404。Docker Hub 匿名回读的 digest/source 匹配正式 5.0 镜像；GHCR 匿名 401 单独保留。

[GitHub Secrets reference](https://docs.github.com/en/actions/reference/security/secrets) 只明确组织/仓库 Secrets 在 workflow run 排队时读取；[重跑文档](https://docs.github.com/en/actions/how-tos/manage-workflow-runs/re-run-workflows-and-jobs) 只保证原触发者权限及原 SHA/ref。Runner 从服务端 job message 生成 Secrets context，公开代码不能证明服务端重跑如何选择值。因此不将本次 expired 错误直接解释为“当前组织 Key 过期”，也不宣称已证实重跑必然使用旧快照。

## 最小验证

新增 `Check current NuGet credential`，仅允许本仓库 main 的手动 `workflow_dispatch`。新排队运行读取当前配置，对已发布的 `SonnetDB 4.0.0` 调用 NuGet 官方 `GET /api/v2/verifykey/SonnetDB/4.0.0`，只保存运行身份、凭据是否非空、HTTP 状态和固定错误分类。请求不跟随重定向，最多一次、30 秒；响应读取有次数和字节上限，作业三分钟上限。日志与 artifact 不包含 Key、长度、hash、原始响应、请求头或异常文本。

官方 NuGetGallery 的 `VerifyPackageKeyAsync` 对普通发布 Key 检查已有包的认证与 scope/owner 权限，不上传或取消列出包，但会记录审计/telemetry。临时 `PackageVerificationApiKey` 会在使用时被消费，不得将它作为组织发布密钥。认证 handler 还支持基于创建时间的 API Key reduction policy；本轮没有证据确认线上是否启用，因此页面所示 Expires 不能单独替代服务端验证。探针将过期正文/Reason 或 Warning 归为固定失败分类，不输出原文。HTTP 200 且无过期分类仅证明该 Key 对已有包有效，不能替代新包创建权限或十包实际发布结果。官方源码固定为 [NuGetGallery 5969489b](https://github.com/NuGet/NuGetGallery/tree/5969489b9f4928c70dbbb645768c47a525133f9b)，入口为 Routes.cs 的 VerifyApiKey 路由、ApiController.VerifyPackageKeyAsync 和 ApiKeyAuthenticationHandler。

本地先运行 PowerShell parser 与 `-Plan`，随后在最终待提交树执行完整 restore 和规定 Format Check。诊断的实际 Actions 结果待新工作流入 main 后取得；当前仍为 **NOT_RUN**。即使新运行验钥通过，也只能确认新请求有效，不能仅据此推断 GitHub 内部 Secrets 快照机制。正式发行与全新缓存消费者继续由 [#230](https://github.com/IoTSharp/SonnetDB/issues/230) 跟踪。

## 本轮上游状态更新

独立复核发现 XCode [#103](https://github.com/NewLifeX/NewLife.XCode/pull/103) 已关闭且未合并；维护者要求避免为特定数据库大量修改基础类，优先通过现有 virtual 扩展，必要架构变更单独提 PR。公开 `XCode.SonnetDB` 仍依赖 SonnetDB 4.0.0。Chloe #435、SqlSugar #1457、ABP #2 仍为 Draft；FreeSql #2291 仍开放。ABP 最新失败在 restore 找不到公开 EF 5.0.0，未进入业务测试。各正式公开 5.0 消费者均为 NOT_RUN，准备脚本的离线检查不提升为业务 PASS。
