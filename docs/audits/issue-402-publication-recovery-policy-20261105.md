# Issue #402：发行恢复策略与对账基础

日期：2026-11-05。基线 `a7125ffb15c3d2182ff811036173316c75329ba9`；本报告记录子任务本地未提交工作树的证据。旧候选 `8bf0e81222784db8b6a014619eaa9eb97d37cb31` 已由主任务保存在验证过的 Git bundle 中，本次只复用其来源策略及13项测试，不引入旧发布workflow。

## 已完成的源码切片

新增 `eng/tag-publication-policy.ps1` 和 `eng/tag-publication-reconciliation.ps1`，并在 CI 的现有 release gate contracts 中调用两组离线测试。该库只读取传入证据和本地档案，返回显式决策；没有内置网络请求、凭据读取、上传、push、tag变更或工作流dispatch。写前fence的Action只在本轮测试中接收本地mock callback。

| 原问题 | 本轮源码行为 | 验证边界 |
|---|---|---|
| 写入前tag可能已变 | 完成昂贵内容复核后，每个操作重新调用远端tag解析callback，核对repo/tag/完整SHA，失败或变化则不调用Action | mock验证前次检查后移动、读失败、歧义、内容失败、取消和超时；没有真实发布接线 |
| NuGet同version可能是别的制品 | 读唯一nuspec核对id/version/commit；对全部文件按路径、长度、SHA256生成有效payload摘要，仅排除根`.signature.p7s`，不要求signed/unsigned整包SHA相同 | 真实ZIP/nupkg fixtures及一个既有公开Protocol包对账；不验证签名证书信任 |
| 404/auth/network/未知混淆 | HTTP状态与决策分开，401/403、传输、429、5xx和非权威404均阻断；不确定写入后的404仍UNKNOWN_WRITE_PENDING | 本地状态合同；未执行远端create/upload/push |
| 已有Release资产被覆盖 | 固定Release id/tag，按asset id/name/size/digest对账，同内容返回可跳过，冲突/缺digest/未完成上传明确拒绝；未知写入不重试 | 本地资产fixtures；没有clobber实现 |
| 上传集合扩大到额外文件 | 批准manifest绑定run/attempt/workflow/artifact id/name/archive digest及完整文件集合；下载对象须完全对应，逐文件核对长度/hash，仅assetName明确列出的记录进入返回集合 | 真实本地ZIP与额外artifact/ZIP/文件/attempt漂移负例；不按后缀递归搜集上传物 |
| current与frozen政策混用 | 两份规范化readiness证据分别标明current-main/frozen-tag及policy commit，均绑定冻结source/version；要求当前十包及SonnetDB.Testcontainers，拒绝策略修订/库存漂移 | 规范化输入的纯策略验证，尚未实现实际双verifier适配器 |

**没有新增或启用 `resume-tag-publication.yml`，既有 `eng/recover-tagged-release.ps1` 及正常5.0发布路线未修改。** 现有`-Resume`入口没有因此自动获得这些新校验。本切片是可复用的准入/对账基础，不能称为完整发行恢复已经上线，#402继续OPEN。

## 来源与manifest的实际信任边界

`Read-TaggedPublicationManifest`先核对独立冻结的SHA256，再解析有大小上限的严格UTF-8 JSON。摘要必须来自事先可信的批准/生成记录，不能把下载目录内自己声明的hash当成批准。`Assert-TaggedPublicationArtifactSet`对冻结manifest中的run attempt与最新run/jobs做精确比较，保留原tag-push条件，workflow_dispatch的预检产物无法替代。

GitHub artifact API的`workflow_run`本身没有足够的artifact→run_attempt归属证明。本库不会凭时间戳猜测；实际Actions接线仍须从可信的原始生成/批准记录固定artifact ID、attempt和digest，再供本库校验。旧制品没有合格记录时应保留UNKNOWN并阻断恢复，不能自行补写声明后发布。

新增policySource、policyCommit、policyRevision、packageIds是恢复策略要求的规范化字段，不是现有readiness报告已经具有并已被双重执行的证明。后续适配器必须真正执行恢复时current-main政策与frozen-tag合同，并记录两边身份；本轮没有伪造真实READY。

fence在每个动作前重新核对，但不是GitHub、NuGet和Release之间的分布式原子锁。callback成功只返回`WRITE_RETURNED_REQUIRES_READBACK`；异常返回`UNKNOWN_WRITE_PENDING`且`retryAllowed=false`，不把成功返回冒称公开可见。

## 本地测试

PowerShell 7.6.9、Windows x64。旧13项来源合同和新增69项对账合同均退出0，共 **82/82 PASS**。新增测试使用真实本地ZIP/nupkg字节、200KB压缩entry和本地Action计数，覆盖tag移动、相同id/version但DLL/commit不同、签名字节不同但有效载荷相同、ZIP短读、DTD、重复/大小写冲突路径、traversal、取消、明确墙钟预算、未知写入、额外ZIP、artifact/run attempt漂移及current/frozen政策差异。所有外部写入计数为0；两个Action仅为一次本地成功mock和一次本地异常mock。

档案上限为256MiB、8192entry、展开总量512MiB，逐块hash并检查取消及墙钟；artifact集合至多16个、每个manifest至多4096文件，来源至多2个，Release资产至多256个。DeflateStream允许任意正的短读，因此read操作的严格上限按声明展开字节数加1计算，每次读均推进或EOF退出；不按预期满缓冲次数提前判截断。测试先以1B最小档案试运行，再覆盖真实较大entry。临时目录为本次GUID标记目录，在finally核对绝对路径与标记后删除。

失败证据保留于本机`artifacts/issue402/`：旧来源测试首次原生退出`0xC0000005`且无输出，原因未判定；一次有界NonInteractive重跑通过，不声称已修复宿主崩溃。另保留PowerShell大小写不敏感hash literal的解析失败、1B fixture暴露的数组解包问题，以及真实公开ZIP暴露的短读误判；修正后取得上述82项通过。

## 既有公开5.0.0的只读对账

仅匿名下载既有两个官方URL，不创建或更新任何资源：

- `https://github.com/IoTSharp/SonnetDB/releases/download/v5.0.0/SonnetDB.Protocol.5.0.0.nupkg`
- `https://api.nuget.org/v3-flatcontainer/sonnetdb.protocol/5.0.0/sonnetdb.protocol.5.0.0.nupkg`

GitHub直连10秒超时后按机器规则使用本地代理，一次重试成功；NuGet直连成功。每个请求最多30秒/16MiB，没有发布重试。独立`git ls-remote`回读tag object `c8f41918cedf624d486b0bd0e62e2d7bdd5b5cc9`、peeled commit `84433f9edcceb84605520ebecc4b2879e3c4aa79`；两个nuspec commit均一致。

| 比较项 | GitHub Release原包 | NuGet公开包 |
|---|---|---|
| 整包SHA256 | `35bd64b129b24e349f08d3450382ed5398650a7aa05eed54c091c0c081f1b5df` | `f89e333a4ee80087f07574c92e85a863dfe04f13850ca6e039f693f769f800d2` |
| 排除根签名后的有效文件数 | 7 | 7 |
| 有效payload摘要 | `a41efb7106e4bc19338c1e4f2dd6e1a837b9cd39eb7d4dedcf5f432da2d92a23` | 同左 |
| 决策 | `ALREADY_PRESENT_SAME_PAYLOAD` | 相同identity/source/payload |

此结果只证明新对账函数能处理一个既有真实signed/unsigned包组合。它不是十包重新验收、签名信任验证，也不是一次恢复后的独立公开读回；正式5.0.0的既有交付不重做。

## 未完成与下一步

受控预发布失败tag的真实Actions恢复、artifact/attempt可信来源适配、双政策verifier接线、真实部分写入失败后的读回、NuGet/Release未知结果恢复、签名验证、完整十包与全新缓存消费者均为 **NOT_RUN**。主集成尚须在最终树完成规定restore/Format，远程CI及Linux pwsh结果另验。本轮未commit、push、dispatch、创建tag、上传、发布、改secret或操作issue。

继续顺序：审阅并合入仅源码基础 → 从可信原始tag-push记录冻结manifest/attempt证明 → 实现并测试有界只读适配及逐动作写前fence → 另获明确发布授权后用受控预发布tag验证恢复 → 独立公开读回和消费者 → 再评估关闭#402。不得将本轮82项PASS或旧5.0.0公开包对账直接用于关闭本单。
