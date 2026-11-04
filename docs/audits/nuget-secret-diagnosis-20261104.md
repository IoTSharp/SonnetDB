# 5.0 发布凭据：重跑与新运行的独立诊断

日期：2026-11-04（Asia/Shanghai）。凭据问题已处理，正式 Publish 第三次尝试成功，十包公开下载及签名验收通过。正式源码保持 `84433f9edcceb84605520ebecc4b2879e3c4aa79`；`v5.0.0` annotated object 保持 `c8f41918cedf624d486b0bd0e62e2d7bdd5b5cc9`。诊断代码提交为 `8477fcc3882096917ddbd37f9f793f5625c24fa8`，未重新编译或移动正式版本。

## 最终原因和处理

用户授权代为在 NuGet 创建凭据后，已登录官方 API Keys 页面独立核实：页面公告“有效期超过 30 天的旧 Key 将在 2026-11-01 失效；2026-08-17 起新 Key 最长 30 天”，原 `sonnetdb` Key 明确为 Expired。这个现场结果补齐了此前仅依据认证 handler 无法确认线上有效期策略的边界。

已单次创建 `SonnetDB-5-release-20261104`，owner 为 MysticBoy，范围 `SonnetDB*`，允许推送新包和新版本，不授权 unlist；到期时间为 `2026-12-04T07:34:43.2449037Z`（北京时间 12 月 4 日 15:34）。Key 先写当前用户 Windows 受保护凭据，再仅经 stdin 写入 **SonnetDB 仓库级** `NUGET_API_KEY`，元数据回读 `updated_at=2026-11-04T07:43:06Z`。值未输出、未存明文文件或进入命令行；本机转交显式绕过代理、禁止重定向和重试，完成后清空页面 Value、关闭该浏览器和接收器。

| 实际动作 | 结果及含义 |
| --- | --- |
| 原正式 Publish attempt 2 | 首包 403 expired，保留原始失败，不代表新 Key 的结果。 |
| 全新诊断 [41789891046](https://github.com/IoTSharp/SonnetDB/actions/runs/41789891046) | 当前组织配置仍为非空、403 EXPIRED，排除只有旧运行重跑取值这一解释。 |
| 新 Key 的全新诊断 [41794394242](https://github.com/IoTSharp/SonnetDB/actions/runs/41794394242) | 官方 GET verifykey 返回 400、正文仅 Bad Request；是 INCONCLUSIVE，不当作发布权限失败或通过。 |
| 原正式 Publish [41721062306 attempt 3](https://github.com/IoTSharp/SonnetDB/actions/runs/41721062306/attempts/3) | 原十包实际全部 Created，随后 GitHub Release 成功；这是新 Key 的发布权限实证。 |
| 公开验收 | 十包已匿名下载，nuspec source、DLL 载荷及十个加密签名验证均 PASS；不以推送接受代替公开可下载。 |

因此，GET verifykey 只能帮助区分响应，不能作为普通发布 Key 的完整有效性或发布硬门禁。新 Key 的实际十包推送和独立公共读回才关闭本轮凭据阻断。脚本的 400 原始分类 HTTP_ERROR 与 INCONCLUSIVE 解释分别保留，没有为制造绿色诊断而修改已取得的结果。

## 诊断开始时的事实

- 用户确认已删除仓库级 `NUGET_API_KEY`，组织级 Key 未过期，并要求先检查重跑取值。
- 仓库 Secrets 接口确认同名覆盖值不存在。仓库侧 `GET /repos/IoTSharp/SonnetDB/actions/organization-secrets` 可读取当前可见组织 Secret 的 metadata，无需 `admin:org`；该值最后更新于 `2026-11-02T03:22:41Z`（北京时间 11 月 2 日 11:22）。没有读取 Secret 明文。
- 正式 Publish [41721062306 attempt 2](https://github.com/IoTSharp/SonnetDB/actions/runs/41721062306/attempts/2) 于北京时间 11 月 4 日 14:30 实际再次返回首包 `403 API key has expired`；该作业有非空的掩码凭据。成功的打包与两平台合同沿用，GitHub Release 跳过。
- 运行前十个准确公共 NuGet 5.0.0 地址均为 404，GitHub Release 为 404。Docker Hub 匿名回读的 digest/source 匹配正式 5.0 镜像；GHCR 匿名 401 单独保留。

[GitHub Secrets reference](https://docs.github.com/en/actions/reference/security/secrets) 只明确组织/仓库 Secrets 在 workflow run 排队时读取；[重跑文档](https://docs.github.com/en/actions/how-tos/manage-workflow-runs/re-run-workflows-and-jobs) 只保证原触发者权限及原 SHA/ref。Runner 从服务端 job message 生成 Secrets context，公开代码不能证明服务端重跑如何选择值。因此不将本次 expired 错误直接解释为“当前组织 Key 过期”，也不宣称已证实重跑必然使用旧快照。

## 最小验证

新增 `Check current NuGet credential`，仅允许本仓库 main 的手动 `workflow_dispatch`。新排队运行读取当前配置，对已发布的 `SonnetDB 4.0.0` 调用 NuGet 官方 `GET /api/v2/verifykey/SonnetDB/4.0.0`，只保存运行身份、凭据是否非空、HTTP 状态和固定错误分类。请求不跟随重定向，最多一次、30 秒；响应读取有次数和字节上限，作业三分钟上限。日志与 artifact 不包含 Key、长度、hash、原始响应、请求头或异常文本。

官方 NuGetGallery 的 `VerifyPackageKeyAsync` 对普通发布 Key 检查已有包的认证与 scope/owner 权限，不上传或取消列出包，但会记录审计/telemetry。临时 `PackageVerificationApiKey` 会在使用时被消费，不得将它作为组织发布密钥。认证 handler 还支持基于创建时间的 API Key reduction policy；诊断早期仅由源码无法确认线上策略，随后官方页面公告已补齐旧Key失效的现场依据，页面所示 Expires 仍不能单独替代服务端验证。探针将过期正文/Reason 或 Warning 归为固定失败分类，不输出原文。HTTP 200 且无过期分类仅证明该 Key 对已有包有效，不能替代新包创建权限或十包实际发布结果。官方源码固定为 [NuGetGallery 5969489b](https://github.com/NuGet/NuGetGallery/tree/5969489b9f4928c70dbbb645768c47a525133f9b)，入口为 Routes.cs 的 VerifyApiKey 路由、ApiController.VerifyPackageKeyAsync 和 ApiKeyAuthenticationHandler。

本地 parser、`-Plan`、缺失 Key 和格式错误 Header 的负边界验证通过；提交 `8477fcc3` 前完整 restore、规定 Format Check 与差异检查均退出 0。实际新运行与正式恢复结果见上表。没有据这两个诊断结果推断 GitHub 内部快照实现；发布作业在替换凭据后实际成功。

## 本轮上游状态更新

独立复核发现 XCode [#103](https://github.com/NewLifeX/NewLife.XCode/pull/103) 已关闭且未合并；维护者要求避免为特定数据库大量修改基础类，优先通过现有 virtual 扩展，必要架构变更单独提 PR。已去重登记 [SonnetDB #404](https://github.com/IoTSharp/SonnetDB/issues/404)，不重开被拒绝的方案。公开 `XCode.SonnetDB` 仍依赖 SonnetDB 4.0.0；Chloe #435、SqlSugar #1457、ABP #2 和 FreeSql #2291 的上游分发分别验收。

公开 SonnetDB 5.0 的 XCode50、Chloe 三协议各101与独立重开、SqlSugar三协议各93、FreeSql237与三协议各25业务/8重开已实际通过。它们使用固定 ORM 候选与公开 SonnetDB，不能提升为上游 Provider 全部已公开。ABP与容器专项的最终状态见[发行验收](release-5.0-20261104-final-publication.md)；原 ABP CI 的公开 EF5.0 缺包失败保留为历史。
