# SonnetDB Workbench Preview 1 — candidate 10

<!-- preview-identity: {"candidate":"candidate-10","version":"4.5.0-preview.1.10","sourceCommit":"54eefc816f133a805b0d38f0cbe930d2c17650b6","profile":"preview-1","scopeRevision":"R1","rid":"win-x64"} -->

2026-10-11。**发布准备中，NOT_READY，尚无此版本的公开下载。** 本说明供 P07 评审；完成门禁并取得 P08 授权后才可公开。它描述 Windows 11 x64 上的同源环回 Web Admin＋匹配 NativeAOT Server 便携包。源码 `54eefc816f133a805b0d38f0cbe930d2c17650b6`，构建 run `38104431383` / attempt 1，profile `preview-1` / R1。

## 本版可评估范围

- 首次设置、用户名密码登录、选择获授权数据库、退出后重新登录，以及 About 中的版本/源码/profile 信息。
- 单条受预算支持的只读 SQL、SHOW/DESCRIBE、EXPLAIN；关系表 schema 与筛选/排序/翻页，每页最多 200 行；measurement schema 与有限时间范围点查询，最多 500 行。SQL 标识符保留原始拼写，双引号名称精确匹配。
- 当前仍获授权的已加载结果窗口导出，以及可见的截断、错误、取消状态。导出不是完整表、全部页面或备份。
- 有 Write/Admin 授权时，关系表普通标量列的单行插入：暂存、预览、一次确认、一次提交、独立回读。Read grant 禁止写入；切库、切身份或撤权使旧结果和审批失效。取消不等于回滚；unknown 可能已经提交，必须独立核对，禁止重放旧审批或盲目重试。

共同预算为 30 秒、SQL 输出最多 1000 行、响应/保留/导出各 4 MiB、SQL/插入输入 UTF-8 最多 64 KiB、每个活动操作同时最多一个读取。更小的模型上限优先；估算物化预算不代表物理堆硬上限，浏览器取消不证明 Server 停止扫描。

## 下载与首次使用

P08 后同一个 prerelease 应恰好有四个附件：`sonnetdb-workbench-web-4.5.0-preview.1.10-win-x64.zip`、`sonnetdb-workbench-web-4.5.0-preview.1.10-manifest.json`、`sonnetdb-workbench-web-4.5.0-preview.1.10-SHA256SUMS.txt` 和本 `notes.md`。SHA256SUMS 只列 ZIP、manifest、notes 三项，不自哈希。不要从旧 Release 或其它候选补文件；checksum 提供字节完整性，下载来源仍须核实。

1. 在 PowerShell 7 中用 `Get-FileHash -Algorithm SHA256 <文件>`，逐项比对 checksum 的完整文件名和哈希。manifest 的 ZIP SHA256 为 `d01729b97a8db906ffb78bf88138cfa58d91b708efaf03fa408c0b59aaab51ed`，大小 46,634,305 bytes。确认 manifest、Server 和 Web 属于上述同一候选。
2. 解压到新的空目录，例如 `C:\SonnetDB\preview-1.10\package`，保留完整库存。包的相邻 `..\data` 是独立数据目录；不要选择已有生产数据目录或覆盖旧安装。
3. 在该 package 目录执行 `pwsh -File .\Start-Preview.ps1 -VerifyOnly`。成功只说明包内库存与身份校验通过；端口被占用、数据目录权限不足、身份错配须先解决。
4. 执行 `pwsh -File .\Start-Preview.ps1`，保持控制台打开，访问 `http://127.0.0.1:5080/admin/`。空数据目录由首次设置页面创建自己的管理员，再以用户名密码登录。无随包凭据或真实数据。
5. 由操作员通过已有管理接口准备测试数据库、表/measurement 和授权。首版 Web 不提供完整建库、用户或 grant 管理；P05 的小数据集和用户由操作员 API 准备，不能据此宣称 Web 治理已验收。
6. HTTP `127.0.0.1:5080` 与 Frame HTTP/2 `127.0.0.1:5081` 仅用于本机。以 Ctrl+C 正常停止并确认进程退出，再做重开或数据副本操作。

包内 START-HERE 是构建时说明，其中 P05/P06 待验状态保留为历史快照；当前证据以候选10的安装报告、审计补记和本说明为准。原 manifest 的 NOT_RUN 字段同样不回写。

## 已知限制与验收范围

本机 Windows 11 build 26300 的实际解压包已用 Chrome `150.0.7871.115` 与 Edge `155.0.4283.45` 验证 R1 用户旅程、真实密码登录/读取/单行审批、撤权清理、正常重开、身份拒绝，以及隔离同候选数据快照回退。合成数据、浏览器响应丢失/延迟/身份故障注入分别记录，不是生产业务、真实网络故障、崩溃恢复或模型质量证据。

document、KV、fulltext、vector、object、MQ、Graph 七个专用工作台读取按 R1 延期，页面说明延期且传输拒绝；九模型源引擎存在不表示这七个 Preview 工作台已可用。Graph 仍为 Beta。Observe、Flows、AI、Govern 入口禁用；AI/MCP onboarding、外部模型数据发送、自由 SQL 写入、批量/UPDATE/DELETE/DDL、摄取/维护、高级模型操作均延期。

Studio、VS Code、Linux/macOS/ARM、其它 Windows 版本、移动浏览器、干净机器安装、服务/系统安装器、容器、跨域、TLS/代理子路径和外网部署未验收，不作为下载资产。Web 与 Server 必须来自同一包；4.0.0、任意旧候选混配和滚动升级不受支持。Preview profile 只限定 Web 入口，Server 的完整 API 及授权层仍存在，不构成多租户沙箱。

MQ 的逻辑身份是 database＋Topic，实例级持久化位于 `.system/mq`；单库备份不包含实例 MQ。正常停止后的测试数据副本不等于完整实例备份、跨模型一致性快照或灾难恢复。

许可证库存含 243 条依赖；16 条缺原文、仅元数据或 Microsoft runtime 再分发条款仍待 P07 复核。保留包内 LICENSE、THIRD_PARTY_NOTICES、VC-RUNTIME-NOTICE、dependency-licenses 和 licenses；不把 Microsoft runtime 归为 MIT。P07 原完整 workflow/job/step/artifact 门禁尚未放行，七天 scheduled Parity 单列为非阻断观察。

## 升级与回退

本次没有验证任意跨版本升级/降级。评估新候选时先正常停止当前进程，在新目录使用完整新包和独立测试数据副本；核新候选的兼容与安装证据后再决定迁移。不要单独替换 Web 或 Server，也不要让旧 Server 打开新数据目录。

P05 只验证同候选10的隔离快照恢复：正常停止后复制数据并逐文件校验；重开后添加测试行，再正常停止，恢复同候选快照并核库存，重新读取确认快照后新增行消失。若需要回退另一版本，保留原二进制及该版本自己的停止后数据副本，两者一起切换；没有格式兼容证据时不得共用数据目录。此办法仍不是生产备份承诺，MQ 实例范围需单独处理。

## 反馈

请在 [SonnetDB Issues](https://github.com/IoTSharp/SonnetDB/issues/new) 提交问题，注明 candidate-10、完整版本/源码 SHA、Windows 和浏览器版本、manifest/ZIP SHA256、发生步骤、权限层级及可脱敏复现。描述实际看到的成功、失败或 unknown，保留一次提交的时间/关联信息，避免用重复写入复现未知结果。不要附密码、token、Cookie、完整凭据文件或生产数据。

来源：[不可变候选10](https://github.com/IoTSharp/SonnetDB/blob/6b90fcfe54e2ced1ea141bdb297e50f13c80a18c/docs/design/m47-unified-management-workbench/preview-1-candidate-10.json)、[P05 安装报告及收口审计纠正](https://github.com/IoTSharp/SonnetDB/blob/d4e4b7fdb15dfcb47e81ac598778ad64aba25173/docs/design/m47-unified-management-workbench/preview-1-p05-installed-journey.md)。旧 FAIL/UNKNOWN 和错误空窗审计保持原样；空窗不作为验收证据。
