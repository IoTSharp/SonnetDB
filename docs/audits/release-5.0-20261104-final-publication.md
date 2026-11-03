# SonnetDB 5.0.0 正式发布与凭据阻断交接

核查日期：2026-11-04，Asia/Shanghai。当前发布状态为 **BLOCKED_CREDENTIAL**；源码、候选门禁与 Docker Hub 已取得实际证据，十个 NuGet 包、GitHub Release 和最终公开消费者尚未完成。

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
| 正式 NuGet | FAIL，公开 0/10 | [41721062306](https://github.com/IoTSharp/SonnetDB/actions/runs/41721062306) attempt 1，两平台产物及合同成功；首包实际推送返回 403 expired。 |
| 正式 GitHub Release | NOT_PUBLISHED | 失败后认证 `releases/tags/v5.0.0` 为 404，未绕过 NuGet 门禁创建部分 Release。 |
| 正式原生连接器 | 构建 PASS，上传未完成 | [41721062359](https://github.com/IoTSharp/SonnetDB/actions/runs/41721062359) 的 Linux x64、Windows x64/x86 全成功；上传因 Publish 失败而拒绝。 |
| GHCR 匿名访问 | ANONYMOUS_DENIED | 新标签发布后匿名读取仍为 401；推送成功不计作公开读取通过。 |
| 最终公开消费者 | NOT_RUN | Testcontainers、ABP、FreeSql、XCode、Chloe、SqlSugar 已准备独立缓存和来源绑定，尚未使用公开 5.0.0 包实跑。 |

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

正式 Publish 中下载回来的 `release-readiness` artifact 同样为 READY、12/12，source 为 `84433f9e`。七天 scheduled 是明确非阻断观察，最新实读为 **NOT_READY，6/7 有效，85.71%**；失败项为 2026-11-01 的 `41290638954`。本机首次补读的 UTF-8 解码失败保留为 UNAVAILABLE 历史，显式 UTF-8 补读与正式 hosted 报告均确认 6/7；不能使用更早“七次全失败”的快照代替当前事实。

## 新发布错误与已冻结工件

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

本机详细回执放在 `artifacts/release-5.0-20261104-final-execution/`，包括新失败日志、官方 readiness、十包库存、Windows 工件/Server 身份、Docker Hub/GHCR 回读、public 0/10、标签更换和历史字节保持记录。该目录不提交构建产物；其它机器应从上述正式 run 获取同源工件。

## 密钥纠正后的恢复顺序

1. 在 [IoTSharp/SonnetDB 的 Actions secrets](https://github.com/IoTSharp/SonnetDB/settings/secrets/actions) 更新仓库级 `NUGET_API_KEY`，使用未过期且覆盖本次十个包名的密钥。仅回读 metadata，不输出明文；再次核对十包与 Release 是否已存在，任何已存在项都须核 source/载荷，未知结果先对账。
2. 核对远端 `v5.0.0` 仍是上述 annotated object/代码，正式 run 仍绑定 source844；保留已有成功构建与工件，执行失败作业恢复：

   ```powershell
   gh api repos/IoTSharp/SonnetDB/actions/secrets/NUGET_API_KEY --jq '{name,updated_at}'
   git ls-remote --tags origin refs/tags/v5.0.0 'refs/tags/v5.0.0^{}'
   gh run rerun 41721062306 --failed --repo IoTSharp/SonnetDB
   ```

3. 有界读取该 run 的新 attempt、NuGet 服务结果和 GitHub Release 终态。确认 Release 是非 draft、非 prerelease、source/tag 正确，正文来自 `84433f9e:docs/releases/5.0.0.md` 且仅去 YAML frontmatter。冻结正文 SHA256 为 `bec8905a1d8a989055bb4adc39222d8ccee76569eb42c1545a9672fb2afd7afb`。
4. Publish 成功后恢复连接器上传，沿用同一正式 run 的三个成功 RID 工件：

   ```powershell
   gh run rerun 41721062359 --failed --repo IoTSharp/SonnetDB
   ```

5. 重新匿名下载十个 NuGet 5.0.0 包，验证 repository commit、签名与 DLL/载荷；下载公开两平台 bundle/安装器、三个 RID 的 14 个连接器 ZIP 及其适用校验文件。预期 Release 共 52 个资产；库存、实际下载和 hash 分别确认。再次回读 Docker Hub 的不可变 digest 与 source。
6. 从公开 Release fresh 下载匹配的 Windows Server，核 ProductVersion/source/SHA 后，再启动真实消费者：固定公开 NuGet-only 空缓存；Testcontainers 用上述不可变镜像执行 UTC 100ns/typed NULL、同一容器停启重开与 Dispose；ABP 22 项及 PR #2 固定 head 的最新 CI；FreeSql 237 项及三协议/重开；XCode 50 项；Chloe 三协议各 101 项与独立重开；SqlSugar 三协议各 93 项。保留原计数、无 skip、实际 DLL/包/Server 身份、协议和资源清理证据。

所有轮询、重试、下载和消费者继续设数量与墙钟双边界，并使用对应 owner 的进程/资源清理。不得重打标签、移动冻结源码、盲推未知项、使用旧 `8bf0e812` 恢复分支捷径或将私有候选测试改称公开消费。

[Issue #230](https://github.com/IoTSharp/SonnetDB/issues/230) 继续作为跨机器中心交接。FreeSql 等上游 Core/Provider 的公开 NuGet 发布与本次 SonnetDB 发布分别核验；SonnetDB 成功本身不足以关闭该 Issue。#392—#403 是已登记后续范围，本次未扩做。
