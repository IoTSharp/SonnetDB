# GH-Issue #197：安装包结果合同与发布门禁（2026-09-26）

## 状态

GitHub 回读仍为 **open**，最新公开 Release 为 `v3.1.0`。本次生产源码基线为 `630096355a740e6cda46188b406a899345a196f6`，候选版本为 **`4.0.0-issue197.1`**；新增验收工具位于 `codex/issue-197-package-contract`。所有候选包仅保存在本地目录，未创建公开 tag、推送 NuGet 包或关闭 Issue。

这次补齐的是已实现 `INSERT ... RETURNING` 的安装包验收及发布门禁。SQL 引擎、ADO 生产代码、持久化格式和第三方依赖没有变更。4.0.0 仍按[既有主版本方案](issue-184-197-release-readiness-20260925.md)等待正式发布决策；[候选发布说明](../releases/4.0.0.md)明确首次完整合同的目标版本与 3.1.0 边界。

## 可重复验收

`eng/verify-insert-returning-package.ps1` 为每次运行建立独立输出和 NuGet 缓存，用精确版本引用 `SonnetDB`，通过 source mapping 强制 `SonnetDB` / `SonnetDB.Core` 来自指定候选 feed。脚本检查 `project.assets.json` 的版本及 package 类型，再将缓存内 nupkg 的 SHA-256 与待发布 nupkg 比对。消费者没有 `ProjectReference`。

同一消费者在以下连接上运行完整合同：嵌入式、REST、Auto、`frame-http2`。Server 是独立 NativeAOT 进程，使用随机 loopback 端口、独立数据目录与临时 token；HTTP/2 使用独立纯 HTTP/2 监听。日志和结果保留，成功或失败均清理本次启动的进程。

完整合同包含：与表 schema 不同的返回列顺序；首行前 `GetFieldType` / `GetSchemaTable`；列名、类型、可空性、主键、自增与 ROWVERSION 属性；`GetValue` 的生成值、默认值、SQL NULL；多行顺序；同步/异步 Reader、Scalar、NonQuery 与影响行数；空源结果；含中文与引号的参数值；同步/异步事务回滚；复合主键及重复键错误码、整批原子失败。

`-VerifyPublishedBaseline` 从 GitHub Release 下载官方 3.1.0 Server bundle 并核对该 Release 的 SHA-256，同时从 NuGet.org 安装官方 3.1.0 客户端。新客户端/旧 Server 与旧客户端/新 Server 分别以 REST、Auto、HTTP/2 运行，共六组；它们明确只断言旧返回值、顺序、生成值和影响行数，不冒称完整元数据兼容。

```powershell
pwsh -File eng/release.ps1 -Tasks nuget -Version 4.0.0-issue197.1 -OutputRoot artifacts/issue197/candidate

dotnet publish src/SonnetDB/SonnetDB.csproj -c Release -r win-x64 `
  -p:SonnetDbPublishAot=true -p:BuildAdminUi=false `
  -p:Version=4.0.0-issue197.1 -p:PackageVersion=4.0.0-issue197.1 `
  -o artifacts/issue197/candidate/server /warnaserror

pwsh -File eng/verify-insert-returning-package.ps1 `
  -Version 4.0.0-issue197.1 -PackageSource artifacts/issue197/candidate/nuget `
  -ServerPath artifacts/issue197/candidate/server/SonnetDB.exe `
  -OutputRoot artifacts/issue197/verification -VerifyPublishedBaseline
```

网络需要代理时可设置标准代理环境变量，并通过 `-DownloadProxy` 指定 bundle 下载代理。`-BaselineServerPath` 支持使用已解压的旧 Server，但该方式明确记录为调用方提供的二进制，调用方须另外保留官方 archive/checksum 证据；以下最终正向证据使用自动下载及校验路径。

## Windows 实测

Windows / .NET SDK `10.0.400`：正式 `eng/release.ps1 -Tasks nuget` 成功生成 **7/7** 候选包；win-x64 NativeAOT Server 发布成功，输出中 **0 个 warning / IL / AOT 警告**。消费者按上述独立缓存安装，候选与旧版消费者构建均为 0 警告、0 错误。

| 检查 | 结果 | 证据边界 |
| --- | --- | --- |
| 候选嵌入式 / REST / Auto / HTTP/2 | **4/4 PASS** | 完整 INSERT RETURNING 安装包合同 |
| 新客户端 + 官方 3.1.0 Server，三种协议 | **3/3 PASS** | 旧值/顺序/影响数；首行前 `id` 类型为 `Object`，不能依赖完整合同 |
| 官方 3.1.0 客户端 + 新 Server，三种协议 | **3/3 PASS** | 同上；新 Server 不能补齐旧客户端的声明类型合同 |
| 新消费者对官方 3.1.0 Server 强制完整验收 | **预期拒绝 PASS** | 首行前 `declared types before Read` 断言失败；进程非零退出，机器结果 `FAIL` |
| Publish workflow 静态检查 | **PASS** | `actionlint` 无诊断；不代表远程 workflow 已执行 |

归档的[正向机器结果](issue-197-package-contract-20260926/windows-result.json)包含候选包、Server、旧版 archive/Server SHA-256 和逐项结果。[负向结果](issue-197-package-contract-20260926/old-server-negative-result.json)保持 `FAIL`，因为这是门禁确实拒绝旧 Server 的预期证据。

首次兼容轮次的旧消费者 restore 曾因同 URL 的两个 NuGet source 被合并而报 `NU1100`。已改为官方旧包使用单一 source mapping，重新执行完整矩阵通过；首次失败没有计入通过结果。

## Linux 验证环境

Linux 验证在 Docker 的 Ubuntu 24.04 / .NET SDK `10.0.202` 内执行，生产源码来自同一 `63009635` 快照及锁定子模块，消费 Windows 正向验收中的同一批 nupkg。`linux-x64` NativeAOT Server 已使用 `/warnaserror` 成功发布，未产生编译或 IL/AOT 警告。

首次运行把 `OutputRoot` 放在 Windows 绑定挂载上，嵌入式数据库在 `sonnetdb.lock` 的文件锁检查处报 `IOException`，尚未执行 SQL。后续运行将数据库放到容器内部 Linux 文件系统，再导出日志和机器结果；不通过禁用数据库所有权锁绕过此环境边界。

最终 **10/10 PASS**：候选嵌入式、REST、Auto、HTTP/2 完整合同 **4/4**，新客户端/官方旧 Server 与官方旧客户端/新 Server 的旧行为兼容 **6/6**。归档的 [Linux 机器结果](issue-197-package-contract-20260926/linux-result.json)记录官方 `sonnetdb-full-3.1.0-linux-x64.tar.gz` 的下载地址、SHA-256 和独立 NativeAOT Server 哈希；两个候选 nupkg 哈希与 Windows 结果一致。

两平台合计 **20/20 组包级合同场景**通过，另有 Windows 对官方旧 Server 的一次完整合同负向对照；这不是 20 个新增 xUnit 用例，也不代表已执行远程 GitHub Actions 发布。`actionlint`、PowerShell 语法解析、修改的 C# 文件格式检查及 `git diff --check` 通过。测试启动的 Windows Server 进程和 Linux 容器均已结束。

## 发布流程变化与剩余步骤

Publish workflow 原先在 Server bundles 构建前即推送 NuGet。现在 NuGet job 只打包，两个 bundle job 下载同一批 nupkg，复用到 bundle 内并执行安装包验收；Windows/Linux 全部通过后，独立 `publish-nuget` job 才推送，GitHub Release 再等待推送成功。候选 `workflow_dispatch` 不触发公开推送。缺少 NuGet key 或任一检查失败都会阻断 tagged release，日志和 `result.json` 单独上传。

正式收口仍需将经过审查的发布变更合入、确认 4.0.0 主版本发布、从同一 `v4.0.0` tag 完成远程 Publish workflow，并复验公开 NuGet 包、Server bundle 与 Release。完成后再把候选说明改为正式版本结论、回写并关闭 #197。此记录不代替完整 MSI/Studio、生产硬件、长期稳定性或 #184 的部署验收。
