# SonnetDB 博客园封面配图规范

用户于 2026-10-10 要求后续博客园文章沿用微信公众号的主题配图方法。本规则与既有标题、事实核查、每天两篇、租约及单次发布请求规则同时生效。

## 复用与生成

1. 先检查对应会话及公众号、微博的现有图片，最多核查本轮 10 个候选。图片必须与正文主题和版本边界一致，有原始文件、来源会话和生成/审核证据。将可用原图复制到 `docs/blogs/assets/covers/`，保存原始 SHA256，不修改另一渠道资产或状态。
2. 没有合适图片时，读取 `C:\Users\mysti\.codex\skills\gpt-image-design\SKILL.md`，按公众号会话 `01a12083-1b1c-77d1-b86f-d791b0c3b773` 的方法用 `gpt-image-2` 生成。参考提示词和收据在 `C:\Users\mysti\AppData\Local\Codex\WechatPublisher\sonnetdb\research\20261009\covers`；`Invoke-WechatCover.ps1` 是该轮固定路径包装器，不能直接用它覆写公众号资产或照搬日期。
3. 每篇独立构图：成年约 25 岁亚洲女性，得体 JK 风格服装，专业讲解姿态；清晰的 SonnetDB 品牌、本文主题及“关注 IoTSharp · 读懂 SonnetDB”等中文提示。结合实际正文安排主题图形，禁止重复套用通用数据库背景。不得画出无证据性能数字、零丢失、生产就绪、客户成果或把 hash/mock 当真实模型效果；涉及 Graph 时明确标 Beta。
4. 使用已核本机凭据桥接或 provider 子进程环境，密钥不得进入命令行、日志、仓库或收据。现存公众号收据使用本机 sonnet_vip 网关；实际端点和凭据来源须当次核验，不打印 `config.toml`。首选已核桥接 `C:\Users\mysti\.codex\scripts\Invoke-SonnetOpenAI.ps1`；不能把旧网关地址当作永久配置。
5. 参考请求为 `1792x768`、quality high、model `gpt-image-2`、retries 0、请求 timeout 120 秒；实际输出尺寸单独读取记录。每轮最多为两篇各生成一次，每次最多 260 秒，遵守任务总 30 分钟及取消/进程身份/finally 清理要求。无兼容账户、超时或生成失败时暂停该稿，不能无图发布或自动换模型。
6. 最终目视核对成年人物、中文、品牌和技术内容。必要时确定性叠字再复核，记录衍生前后哈希。博客园正文展示完整横图，不机械套用公众号方形裁剪。图中示例数据须与正文一致；主题图不是操作截图、真实客户现场或实测结果。

## 正文与状态

封面放在正文 H1 后首个 SonnetDB 叙述段之后、主体章节之前，使用有意义的 alt 和图注。保留标题原意、sourceDocs、版本/提交证据以及默认两张社区二维码；二维码不能替代封面。图注注明“AI 主题示意图，并非实测截图”。

每篇 `coverImage` 至少记录：`path`（仓库相对）、`markdownUrl`（相对文章，上传后可为已验证 HTTPS URL）、`sha256`、`sourceThreadId`、`sourceAsset`、`generationReceipt` 或 `sourceEvidence`（博客目录内可追溯生成/复用证据文件）、`model`、`requestedSize`、`actualSize`、`alt`、`caption`、`reviewedAt`、`reviewStatus=approved`。非 AI 图片的模型/请求尺寸明确填 `not-applicable`，不捏造生成记录。上传后追加 `remoteUrl`、`remoteVerifiedAt`、`remoteSha256`。

已发布稿的本地补图使用 `contentRevision`，保留旧 `contentSha256`、原发布/标题修订证据和成功事件。修订 SHA 保存在 `contentRevision.contentSha256`，状态分别为 `prepared`、`updating`、`updated`、`unknown`、`failed` 或 `needs-update`，不能用改文件推定上线。原文仍为 `published`，不能进入新文章队列。

## 发布门禁

写稿/queued 前及每篇发布前运行：

```powershell
& 'C:\Program Files\PowerShell\7\pwsh.exe' -File docs/blogs/verify-cover-image.ps1 -ArticleId 145
& 'C:\Program Files\PowerShell\7\pwsh.exe' -File docs/blogs/verify-project-identity.ps1 -ArticleId 145
```

实际发布前为配图检查增加 `-RequireUploadReady`。本地图片必须有完整且已核的 MetaWeblog 上传配置；远程图片必须有公开 GET 和原图哈希证据。缺图、无来源/目视审核、哈希不符、正文未引用封面、无上传通道时，稿件标 `needs-update` 并记录 `lastError`，不能 queued/发布。

仍使用 publish-cnblogs 技能指定的 C# 发布器，先 `--dry-run --preview-output <本任务临时预览>`。核对最终正文含主题封面及默认双二维码；dry-run 仅证明离线解析与图片存在，不证明上传可用。正式请求前再校验租约。上传失败停止发文；不得使用 `--no-community-footer` 或删图绕过。成功后公开回读封面 URL 与双二维码，保存图片和正文证据。

2026-10-10 已核上传方式：直连读取失败后，按机器规则选定 `http://127.0.0.1:7890` 代理。在 `https://rpc.cnblogs.com/metaweblog/IoTSharp` 使用当前技能配置中的已有 PAT，在内存里作为认证输入，先只读 `metaWeblog.getPost` 核已确认原文章 ID/标题，再用 `blogger.getUsersBlogs` 核 `https://www.cnblogs.com/IoTSharp` 的账号与真实 blogId；本次返回 blogId `523396`（图片存储目录 `1725297` 不是该 ID）。这证明本机当前凭据可用，不能假定任意 PAT 都适用于 MetaWeblog。每次仍须核验，不新增或复制凭据。

核验通过后可单次调用 `metaWeblog.newMediaObject` 上传 PNG 原图：参数为已核 blogId、已核账号、内存凭据，以及 `name/type/bits` struct，其中 bits 为原图 base64；仅保留响应图片 URL，不记录请求 XML/凭据/base64。保存上传意图后调用，响应未知则暂停核对，禁止重试。GET 返回的公开原图 SHA256 必须匹配本地资产；将正文图片替换为该已核 HTTPS URL、保存原本地资产与新正文 SHA，再运行 `-RequireUploadReady` 和 C# dry-run，发布器可复用远程图片。不得把未核配置或 dry-run 当作上传成功。

本任务租约固定为 `docs/blogs/publishing-lease.json`，创建/使用时取得独占文件句柄；同时核对所有文章的 publishing/lease 和 contentRevision 的 updating/lease。其它运行不得覆盖存在的租约；过期也必须先核请求证据。JSON 状态更新用同目录临时文件原子替换，正常/失败最终只释放已确认属于本 runId 的租约。原发布成功事件和未知状态始终保留。

## 原文补图

用户单独授权补图的已发布稿只按原 postId 编辑，不新建、不自动批量修改历史稿。保存原正文/分类/标题、前后哈希、一次编辑请求响应和独立原 ID/公开回读。调用前原子记录 `contentRevision.status=updating`、runId、lease 和 editAttempts；最多一次实际编辑请求。网络/响应未知则修订为 `unknown` 暂停，禁止重试；明确 HTTP/RPC 失败为 `failed`。

确认原 ID 正文和公开图片后追加 `cover-updated` 事件，不改变原 publishedAt/postId/URL，不增加当天新文章额度。临时预览及凭据文件只在已核本任务路径内创建和清理，JSON/JSONL 为 UTF-8 无 BOM、LF，状态同目录临时文件原子替换。
