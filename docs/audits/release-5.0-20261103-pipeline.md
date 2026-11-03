# SonnetDB 5.0 发布链路复核（2026-11-03）

本轮只读核查 GitHub、NuGet.org、Docker Registry 及仓库发布脚本，没有重跑工作流、更新凭据、修改标签、推包或发布 Release。结论为：**现有 `v5.0.0` 的构建合同成立，但正式交付未完成；恢复前须处理已知 Testcontainers 包名前缀权限问题，并确认实际有效的 NuGet 发布凭据。** 此结论不替代其它智能体的 ORM 业务兼容核查。

原始请求、进程收据、API JSON、失败日志和镜像配置保留于 `artifacts/release-5.0-20261103-pipeline/`。网络经每请求代理 `http://127.0.0.1:7890`，未读取或输出任何凭据值。

## 现存标签与工作流

GitHub ref `refs/tags/v5.0.0` 指向 annotated-tag 对象 `a3d3e7baf378054254b86942d7e854026cbc6050`，剥离后提交为 `455bf3b53b1e56df0064d8f574b0595cbb0b609e`。本轮没有改变它。

| 检查对象 | 本轮回读结果 |
| --- | --- |
| 正式 [Publish 41348714535](https://github.com/IoTSharp/SonnetDB/actions/runs/41348714535) | attempt **4**，`completed/failure`，最后更新 `2026-11-02T03:38:09Z` |
| NuGet Packages 构建及纯包消费者 | 既有 job `success`；本轮回读 job/步骤状态，没有重跑消费者 |
| Linux/Windows Release Bundles | 两个既有 job 均 `success`，包括实际 NativeAOT Server、INSERT RETURNING、发布兼容合同与 Windows Studio 资产检查 |
| Publish verified NuGet packages | attempt 4 在首个 `SonnetDB.5.0.0.nupkg` 推送失败 |
| GitHub Release | 前置失败导致 `skipped`；`releases/tags/v5.0.0` 本轮 API 为404 |
| [Connectors 41348714371](https://github.com/IoTSharp/SonnetDB/actions/runs/41348714371) | win-x64、linux-x64、win-x86 构建及库存检查均成功；Release 上传等待正式 Publish 后失败 |
| [Docker 41348714479](https://github.com/IoTSharp/SonnetDB/actions/runs/41348714479) | 同 tag、同 SHA，`completed/success` |

最新失败日志 `publish-attempt4-failed.log` 明确包含 `2026-11-02T03:38:07Z` 的 **HTTP403：The specified API key has expired**。这不是仅转述首轮失败。仓库 `NUGET_API_KEY` 的只读元数据在本轮最后一次查询仍为 `updated_at=2026-11-02T03:35:45Z`，没有验证密钥值或其权限；用户在当前会话表示已更新有效密钥，应在实际恢复时结合最新状态判定，不能把旧 attempt 的拒绝写成新密钥已经失败。

同提交、精确 5.0.0 的十二类既有候选工作流均可在本轮 API 列表中回读为成功：CI `41345345369`、CodeQL `41345348672`、Docs `41345353023`、Management `41345357094`、Parity `41344841180`、Document Soak `41345360457`、Ecosystem Soak `41345363858`、M19 `41345367093`、M39 `41345370477`、Publish 预检 `41346120155`、Connectors 预检 `41346123540`、Docker 预检 `41345377669`。本轮没有重新下载 raw Parity 或复跑这些测试；完整旧门禁合同仍见 `issue-230-release-20261102.json`，不能据此把新提交标为 READY。

## 公开 NuGet 与镜像

十个预期 NuGet ID 的公开 flat-container 索引已逐一读取，均没有 `5.0.0`：

| ID | 索引 HTTP | 5.0.0 |
| --- | --- | --- |
| SonnetDB | 200 | 未发现 |
| SonnetDB.Core | 200 | 未发现 |
| SonnetDB.Protocol | 404 | 未发现 |
| SonnetDB.Client | 404 | 未发现 |
| SonnetDB.Cli | 200 | 未发现 |
| SonnetDB.EntityFrameworkCore | 200 | 未发现 |
| SonnetDB.CAP | 404 | 未发现 |
| SonnetDB.Caching.Distributed | 200 | 未发现 |
| SonnetDB.Caching.EasyCaching | 200 | 未发现 |
| Testcontainers.SonnetDB | 404 | 未发现 |

Docker Hub `iotsharp/sonnetdb:5.0.0` 的匿名 manifest 和配置均读取成功：

- Manifest digest：`sha256:6c5974f4ba4e81fd12a82cb08a6265c959d3c63f589f325df7d02ff9a1d81597`。
- 平台为 **linux/amd64**，与现行工作流单平台合同一致；没有 ARM64 发布结论。
- OCI `org.opencontainers.image.version=5.0.0`，`org.opencontainers.image.revision=455bf3b53b1e56df0064d8f574b0595cbb0b609e`，来源为本仓库。
- 这证明已有部分正式镜像分发，不能把5.0视作完全尚未发布。若按本轮用户授权更换 tag/提交，必须同步核对替换后的镜像 digest、版本及源码标签，保留旧 digest 证据。

GHCR 的匿名 manifest 和匿名 token 请求均401；当前 GitHub CLI 身份读取 package metadata 又因缺少 `read:packages` scope 返回403。因此 GHCR 的匿名可拉取状态为 **UNKNOWN**，不能用 Actions push 成功提升为公开可安装。Docker Hub 的公开读取结论独立成立。

## 十包发布中的包名阻断

已读取原始历史收据 `artifacts/online-closure-20261012/ef266pub5-final-outcome.json`：同一发布流程曾成功公开六包，但 `Testcontainers.SonnetDB` 被 **HTTP409，NuGet reserved namespace; prefix-owner authorization required** 拒绝。当时未重试或改名。

本轮实际项目 `src/Testcontainers.SonnetDB/Testcontainers.SonnetDB.csproj` 仍使用 `<PackageId>Testcontainers.SonnetDB</PackageId>`，十包库存也仍要求此ID。公开索引404不能证明前缀权限已获得；新 NuGet key 的有效性也不能证明该保留前缀授权成立。本轮未找到前缀授权已经解决的证据，没有执行探测性推包。

用户在本轮已明确同意改用自有前缀包ID **`SonnetDB.Testcontainers`**，主智能体在独立发布 worktree 准备补丁。此方案保持现有 CLR assembly/namespace 与项目路径，仅改变 NuGet PackageId 及相应包清单、安装说明；必须重新打包并重新取得同提交、精确版本的门禁。不能静默丢弃此包、降低库存要求，或把本地同版本包当作已公开。

新包ID已定位必须审查的路径如下，**本智能体没有修改这些源码；变更归主智能体所有**：

| 路径 | 需要审查的内容 |
| --- | --- |
| `src/Testcontainers.SonnetDB/Testcontainers.SonnetDB.csproj` | `PackageId`、面向用户的 `Title`；无需自动修改 `AssemblyName`/`RootNamespace` |
| `src/Testcontainers.SonnetDB/PackageReadme.md` | 标题、包名、`dotnet add package`；保留已有 `using Testcontainers.SonnetDB` 命名空间 |
| `eng/verify-release-artifacts.ps1` | 十包ID库存 |
| `eng/test-release-artifacts.ps1` | 库存合同夹具 |
| `eng/verify-nuget-candidate-promotion.ps1` | 候选晋级ID清单 |
| `eng/test-nuget-candidate-promotion.ps1` | 晋级合同夹具 |
| `eng/recover-tagged-release.ps1` | 标签恢复的公开包ID清单 |
| `eng/release.ps1` | SDK bundle README 内的 `.nupkg` 文件名；既有项目打包路径可保留 |
| `docs/releases/README.md`、`docs/releases/sdk-bundle.md` | 新包ID及包文件名 |

`SonnetDB.slnx` 的项目路径、四个源文件 namespace 以及历史 `docs/releases/3.0.0.md` 不应通过全局替换无条件改变。上述是本轮明确目录搜索的结果，落地时还需核对 README/示例/中央包引用和候选清单，保留历史审计收据。

适用现有验证包括 `eng/test-release-artifacts.ps1`、`eng/test-nuget-candidate-promotion.ps1`、十包严格打包与 `eng/verify-package-consumers.ps1`；任何提交还须执行根 AGENTS 规定的完整 restore 与 Format Check。公开新ID后需全新缓存 restore 消费，不能只验本地 nuspec。

## 可复用的正式构建资产

下列均来自 **正式 run 41348714535**，仍未过期，绑定旧提交 `455bf3b5...`。它们属于 Actions 产物，不属于已经公开的 Release；与预检 run `41346120155` 的同名产物不要混为相同字节。

| Artifact | ID | ZIP字节数 | GitHub artifact digest |
| --- | --- | --- | --- |
| `nuget-packages-5.0.0` | 13099769159 | 19918013 | `sha256:4065f79d194e068d36c90664812a916254dbe4133dfb17a1bd4569a09d6f568c` |
| `release-5.0.0-win-x64` | 13101550413 | 792045414 | `sha256:eaf02630f6a0dda30321ff1d3aecf7e3386a53bedbb2f5c6bf6278d1faf3429f` |
| `release-5.0.0-linux-x64` | 13100945728 | 556185175 | `sha256:e97c43753e50df6fe011ecfbb79ea8a7fbdbc279353f86590827d58330eacb7c` |

另有 `release-readiness`、`package-consumers-5.0.0` 和两平台 `insert-returning-contract-5.0.0-<rid>`。完整API库存见 `publish-artifacts.json`；预检库存另见 `preflight-artifacts.json`。主智能体统一安排下载到 `artifacts/release-5.0-20261103-shared/`：主智能体下载十包，本智能体按后续明确分工单次下载 Windows artifact，没有重复下载。

Windows artifact下载实际 exit0，初次后处理误认为文件平铺，因真实路径保留 `bundles/`、`installers/` 而退出1；该失败保留，随后仅修正提取路径，没有重复下载。最终于 `2026-11-03T11:04:00Z` 校验、解包通过：

- 原包 `artifacts/release-5.0-20261103-shared/windows-artifact/bundles/win-x64/sonnetdb-full-5.0.0-win-x64.zip`，SHA256 `56f0dc5ad3970e33165660e91f2c4b54b5a8eca198d0cf02b209f02b29471dac`，与原 `.sha256` 一致，原包保留。
- 解包174项到 `artifacts/release-5.0-20261103-shared/server/`。
- 实际 Server：`D:\source\SonnetDB\artifacts\release-5.0-20261103-shared\server\SonnetDB.exe`，SHA256 `b3a2991a7bbc4750c2d75f711e3151d5d7059f04b3bad853308e1481a8f22ca4`。
- 收据 `artifacts/release-5.0-20261103-pipeline/server-download-verification.json`。已向两个兼容核查智能体提供同一路径，供各自独占端口、数据目录及生命周期验证。本智能体未启动 Server，也未把此次下载视为生产安装验收。

## 恢复顺序与新闻说明缺口

用户已经选择新包ID，**不能仅重跑原 `41348714535` 正式发布**：原run仍绑定旧SHA和旧保留前缀包名。正确顺序为主智能体完成新ID、库存、消费者和5.0新闻稿，冻结新提交并取得该提交的完整门禁，再依据本轮用户的标签替换授权执行正式触发。发布后独立读回十个新ID/版本和Release；连接器须等待该次新提交的正式Publish成功，再完成资产上传。旧正式run的产物仅用于本轮旧候选业务复核与历史对照。

`publish.yml` 的 `workflow_dispatch` **只做预检，不推NuGet/Release**，不能误用为正式恢复。旧SHA的成功不自动覆盖新SHA；若新的正式run出现可重试失败，须先核对已发生的分发，再只恢复该新run的失败jobs。

现行正式 Publish 根据 `docs/releases/${VERSION}.md` 读取说明。核查时 `docs/releases/5.0.0.md` 不存在，会回退发布物总览 README；必须补充面向5.0的发行新闻和明确迁移边界。若恢复旧tag，该文件不能凭空进入旧源码，主智能体应使用已审定新闻更新最终 Release 正文；若重固定新tag，应把正确文件纳入新提交。

发布后仍需独立确认：十包版本和 nuspec 源码、仓库签名后包的 payload/程序集身份、匹配 Server 的消费者复验、完整 GitHub Release 资产及校验文件、连接器上传、Docker 镜像源码/digest和适用可见性。NuGet 附加签名后整包 SHA 可不同于原候选，需分别保留两种哈希，不能用整包不相等误判源码变化。

## 新包补丁与新闻稿的独立只读复查

主智能体提供独立 worktree `C:\Users\mysti\.codex\worktrees\sonnetdb-5-release-20261103\SonnetDB` 后，本智能体复查了当前变更，没有发现上述源码范围中的发布阻断：

- 新纯包消费者使用 `SonnetDB.Testcontainers` 精确版本 PackageReference、独立缓存与既有候选包字节核对；真实编译 `using Testcontainers.SonnetDB`，运行 builder 并断言程序集仍为 `Testcontainers.SonnetDB`。它验证安装和API兼容，不声称已启动 Docker 容器。
- 仅 Testcontainers 消费者设置 `IsAotCompatible=false`，与该扩展原有明确边界一致，Core/Client消费者原AOT合同保持。
- 恢复脚本为4.0保留原七包，4.1加入CAP，5.0移除旧Testcontainers包ID并加入Protocol、Client及新包ID，最终仍为十包。
- 发布总览、SDK说明、生成的SDK README及迁移合同已与新包名同步；历史审计及#266既有公开结果保持，不把新候选补丁追写成旧包已公开。
- 5.0 新闻稿明确主版本兼容、外部 Provider 各自的合入/发包状态、AOT和模型边界，没有把候选验证写成生产验收。已给主智能体提出：正式 Publish 原样复制Markdown到Release，迁移与快速开始等链接宜使用绝对源码/文档URL，避免依赖Release页面的相对路径解析。

上述为变更审查，不替代主智能体在最终待提交树上的实际restore、Format、新十包打包、消费者执行和全部新SHA工作流门禁。

## 资源与边界

本智能体只新增本报告、独占 ignored 证据目录及明确分工的共享 Windows下载/解包产物，保留共享工作树现有修改。核查、下载和提取命令均由独立 native Cordonix `execute_program` 包裹，均有进程超时/最大请求数、PID/父PID及收据；已结束，Cordonix均报告 `cleanup_complete=true`、剩余子进程0。没有创建容器、持久服务、浏览器或后台轮询。证据脚本、JSON和按要求保留的原始产物是交付物，未作为临时文件删除。最终根HANDOFF及Git提交由主智能体统一处理，本智能体没有修改其它智能体的共享文件。
