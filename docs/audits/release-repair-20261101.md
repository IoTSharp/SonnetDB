# SonnetDB 5.0 发布阻塞修复（2026-11-01）

用户已选择保留新 API，按 **5.0 主版本**准备。此记录接续 [10 月 31 日发布评估](release-readiness-20261031.md)；该评估中的失败证据保留，以下结果不能倒写成旧 main 已通过。

## 最终候选与发行范围

代码候选冻结为 `89d807406293084a9311fb98fda2099a59b12345`，预检版本为 `5.0.0-preview.readiness.20261031`。同提交的十二类远程工作流全部成功；完整放行验证器于北京时间 2026-11-01 02:04:39 返回 **READY**，issues 为空，逐项门禁见 [证据归档](release-evidence-5.0-20261101.json)。本节描述最终候选，后文保留各轮真实失败及修复过程。

最终远程 10 包已下载，逐包核对版本、repository commit 和附件 SHA-256；Core/Protocol/Client 的字节与远程隔离缓存消费者报告一致。公开 4.0 包到这组最终远程包的升级、事务写入、重复重开及离线备份回退均已复验通过。管理工作台默认配置为 313 PASS / 25 项 native-only SKIP，新增的 StudioNative 必跑步骤另取得 25 PASS / 0 SKIP。

本轮代码阻塞修复及候选技术预检已经完成，可以评审 5.0 预发布。尚未正式发布 `5.0.0`；正式版必须对精确版本重新取得产物预检。Windows 干净机器的安装/升级/卸载、固定目标硬件与长期运行，以及 VS Code 打包工具链告警仍按对应渠道或发行声明验收，不能由本轮绿灯推断完成。

本次收尾仅提交审计、机器证据及 HANDOFF，不改变冻结代码。文档提交不继承 `89d80740` 的门禁，READY 只能绑定证据中的代码 SHA 和预检版本。未合并 main、未打标签、未正式发包或部署。

## 本轮实现

- 修复索引候选收集对 `= NULL` / `IS NULL` 来源的混淆：普通 NULL 等值仍为 UNKNOWN，允许空唯一索引探测；真实 IS NULL 不使用遗漏 NULL 键的唯一索引。保留原扫描断言、相关绑定、完整残余谓词及事务 overlay。
- 新增 8 项普通/反向 NULL 等值、与 IS NULL 合取、单列/复合 nullable 唯一/普通索引回归。
- NuGet、SDK/完整 bundle、候选晋级及标签恢复纳入 Protocol/Client，严格库存由 8 包变为 10 包；旧标签恢复仍保留原版本合同。
- Core 的 4.x 继续校验 4.0.0；首个 5.0 建立新主版本 API 边界，后续 5.x 自动校验正式 5.0.0。Protocol/Client 同样启用包兼容校验。没有生成 API 抑制文件或关闭校验。
- Publish 增加仓库外独立缓存的真实包消费者，并将执行步骤和证据纳入 readiness 硬门禁；已有 ADO 包合同把 Protocol 固定到候选源并逐字节核对。
- [5.0 升级准备](../releases/5.0-migration.md) 明确程序集迁移、重新编译、分页 long API、v19 floor 与备份回退边界。尚未宣称整个 4.5 路线图或完整 M49 已完成。

## 已取得的本机证据

环境：Windows、.NET SDK 10.0.400、Release；开发基线 `3c40730a35140a24589e606310bcf1e2dae88ec3`，分支 `codex/fix-exists-release-readiness-20261031`。原始证据位于被忽略的 `artifacts/release-fix-20261031/`，不作为可晋级发行产物。

| 验证 | 实际结果 |
| --- | --- |
| EXISTS 完整类 | 22 PASS / 0 FAIL / 0 SKIP；原来失败的 2 项及新增 8 项通过 |
| Core 完整测试 | 7995 PASS / 0 FAIL / 0 SKIP，10 分 49 秒 |
| Client 完整测试 | 219 PASS / 0 FAIL / 0 SKIP |
| 真实 5.0 候选打包 | `5.0.0-preview.readiness.20261031` 的 10 包全部生成，严格库存、身份和校验和通过 |
| 外部消费者 | 两个独立缓存；候选 Core/Protocol/Client 安装字节一致；Core 创建、重复重开、NULL/唯一键查询通过；Client 无 Core/Data 依赖且 Frame round-trip 通过 |
| 4.x 拒绝边界 | 正常 restore/build/pack 仍返回 1，15 个 CP0001 + 2 个 CP0002；不得晋级其已产生的 nupkg |
| 版本策略 | 3 个项目 × 8 个版本，共 24 项 MSBuild 策略检查通过 |
| 发行库存合成合同 | 20 项通过，含 Protocol/Client 缺包及 SDK 缺 Protocol 的拒绝 |
| 候选晋级合成合同 | 10 项通过，含 Protocol/Client 缺包拒绝 |
| 其它发布合同 | readiness、测试分片覆盖、网络默认值及 M43 30 项合成合同通过；这些不能算作生产证据 |
| 完整 solution restore / build / Format | 三项退出 0；Release 全 solution `/warnaserror` 构建为 0 警告、0 错误；Format 使用与 CI 完全相同的方案、warn 级别及 extensions 排除，输出一条 workspace 加载警告 |

原始失败留在 `artifacts/release-review-20261031/`。本轮还保留了消费者夹具 nullable 编译失败及一次 `--no-build --no-restore` pack 的增量缓存观测；后者未实际证明基线拒绝，随后用正常 restore/build/pack 重新取得上述 17 项真实 API 诊断，没有以缓存绿灯代替兼容验证。

## 4.0 → 候选升级与离线回退

使用公开 NuGet `SonnetDB.Core 4.0.0`、独立缓存和真实引擎创建订单/流水库，含已提交数据与 nullable 唯一请求键。关闭旧进程后复制目录作离线备份，再在独立副本上执行：

1. 候选包读取旧库，核对两个表的金额及唯一键命中/未命中/NULL 语义。
2. 提交事务 DDL 并提交订单/流水两表金额更新；候选连续两次独立进程重开，金额一致。
3. 旧 4.0 包重开升级目录，实际抛出 `InvalidDataException: TableSchema: unsupported format version 19`。
4. 从未改动的升级前离线备份恢复到另一目录，4.0 连续两次独立进程重开，恢复原金额；备份前后 SHA-256 清单一致。

最终复验使用 Publish run `41225641681` 下载的不可变包，日志为 `upgrade-final-89d80740/upgrade-*.log`，机器结果与备份文件清单为 `upgrade-final-89d80740/upgrade-result.json`；关键结果已纳入跟踪的机器证据归档。此证据只覆盖本机的关系表业务夹具与关闭数据库后的目录复制，不涵盖在线 Backup API、所有模型、物理掉电、客户真实生产库或整个 Server/MQ 实例。候选上的新写入不会出现在升级前备份，回退仍需处理这些写入的回补。

## 正式发布仍须满足

最终冻结 commit/version 已通过跨平台 CI、Publish/Connectors/Docker dispatch、候选 Parity 以及现有十二类门禁。安装器实机、固定目标硬件和长期证据按实际发行渠道和声明另验。七天 Parity 仍是非阻断观察，不人为重新等待七天。

本次还验证了 2026-10-25 至 2026-10-31 最新七次 scheduled Parity 原始数据，7/7 有效、各次 light/full 均通过。它们覆盖多个历史提交，不能称为最终候选连续七天运行的证据。

当前未打标签、未正式发包、未部署。最终预检证据绑定不可变提交及 preview 版本，不冒充后续文档提交或正式 `5.0.0` 的产物证据。

## 第一轮远程预检

已推送 SQL 修复 `48fe989b` 和 5.0 发包链 `5f165d4e`，远端回读一致，`parity-results` 保持 `b7c59769`。在 `5f165d4e` 上派发十二类既有门禁；Parity 使用 both 且不写入结果分支，容量使用 GitHub-hosted、文档使用 quick、生态使用 ci，不把这些 profile 冒称固定硬件或长稳证据。

Publish run `41219222210` 在 CAP 测试的编译阶段失败：该作业的 checkout 未初始化 CoAP/MQTT 子模块，出现 missing project / CS0246。已补递归检出，并使消费者证据只在对应步骤实际执行后上传；此项必须在新提交的干净 runner 上重验。本机包含子模块的 CAP 完整测试 42/42 通过，0 失败、0 跳过。原失败完整日志保存为 `publish-attempt01-failed.log`，不以本机 PASS 冒称远程已修复。

推送时另核对到 Dependabot #78：`extensions/sonnetdb-vscode/package-lock.json` 中开发依赖链的 `@azure/msal-node 5.4.0`，GHSA-xcx6-jx8p-x3h5，修复版本 7.1.0。扩展没有 runtime dependencies，当前 `vsce package --no-dependencies` 且 `.vscodeignore` 排除 `node_modules/**`；本次没有使用该工具链的授权码登录入口。此告警仍应单独升级及验证开发/市场发布工具链，不能声称依赖告警已关闭，也不能据此直接断言数据库 Server 运行时受影响。

## 第二轮远程预检与管理工作台修复

提交 `1fd0d277bb8a55a801a5c6b9806bc71101eea9a0` 的十二类工作流全部结束：CI、Publish、CodeQL、Docs、Parity、Document Soak、Ecosystem Soak、M19、M39、Connectors 和 Docker 成功；Management smoke 失败。Publish 的 CAP、10 包及 Linux 隔离缓存消费者均成功，Linux/Windows 完整 bundle、网络默认值和已安装 INSERT RETURNING 合同通过。三平台 NativeAOT 成功，当前候选 Parity light/full 成功；这些不能覆盖管理 smoke 的失败。

Management run `41220426069` 的原始日志、截图和 trace 保留在本机 `management-attempt01*`。本机复现了同样两项失败：

- OAuth 夹具把 `/v1/events` 返回成 `200 application/json` 的 `{}`，浏览器 EventSource 永久关闭，应用按合同清空数据库会话并跳到登录页。已修成有效 `text/event-stream`，未修改生产 SSE 鉴权逻辑。
- Measurement 监控 401 后，全局应用正确跳转登录；原测试却继续查找已卸载按钮。用已有组件宿主保持原清载荷断言后，又发现生产组件在登录态清空时自动重发 point 读取。已在无凭证时直接锁定、取消请求并清空载荷，防止旧请求回填或无凭证重读。

保留组件 401/403 锁定、Schema/Auth 刷新后不得自行解锁和载荷清除断言，新增两个实际 Vue 路由的 401/403 浏览器夹具旅程：401 清空会话并保留原资源登录返回地址；403 保留会话并锁定，均不泄露旧点值、编辑或导入内容。它们使用受控 API，不冒称真实 Server 授权证据。

本机 Web 构建通过，合同测试 472/472、OAuth/Measurement/会话过期三个完整测试文件 37/37 通过，0 失败、0 跳过。本机采用已安装 Edge（Chromium）及仓库自带 runner 的临时副本，仅在被忽略的副本中修正本机 PowerShell 路径；远程仍使用原 runner 与 Chromium。最终代码提交必须重新取得同 SHA 的十二类门禁与 raw Parity evidence，旧提交结果不移用。

## StudioNative 配置补验与 CI 必跑步骤

`692c6433` 的 Management run `41223423315` 成功，Web smoke 实际为 313 PASS / 25 SKIP；25 项分别属于 `copilot-studio-native.spec.ts`（13）及 `studio-host-client.spec.ts`（12），原先按默认 BrowserDirect 配置跳过。该提交的候选 raw Parity 两个 profile 各 51 场景通过，不能由这些结果推断 StudioNative 已验。

单独启动 StudioNative 后，首次运行出现 4 个 45 秒超时，并在外部 240 秒边界结束；`web-studio-native.log` 保留此失败记录。两个夹具也把 `/v1/events` 返回为 JSON，导致会话过期，已修正为有效 SSE。两个首场景恢复后，两个完整文件 25/25 通过，0 失败、0 跳过；日志为 `web-studio-native-final.log`。这些依然是浏览器内受控桥接/API，不能冒称实际 WebView2 或原生凭据库验收。

Management CI 新增明确的 `Run Studio native browser contracts` 步骤，在 StudioNative 配置下单独运行上述两个文件；readiness 同时要求默认 smoke 和此步骤实际成功。新增合同验证缺失、跳过或失败均拒绝放行。因此最终候选必须以包含新步骤的提交为准，不能继续使用 `692c6433` 的旧管理收据。

远程 NuGet 原始消费者报告已下载并核对为 PASS。最终包归档下载曾遇到超时/EOF，保留了原失败记录；随后为下载子进程关闭 HTTP/2，成功取得完整 10 包。上文最终远程包升级/回退复验已完成，该历史下载失败不再是当前阻塞。

## 最终同提交远程收据

以下均绑定 `89d807406293084a9311fb98fda2099a59b12345`，结论均为 success：

| 工作流 | Run ID | 验证范围 |
| --- | --- | --- |
| CI | 41225638080 | Linux/Windows 全量分片、Format、Linux x64/ARM64 与 Windows NativeAOT |
| Publish | 41225641681 | 10 包、真实消费者、Linux/Windows bundles、网络默认值及解包后 SQL 合同；未正式发布 |
| CodeQL | 41225645867 | 分析成功；候选分支开放 code-scanning 告警 0 |
| Docs Pages | 41225649388 | 文档检查 |
| Management workbench smoke | 41225652886 | 五个作业成功；默认浏览器 smoke 与 StudioNative 分别实际运行 |
| Parity | 41225657087 | light/full 各 51 场景，raw artifacts 验证有效、issues 为空；不写结果分支 |
| Document Store Soak | 41225661113 | quick，不能冒称长期运行 |
| Ecosystem Soak | 41225664422 | ci profile |
| M19 Capacity Evidence | 41225667988 | GitHub-hosted，不能冒称固定目标硬件 |
| M39 SQL Trigger Evidence | 41225671451 | quick=false |
| Connectors Release | 41225674534 | 三平台连接器预检 |
| Docker Publish | 41225677469 | dispatch 预检，不推送正式镜像 |

Dependabot #78 在本次查询时仍 open，属于 VS Code 开发打包工具链的高危依赖；没有关闭告警或添加未经验证的 major override。候选 CodeQL 结果不能代替该工具链的独立依赖升级与验证。
