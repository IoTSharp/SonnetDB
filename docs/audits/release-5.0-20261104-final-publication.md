# SonnetDB 5.0.0 正式发布与公开验收

核查日期：2026-11-04，Asia/Shanghai。正式发布已完成，状态为 **PUBLISHED_PUBLIC_VERIFIED**：十个 NuGet 包已公开并通过来源、载荷和加密签名验证；GitHub Release 的 52 个资产均已匿名下载并核对 digest；Docker Hub 来源与不可变镜像匹配。Testcontainers、ABP、FreeSql、XCode、Chloe、SqlSugar 的公开 SonnetDB 消费者已实际复验通过，ABP 同提交远程 CI 也已成功。上游 Provider 正式分发保持独立边界。

[机器可读验收清单](release-5.0-20261104-public-acceptance.json)绑定正式源码、标签、运行、十包签名、资产摘要、消费者结果和剩余边界；原始下载物与TRX保留为ignored证据。

## 固定身份与实际交付

- 发布代码：`84433f9edcceb84605520ebecc4b2879e3c4aa79`。
- 正式标签：`v5.0.0`，annotated object `c8f41918cedf624d486b0bd0e62e2d7bdd5b5cc9`，远端 peel 为上述代码提交。
- 冻结分支：`codex/release-5-0-final-20261104`，保持该代码提交。后续交接只增加纯文档提交，不把文档提交重新当作待发包来源。
- 旧标签 object `a3d3e7baf378054254b86942d7e854026cbc6050` 与旧代码 `455bf3b53b1e56df0064d8f574b0595cbb0b609e` 已保留在本地 `refs/codex-release-backups/v5.0.0-20261104` 及原始标签文本中；旧代码也是新代码的祖先。
- 标签更新前 fresh 核对十包 5.0.0 均为 404、认证 GitHub Release 为 404，使用精确旧 object 的 `--force-with-lease` 单次推送，未删除历史提交。

| 层级 | 当前结果 | 原始证据与边界 |
| --- | --- | --- |
| 同一代码的候选门禁 | READY，12/12 | 下表全部绑定 `84433f9e`，原始 Parity 候选也为 READY。 |
| 正式 Docker | PASS | [41721062233](https://github.com/IoTSharp/SonnetDB/actions/runs/41721062233)，Docker Hub 匿名 manifest/config 已独立读取。 |
| 正式 NuGet | PASS，公开 10/10 | [41721062306 attempt 3](https://github.com/IoTSharp/SonnetDB/actions/runs/41721062306/attempts/3) 实际十次 Created；匿名下载、nuspec source、DLL/有效载荷和 `dotnet nuget verify --all` 十包全部通过。 |
| 正式 GitHub Release | PASS，公开 52/52 | [v5.0.0](https://github.com/IoTSharp/SonnetDB/releases/tag/v5.0.0)，Release ID 433640152，非 draft/非 prerelease；52 个唯一资产全部实际匿名下载并核对 API digest，19 个载荷的 sidecar checksum 通过，无缺失、额外或 partial。 |
| 正式原生连接器 | PASS | [41721062359 attempt 2](https://github.com/IoTSharp/SonnetDB/actions/runs/41721062359/attempts/2) 只恢复上传，沿用三 RID 原成功构建；14 个公开 ZIP 的版本/README/LICENSE/原生入口库存通过。 |
| GHCR 匿名访问 | ANONYMOUS_DENIED | 新标签发布后匿名读取仍为 401；推送成功不计作公开读取通过。 |
| 公开 SonnetDB 消费者 | PASS | 下表均使用公开 5.0.0 包、独占 NuGet-only 缓存和适用的正式 Server/不可变容器；上游 ORM 候选与其正式 NuGet 分发保持分列。 |

Docker Hub 实际公开镜像为 `iotsharp/sonnetdb@sha256:f35b6493c9cbc41032ba7f825b02780a24be8c6c7072ee0442544cf3a4fbd325`，config digest 为 `sha256:72bc0f587518b566573f5fe47558af342c47eb55a605d75fc156e157f0696a9c`。匿名读取确认 OCI version `5.0.0`、revision 为完整 `84433f9e` 提交，平台是 Linux amd64。旧 `455bf3b5` / `sha256:6c5974f4ba4e81fd12a82cb08a6265c959d3c63f589f325df7d02ff9a1d81597` 证据保留为历史。

## 十二类候选证据

| 工作流 | Run ID | 结果 |
| --- | --- | --- |
| CI | 41716496000 | success，含全部测试分片、Format 与三平台 NativeAOT。 |
| CodeQL | 41716496066 | success。 |
| Docs | 41716496011 | success。 |
| Management | 41716943148 | success。 |
| Parity light/full | 41716950392 | success，真实起栈、各 23 项对拍与 84 项可靠性检查，原始候选 verifier READY。 |
| Document Soak | 41716956769 | success，quick profile 边界保留。 |
| Ecosystem Soak | 41716964079 | success，CI profile 边界保留。 |
| M19 hosted | 41716971578 | success，不代替固定目标硬件容量。 |
| M39 | 41716978496 | success。 |
| Publish preflight 5.0.0 | 41716986437 | success，十包与两平台安装器/网络/INSERT RETURNING 合同。 |
| Connectors preflight 5.0.0 | 41716993467 | success，三个 RID。 |
| Docker preflight | 41717000576 | success。 |

正式 Publish 中下载回来的 `release-readiness` artifact 同样为 READY、12/12，source 为 `84433f9e`。该冻结报告内的七天 scheduled 非阻断观察为 **NOT_READY，6/7 有效，85.71%**；失败项为 2026-11-01 的 `41290638954`。本机首次补读的 UTF-8 解码失败保留为 UNAVAILABLE 历史，显式 UTF-8 补读与正式 hosted 报告均确认 6/7；这是当时窗口，不称最终提交连续七天成功，也不沿用更早“七次全失败”。

## 历史凭据失败与原始冻结工件

2026-11-04 05:44:12（Asia/Shanghai），新正式 run `41721062306` attempt 1 在验证十包库存后推送首包 `SonnetDB.5.0.0.nupkg`，NuGet 服务返回：

```text
403 (The specified API key has expired. Please create a new API key at https://www.nuget.org/account/apikeys.)
```

这是新运行实际错误。失败后于 05:47 fresh 读取十个准确 `.nupkg` URL，全部 404；没有以旧运行的过期密钥日志推断新结果，也没有重复推送。仓库级 `NUGET_API_KEY` metadata 的 `updated_at` 为 `2026-11-02T03:35:45Z`，更新时间不证明密钥有效；未读取或输出 secret 值。当前进程无同名变量可供替换；组织级 metadata 无权限读取，保持 UNKNOWN，不扩权。若只更新组织级同名值，仓库级同名值仍会优先。

正式工件已保存并绑定 source，供失败作业恢复：

| Actions artifact | ID | Actions archive digest |
| --- | --- | --- |
| `nuget-packages-5.0.0` | 13273590352 | `sha256:9d625a427ea9dd986026ab93c1025854a0a355bd0b637a51937228a7179d368e` |
| `release-5.0.0-win-x64` | 13272379315 | `sha256:c05a13b8ccf6a4434d3607f17a50240e51ab8f0ff87c00e2dcaa9a9bbe613088` |
| `release-5.0.0-linux-x64` | 13273188868 | `sha256:2ed07f50e432b6ca9951b981325167aa616afd865090db0dc12377bf85251f7f` |

正式十包的 nuspec repository commit 与自身 `.sha256` 已验证；十包逐 entry 载荷与成功 preflight 相同，但两次未签名 ZIP 的整体 hash 因容器元数据不同而不相同，未把它们写成整包字节一致。后续 NuGet 仓库签名也会改变整个 ZIP；应验证签名、源码、DLL 与签名前载荷，不能要求签名后 ZIP 等于未签名 ZIP 的整体 hash。

正式 Windows artifact 仅下载一次，五个 payload 与五个校验文件均通过；SDK/full 内四包与同一正式 NuGet artifact 逐字节一致。解出的 NativeAOT Server 的 ProductVersion 为 `5.0.0+84433f9edcceb84605520ebecc4b2879e3c4aa79`，SHA256 为 `4c6cdee527f3f82c25945bbe5683cc92790e8daaf2b3b227c1b8adf2911f882e`。这是 **Actions artifact 验证**，尚未作为公开 Release Server 下载验收，也未因此启动公开 ORM 测试。

本机详细回执放在 `artifacts/release-5.0-20261104-final-execution/`，保留原始失败、当时 public 0/10、官方 readiness、十包库存、工件/Server、Docker Hub/GHCR、标签和历史字节记录。后续正式成功与公开 10/10 不覆盖原失败。该目录不提交构建产物；其它机器应从正式 run 和公开 Release 获取同源工件。

## 已执行的凭据纠正与原制品恢复

1. 用户先确认组织 Key 未过期并要求查重跑取值。删除仓库覆盖值后 attempt 2 和全新诊断 41789891046 均实际 403 expired。用户随后明确授权代为申请 Key；NuGet 官方页确认超过30天的旧Key已在11月1日失效，旧 sonnetdb Key 为 Expired。已单次创建30天的 `SonnetDB-5-release-20261104`，范围 `SonnetDB*`、可推新包/版本、无 unlist，安全存入 Windows 凭据库及本仓库 `NUGET_API_KEY`，metadata 为 `2026-11-04T07:43:06Z`，到期为北京时间12月4日15:34。完整原因和验钥400的 INCONCLUSIVE 边界见[诊断](nuget-secret-diagnosis-20261104.md)。
2. 重新核对公开十包仍404、原运行与标签仍绑定 source844 后，仅恢复失败的原发布作业。下面命令是已执行记录，不是再次发包指令：

   ```powershell
   gh api repos/IoTSharp/SonnetDB/actions/secrets/NUGET_API_KEY --jq '{name,updated_at}'
   git ls-remote --tags origin refs/tags/v5.0.0 'refs/tags/v5.0.0^{}'
   gh run rerun 41721062306 --failed --repo IoTSharp/SonnetDB
   ```

3. attempt 3 实际于北京时间15:49:52—59完成十包 Created，随后 GitHub Release 成功。正文来自 `84433f9e:docs/releases/5.0.0.md`，内容与去 frontmatter 的冻结稿一致；公开原始正文仅多一个开头 LF，未修改 Release。冻结正文 SHA256 为 `bec8905a1d8a989055bb4adc39222d8ccee76569eb42c1545a9672fb2afd7afb`，公开原始正文为 `d774446c613f9a21300e7c0af6a106d838adefbb9c97bfa25e93e5f072c166a1`。
4. Publish 成功后仅恢复连接器上传，attempt 2 success，沿用同一正式 run 的三个成功 RID 工件：

   ```powershell
   gh run rerun 41721062359 --failed --repo IoTSharp/SonnetDB
   ```

5. 公开十包已匿名下载并核 repository commit、DLL 与有效载荷，十个加密签名验证通过。NuGet 签名后整包不要求与未签名 GitHub 包相同。52个公开资产共1,675,087,792字节全部实际下载：9个bundle/安装器、10个原始NuGet、19个checksum、14个连接器。十个GitHub原始包whole-file hash与原正式工件相同，5个Windows载荷也与原工件相同；Docker Hub再次匿名source/digest通过。
6. fresh公开Windows Server的ProductVersion为 `5.0.0+84433f9edcceb84605520ebecc4b2879e3c4aa79`，SHA256为 `4c6cdee527f3f82c25945bbe5683cc92790e8daaf2b3b227c1b8adf2911f882e`，回执明确 `publicReleaseDownload=true`。以下消费者均已实际运行，原始失败与修正后的结果分别保留。

## 公开 SonnetDB 消费者结果

| 消费者 | 实际结果与分发边界 |
| --- | --- |
| Testcontainers | 公开四包、不可变source844镜像；UTC100ns、typed NULL、同一容器Stop/Start、重开读回与Dispose全部PASS，session容器/volume/network为空。首次拉取超时与夹具内联PK语法错误保留；只将夹具改为现行表级PK语法，字段及原断言不变。 |
| ABP | 固定head `979c3e4e406d1ff564756c2b189386886ba41780`，公开四包及实际DLL匹配；完整restore/Format/build和原22项全部PASS。首次public-only restore600秒超时未跑测试，在同一独占首次空缓存有界续跑后完成；PostgreSQL按owner清理并回收volume。同head的[远程CI41688290416 attempt2](https://github.com/IoTSharp/SonnetDB.Abp/actions/runs/41688290416/attempts/2)也为success，原缺公开依赖的NU1102结果保留历史。 |
| FreeSql | 固定候选 `a2268c1ff580b77d9ee202b3d34f230be59eab8b`，完整237/237，0失败/跳过；embedded/REST/frame-http2各25业务+8重开PASS，实际REST SQL68、Frame32、HTTP2事务21。公开SonnetDB三包和Server，FreeSql Core/Provider仍候选源，未称全公开组合。 |
| XCode | 固定fork `f21d0c7537955b0bcd79f875b9d649546f762147`，50/50，0失败/跳过；公开SonnetDB包/Server来源核验通过。上游#103架构拒绝继续由#404跟踪。 |
| Chloe | 固定fork `742ea2acb9b0f108e441cd8fc503100a737f9b17`，embedded/REST/frame各101/101，0失败/跳过；与XCode的独立重开旅程为6消费者进程、4个Server启动，每协议36业务+23重开检查全PASS。 |
| SqlSugar | 固定fork `18f9df7ede7b575b6caba5f46bcc070a4da95784`，三协议各93/93，含维护与重开；REST为HTTP/1.1，Frame为HTTP/2，无协议回退。构建保留9警告/0错误，其中NU1903为上游Oracle依赖图的Pkcs8.0.1，未压制或改依赖，不归入已检查的SonnetDB/Core/Protocol依赖。 |

各临时Server、PostgreSQL、测试容器和任务后代按owner回读并清理；native Cordonix回执均保留cleanup_complete。不可访问的机器进程快照仍为partial，不把空匹配列表称为全机无进程。公开缓存、下载物、原日志和失败证据保留供交接。

所有轮询、重试、下载和消费者继续设数量与墙钟双边界，并使用对应 owner 的进程/资源清理。不得重打标签、移动冻结源码、盲推未知项、使用旧 `8bf0e812` 恢复分支捷径或将私有候选测试改称公开消费。

[Issue #230](https://github.com/IoTSharp/SonnetDB/issues/230) 继续作为跨机器中心交接并保持开放。FreeSql等上游Core/Provider公开NuGet仍分别验收：公开FreeSql3.5.311及Provider404、XCode公开Provider仍依赖4.0、Chloe/SqlSugar尚待上游分发，不能被本次候选功能PASS覆盖。#392—#403保留原后续范围；新增[#404](https://github.com/IoTSharp/SonnetDB/issues/404)专门处理XCode上游明确架构拒绝，不重做引擎功能。固定硬件、实机安装、长期/物理掉电、生产和Server实例MQ恢复仍按原边界另验。
