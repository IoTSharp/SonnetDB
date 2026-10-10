# Workbench Preview 1：独立 Web 分发合同

本文件是 M47-P01 的设计交付，关联 [范围冻结](preview-1-scope.md)与[候选 01 清单](preview-1-candidate-01.json)。**当前实现状态：NOT_IMPLEMENTED；发布状态：NOT_READY。** P01 不修改现有 workflow、不生成产品包、不创建 tag/Release、不发布 NuGet/容器/Marketplace。

## 现有流程的实际行为

候选 `c40fa670e3a170c5fac8cefa181d618631862270` 的 [publish.yml](../../../.github/workflows/publish.yml) 有两种入口：

| 入口 | 实际行为 | 首版限制 |
|---|---|---|
| `workflow_dispatch(version)` | 构建 NuGet；linux-x64/win-x64 构建 bundles/installers；`-BuildAdminUi`；运行网络默认设置与 installed INSERT RETURNING/发布基线兼容验证；上传 Actions artifacts | 继续作为原 release policy 的完整预检。Actions 中的全套验证产物不是首版公开下载白名单 |
| push `v*` | 先调用现有 readiness verifier；完成 packages/bundles 后发布 NuGet.org；创建或更新 GitHub Release，扫描下载目录内全部 nupkg/zip/tar.gz/msi/deb/rpm/sha256，`--clobber` 上传并撤销 draft | 即使版本有 `-preview` 也会公开全套资产，仅改变 prerelease/latest 标志。**不能用这个入口发布 Web Preview** |

[release.ps1](../../../eng/release.ps1) 的 `bundles` 生成 SDK、full Server（含 CLI/NuGet）和 Windows Studio ZIP；Windows `installers` 要求 Studio bundle 并生成 Server MSI/Studio MSI；Linux 生成 DEB/RPM。publish.yml 本身没有 VSIX 打包步骤；VSIX 来自 Workbench smoke/扩展渠道，不能误写为 Publish 默认资产。现有 [connectors-release.yml](../../../.github/workflows/connectors-release.yml) 与 [docker-publish.yml](../../../.github/workflows/docker-publish.yml) 也有 `v*` 标签入口；不能只过滤 GitHub Release 文件却放任其它发布副作用。

## 冻结分发渠道与资产集合

只选择 **GitHub 独立 prerelease**：标签 `workbench-preview-1.N`，标题 `SonnetDB Workbench Preview 1 — candidate N`，`prerelease=true`、`latest=false`。P08 才能创建/公开。公开说明和 Issues 反馈指向该版本，不替换旧 4.0.0 Release、不更新 Marketplace 0.4.1、不发 NuGet/容器/系统包/连接器。当前 N=1 是保留规则示例，没有可下载文件。

对版本 V=`4.5.0-preview.1.N`，公开附件集合必须**恰好**为：

| 文件 | 内容 / 生成归属 |
|---|---|
| `sonnetdb-workbench-web-V-win-x64.zip` | P04：同候选 NativeAOT Server 及必要运行时/原生依赖、同源 `wwwroot`、配置模板、启动说明/脚本、LICENSE/第三方 notices；独立数据目录布局与 Preview profile。不含 Studio、CLI、SDK/NuGet、测试日志、token、预置真实数据 |
| `sonnetdb-workbench-web-V-manifest.json` | P04：完整 source SHA/tree/gitlinks、组件实际版本、contract/能力/入口 profile、RID、构建参数/工具依赖版本、许可证清单、ZIP SHA256/大小及包内文件 SHA256、兼容/预算。引用具体 gate/实物证据；缺失值不伪造 |
| `sonnetdb-workbench-web-V-SHA256SUMS.txt` | P04：ZIP、manifest、发行说明三个文件的精确文件名与 SHA256；不自哈希，不采用通配符补入额外资产 |
| `sonnetdb-workbench-web-V-notes.md` | P06：已验收范围、Beta/权限/unknown/备份边界、首次使用、升级/回退、反馈入口和已知限制；与包内版本/公开 Release 正文一致 |

不额外分发一个可任意覆盖旧 Server 的 Web-only 静态 ZIP；“独立 Web 分发”指**Web＋匹配 Server 的独立发行单元**，不携带 Studio/SDK。Linux/ARM 包、MSI/DEB/RPM、VSIX、Studio ZIP/MSI、SDK、full bundle、nupkg、连接器、容器 tag 均延期。现有全套预检产物仍按原规则构建/验证/保留，其成功不自动授权公开。

## P04/P06 的可审查实施边界

1. **P04 组包**：从最终已提交 source SHA 的干净 CI checkout 和锁定 gitlinks 构建，使用新候选版本。不得读取共享 dirty 工作树、旧 `web/dist`、旧 Release 或 WB80 运行目录。复用 Server 原发布/网络默认设置与已有 validator；单独的 Preview 组包器只取该次 Server publish 与其 `wwwroot`，补 profile/manifest/notices，不直接重命名 full/Studio ZIP。实现精确包内库存与禁止项检查，记录依赖哈希与 AOT 结果；任何源文件/打包变动进入下一候选版本。
2. **P06 分发**：新增独立、显式手动入口（建议 `.github/workflows/workbench-preview-publish.yml`，此文件当前不存在），不挂 `main` push 或自动 tag 触发；先以只读权限解析候选与 P07 evidence，再进入需要 P08 授权的 publish 环节。checkout 必须是清单 full SHA，验证 tag 不存在或已精确指向该 SHA。
3. **完整 gate**：调用现有 `eng/verify-release-readiness.ps1 -CommitSha <final SHA> -Version <V>`，并核 C01～C05/D01、P03/P05 的真实证据。现有 policy 中 publish/connector dispatch 预检仍针对同 SHA/V、五 Workbench job 仍全部必需。不能把这个新入口做成绕过旧 verifier 的捷径。
4. **上传前 fail closed**：从指定 run/attempt 下载已验证的 Preview 产物；核 version/SHA/profile、SHA256/大小、包内库存与四附件集合，漏项、多项、旧构建或任何延期文件一律失败；上传显式数组，禁止 `find *.zip`/通配扩展名。测试至少包含“混入 Studio ZIP”“混入旧 VSIX”“缺 manifest”“错误版本/SHA/hash”和正常集合。
5. **不可变与未知效果**：禁止 `--clobber` 覆盖发行物或移动 tag；同 tag 已存在时只读比对，字节/来源不同则阻断并分配新 N。上传效果未知先独立回读 ID/清单/hash，不盲重试；必要修正用新候选与新版本。
6. **P05/P06 回退**：仅在隔离数据副本验证首次安装/正常停止重开、升级与回退；未经数据格式兼容验证不得让旧 Server 打开新数据目录。下载相同文件、回退旧二进制与数据恢复分别取证。

P04 实现和验证新组包器，P06 实现和验证独立上传器；本次只冻结其接口/输入/拒绝条件，**没有声称源代码与入口控制已经落地**。两者未闭合时 D01 阻断所有首版公开渠道。

## 不变的发布门禁

[release-readiness-policy.ps1](../../../eng/release-readiness-policy.ps1) 是门禁权威来源；候选 JSON 保存该 SHA 的完整 workflow/job/artifact 快照与具名 required steps，后继候选按实际 policy 重新核对，不能删减：

- CI 的 Ubuntu/Windows Build & Test、Format Check、三个平台 NativeAOT；CodeQL；Docs Build。
- Workbench **全部五项**：Server management contracts、Web Admin and Studio bridge、Studio desktop host、VS Code HTTP consumer、VS Code Extension Host，以及三个必需 artifacts。
- Parity light/full；document/ecosystem soak；M19 hosted capacity；M39 trigger evidence。
- 同版本 publish dispatch 的 NuGet 与 linux-x64/win-x64 bundle、两平台 INSERT RETURNING 证据；三个 RID 的 connectors dispatch；Docker dispatch validation。
- verifier 的同仓库/同 SHA/正确事件与路径、latest attempt、已完成 success、必需 steps、非空未过期且属于当前 attempt 的 artifacts 规则保持。PR/check/build 成功不能代替真实安装、用户旅程或固定目标硬件证据。

`eng/verify-release-artifacts.ps1`、连接器库存/native-entry 验证与既有容量/soak/适用硬件要求不因公开附件减少而撤掉。七天 scheduled Parity 是独立、非阻断观察；它不替代 light/full，也不新增“必须先等七天”的首版门禁。P01 未执行远端门禁评估，machine manifest 的产品/发布证据一律 `NOT_RUN` 或历史明确失败引用。
