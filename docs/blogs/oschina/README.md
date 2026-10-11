# SonnetDB 开源中国发布队列

本目录独立记录 OSChina 的 001–142 篇现有文章，不与博客园进度混用。

- [PROGRESS.md](PROGRESS.md)：查看逐篇博客链接、动弹状态和新闻审核进展。
- [publishing-state.json](publishing-state.json)：权威队列，保存原稿与渠道副本 SHA256、事实复核清单、实际 ID、链接和动弹关联。
- [publishing-events.jsonl](publishing-events.jsonl)：只追加的请求回执与只读对账记录，保留历史失败结果。
- [prepared](prepared)：此渠道复核后的正文与冻结清单，保留原编号和标题；原稿不修改。
- [RESULT-20261007.md](RESULT-20261007.md)：首次补发结果、停止原因和下一次接续位置。

先读取队列，再按编号升序接续。已返回 ID 的博客或动弹保留原 ID；`publishing`、`unknown`、`needs-reconciliation` 必须先只读核对，不能重新创建。`submitted` 表示接口接收及正文验证，不等于已审核公开。动弹 `submitted-verification-pending` 表示 POST 已返回 ID，但详情仍提示审核中或审核失败，不再创建。

每天北京时间 11:00 由本会话的 `sonnetdb-2` 自动任务续发最多两篇。2026-10-07 的首次即时补发另按用户授权尽量执行。博客遇到平台限制停止当轮；动弹错误独立保存，不中断后续博客。动弹采用不含 URL 的纯文字公告，真实博客链接保存在本地关联中。

通用发布脚本属于全局 `publish-oschina` 技能，位于 `C:\Users\mysti\.codex\skills\publish-oschina\scripts`；本目录只保存 SonnetDB 的稿件、事实复核、发布账本与进度。使用技能前读取其 `SKILL.md` 及 `references/queue-publishing.md`。所有操作使用 PowerShell 7、技能 HTTPS 发布器和 Windows 加密凭据，显式指定项目与队列目录，串行执行，不与另一发布进程同时写账本：

```powershell
# 仅导入已经冻结且源稿/副本哈希一致的复核清单，同时刷新进度表。
$tools = 'C:\Users\mysti\.codex\skills\publish-oschina\scripts'
$queue = @{ RepositoryRoot = 'D:\source\SonnetDB'; QueueDirectory = 'D:\source\SonnetDB\docs\blogs\oschina' }
& "$tools\Sync-PublishingState.ps1" @queue -Manifests @('docs/blogs/oschina/prepared/review-031-060.json')
# 离线预检候选；实际发布须按权威队列、当天额度与既有草稿 ID 接续。
& "$tools\Publish-QueuedBlogs.ps1" @queue -ArticleIds @('030','031') -DryRun -WallSeconds 600
# 补明确尚未发送的公告；有 ID 或未知结果的条目只保留，不再发。
& "$tools\Publish-PendingTweets.ps1" @queue -ArticleIds @('030','031') -DryRun -WallSeconds 300
# 只刷新展示与既有回执事件，不发送网络请求。
& "$tools\Sync-PublishingState.ps1" @queue
# 只读核对已返回 ID 的公告，或明确频率拒绝后的原草稿；不重新创建。
& "$tools\Reconcile-QueuedReceipts.ps1" @queue -TweetArticleIds @('029') -WallSeconds 120
# 离线验证源稿/副本哈希、队列与全局回执一致性及脚本语法。
& "$tools\Verify-PublishingState.ps1" @queue
```

以上编号是用法示例，执行前必须以权威队列中的实际状态为准；`Publish-PendingTweets` 要求对应博客已有成功回执。正式发布按每日额度移除 `-DryRun`，每篇后独立发无链接动弹。每批最多十篇，有墙钟及发布器请求预算；每个写请求不重试。运行日志、进程审计和账号快照留在本地并由 `.gitignore` 排除；新工具的运行文件写入 `.local`。未实跑的 SQL、构建、数据库恢复和性能示例仅作教程说明。

SonnetDB 新闻的项目仓库链接固定为 [Gitee 仓库](https://gitee.com/IoTSharp/SonnetDB)。技能和发布器在 dry-run 及联网前校验：正文必须包含这个规范链接，正文或 `--origin-url` 中的 GitHub 仓库首页/`.git` 地址会被拒绝。正式 Release、附件下载和标签发行说明使用各自已核验的真实来源，并单独标注用途。

4.0.0 新闻已补投，ID `502847`。2026-10-08 核得账号状态 `1`，公开 API 标题与全文可读取，新闻网页公开可见性尚未确认；保留原投稿正文、原文链接、指纹和回执，不重复投稿。原 `news.preparedPath` 是历史实际投稿稿件，采用 Gitee 仓库地址的本地修订稿由 `futurePreparedPath` 与独立 SHA 登记，仅供后续内容准备参考。未来发布策略存于 `news.futurePublicationPolicy`；修改仓库偏好不改变原投稿事实。
