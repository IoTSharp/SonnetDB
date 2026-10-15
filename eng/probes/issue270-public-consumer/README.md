# Issue 270 公开包复现

生产修复已存在。本目录只保存独立消费者与固定官方 BookStore 快照的验收探针；不把工作树 DLL 放入消费者。

需要 Windows、PowerShell 7、.NET 10、BookStore 的 Node/npm，以及可访问 NuGet.org/GitHub 的网络。包固定为 `4.5.0-preview.ef266.5`，独立对照 EF/SQLite 固定 `10.0.12`。`NuGet.Config` 只保留 NuGet.org 并清空 fallback；三个 `Directory.*` 文件阻止导入外层仓库设置。runner 需要新的输出目录，保留数据库、日志和 SHA256 清单。

## 独立消费者

先记录当前源码与公开包的哈希，运行：

```powershell
# EvidenceRoot 使用新的短路径；不要复用已有运行目录。
$env:TEMP = 'D:/sndb-t/i270-consumer-temp'
$env:TMP = $env:TEMP
$env:NUGET_SCRATCH = 'D:/sndb-t/i270-consumer-nuget'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
New-Item -ItemType Directory -Path $env:TEMP, $env:NUGET_SCRATCH -Force | Out-Null
& ./eng/probes/issue270-public-consumer/run.ps1 -EvidenceRoot 'D:/sndb-t/i270-consumer'
```

有代理需求时，仅为该命令进程设置 `HTTP_PROXY`/`HTTPS_PROXY`。正常退出为 0，`validation-manifest.json` 应记录 102 个场景、零失败；每场景包含实际列表、Count、分页、SQL、参数和运行程序集 SHA。SonnetDB 和 SQLite 各原复现 3 项与扩展矩阵 48 项，不能把其中 SQL 命令数量称为更多测试。

## 真实 Server

`run-remote.ps1` 接受预先冻结的 Server 输出目录及逐文件 SHA256 manifest，启动前验证全部文件。manifest 形状为 `{ "productionSource": "<源码完整提交>", "files": [{ "path": "SonnetDB.dll", "sha256": "..." }] }`。应同时保存该宿主的原始构建收据，并明确它是源码宿主还是发行物。

```powershell
& ./eng/probes/issue270-public-consumer/run-remote.ps1 `
  -EvidenceRoot 'D:/sndb-t/i270-remote-evidence' `
  -ServerSnapshot 'D:/path/to/frozen-server' `
  -ServerManifest 'D:/path/to/server-snapshot.json' `
  -TemporaryRoot 'D:/sndb-t' -RestPort 43870 -FramePort 43871
```

此 runner 的代理默认使用本机 `http://127.0.0.1:7890`，本地请求绕过代理。按实际环境修改该进程环境字典即可。探针只把 `Program.cs` 中唯一的嵌入式连接表达式替换为从 `ISSUE270_CONNECTION` 读取，manifest 记录替换前后 SHA 和完整替换内容；查询与断言不变。客户端仍单独从公开源还原。REST/h2c 各执行 51 项，记录真实 Kestrel 请求协议；结束后验证 PID、创建时间、父 PID、命令行再终止所启动的宿主，不把该清理称为优雅关闭或恢复测试。

## 原官方 BookStore 作者路径

固定应用仓库 `IoTSharp/SonnetDB.Abp` 提交 `841567fd963621a8656a904121462234c6c80c0e`，其来源为 `abpframework/abp-samples` 提交 `97bc6ddfbc491843ddcd101342ce10e088c1d0d6` 的 `BookStore-Mvc-EfCore`。归档 ZIP SHA256 为 `8BB22F2FAD35EF12807C959D0A08ECC2A51090B7345AB12C73CD03BD4523AD59`。从 GitHub 固定提交 ZIP 获取全新快照，校验该 hash 后展开；不能改为浮动 main。令 `$application` 为展开后的根目录，`$probe` 为本目录绝对路径，`$evidence` 为新的 D 盘独占工作目录。

```powershell
& (Join-Path $probe 'prepare-bookstore.ps1') -SnapshotDirectory $application
$env:NUGET_PACKAGES = Join-Path $evidence 'p'
$env:NUGET_HTTP_CACHE_PATH = Join-Path $evidence 'h'
$env:DOTNET_CLI_HOME = Join-Path $evidence 'c'
$env:npm_config_cache = Join-Path $evidence 'npm'
$env:TEMP = 'D:/sndb-t/i270-bookstore-new'
$env:TMP = $env:TEMP
$env:NUGET_SCRATCH = Join-Path $env:TEMP 'nuget'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:BOOKSTORE270_EVIDENCE = Join-Path $evidence 'bookstore-results'
New-Item -ItemType Directory -Path $env:NUGET_PACKAGES, $env:NUGET_HTTP_CACHE_PATH, $env:DOTNET_CLI_HOME, $env:TEMP, $env:NUGET_SCRATCH, $env:BOOKSTORE270_EVIDENCE -Force | Out-Null
Set-Location -LiteralPath $application
$runner = './eng/Invoke-CheckedCommand.ps1'
$project = 'tests/SonnetDB.Abp.BookStore.Tests/SonnetDB.Abp.BookStore.Tests.csproj'
& $runner -Executable dotnet -Arguments @('restore', $project, '--configfile', (Join-Path $probe 'NuGet.Config'), '--packages', $env:NUGET_PACKAGES, '--no-cache', '-p:RestoreFallbackFolders=', '-p:NuGetAudit=false', '-nr:false') -Label issue270-restore -TimeoutSeconds 300 -Proxy
& $runner -Executable node -Arguments @('C:/Program Files/nodejs/node_modules/npm/bin/npm-cli.js', 'ci', '--prefix', 'samples/BookStore/src/Acme.BookStore.Web', '--no-audit', '--no-fund') -Label issue270-npm -TimeoutSeconds 300 -Proxy
& $runner -Executable node -Arguments @('eng/Copy-BookStoreAssets.cjs') -Label issue270-assets -TimeoutSeconds 120
& $runner -Executable dotnet -Arguments @('build', $project, '-c', 'Release', '--no-restore', '-p:SourceRevisionId=841567fd963621a8656a904121462234c6c80c0e', '-p:RepositoryCommit=841567fd963621a8656a904121462234c6c80c0e', '-nr:false') -Label issue270-build -TimeoutSeconds 300
& $runner -Executable dotnet -Arguments @('test', $project, '-c', 'Release', '--no-build', '--no-restore', '--filter', 'FullyQualifiedName~PublicPackage_OfficialAuthorFilters_ReturnLiteralNamesAndCountsAcrossApplicationRestart', '--logger', 'trx;LogFileName=issue270-bookstore.trx', '--results-directory', $env:BOOKSTORE270_EVIDENCE, '-nr:false') -Label issue270-test -TimeoutSeconds 240
```

`-Proxy` 使用快照 runner 的本机代理；无需代理时省略。Node 的 npm-cli 路径按实际安装位置设置。应用内部的项目引用属于原应用/适配器组成，仍须检查资产图没有跨仓库引擎项目引用，公开三个 SonnetDB 包的 source、nuspec、nupkg SHA 和实际加载 DLL 均匹配。

探针从固定快照的 `BookStoreSonnetHttpJourneyTests.cs` 保留标准 WebApplicationFactory、登录、Cookie/antiforgery 辅助方法，只新增一个单独测试文件。实际执行标准完整模型迁移、重复 seed、授权 admin 登录、五名作者创建和三种过滤的列表/Count/分页，并销毁和重建应用宿主重验。最终应为一个 xUnit 测试通过、六组过滤检查通过。JSON 包含每次 HTTP 结果、运行程序集路径/版本/SHA；这是官方应用 TestServer 旅程，不是浏览器点击、完整应用独立 Kestrel、进程崩溃或 Server 发布包验收。
