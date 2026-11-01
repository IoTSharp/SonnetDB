# 依赖升级审计（2026-11-01）

## 结论

本轮只纳入当前主版本内可以完成验证的升级。`origin/main` 已包含本次同步前的 Dependabot 合并提交，当前工作树基于该提交继续收口；没有把主版本迁移伪装成兼容补丁，也没有把 HTTP/2 环境失败写成依赖升级通过。

## 已升级并验证

- .NET/NuGet：`System.IO.Hashing`、`System.Numerics.Tensors`、Microsoft.Extensions 系列统一到 10.0.14；`Microsoft.ML.OnnxRuntime` 1.31.1；`SkiaSharp` 与 Linux native assets 4.154.0；OpenTelemetry Hosting/ASP.NET Core/HTTP/OTLP 1.20.1；`NUnit3TestAdapter` 6.3.1；`Qdrant.Client` 1.20.0；`StackExchange.Redis` 3.4.29；`AWSSDK.S3` 4.0.104.8；NATS Core/JetStream 3.3.2；`MongoDB.Driver` 3.13.0。
- Web：axios 1.21.0、maplibre-gl 6.16.0、naive-ui 2.45.3、Vue 3.5.44、Playwright 1.64.1，并更新 `web/package-lock.json`。
- VS Code 扩展：`@types/node` 22.19.0、`@types/vscode` 1.144.0，并更新 `extensions/sonnetdb-vscode/package-lock.json`。

## 明确延期的主版本迁移

- NUnit 5：仓库仍使用 NUnit 3 的 `CollectionAssert` 等 API，必须单独迁移测试代码。
- `Microsoft.CodeAnalysis.CSharp` 5.9：仓库固定 Roslyn 5.3 以避免 SDK source generator 的 `CS9057`，需单独验证生成器和 SDK 矩阵。
- Vite 8、`@vitejs/plugin-vue` 6、TypeScript 7：属于前端工具链主版本迁移；`vue-tsc` 3.3.14 已是当前 `web/package.json` 的基线，本轮只修复其锁文件漂移。
- Pinia 4、Vue Router 5、marked 18：属于前端运行时/API 主版本迁移。
- VS Code `@vscode/vsce` 4 与 Node 类型 26：属于扩展打包工具链迁移。
- `OpenTelemetry.Exporter.Prometheus.AspNetCore` 没有稳定版，不能把 beta 版本升级写成稳定依赖完成。

## 验证记录

- `dotnet restore SonnetDB.slnx`：通过。
- Release solution build（warnings as errors）：通过，0 warning / 0 error。
- `SonnetDB.Core.Tests`：7,995/7,995 通过。
- Client 非 HTTP/2：213/213 通过。
- `SonnetDB.Native.Tests`：12/12 通过；`SonnetDB.CAP.Tests`：42/42 通过；`SonnetDB.EntityFrameworkCore.Tests`：295/295 通过。
- Web build：通过；Web contracts：472/472 通过。
- VS Code extension：59/59 通过。
- 全量测试中 HTTP/2/h2c 的 `RequestVersionExact` 失败仍是当前机器无法建立 HTTP/2 连接的环境门禁，不能归因于本次依赖升级，也不能作为正式发布通过证据。
- `Cloud.Unum.USearch` 2.26.6 已随远端 Dependabot PR 合入；当前 Windows 专项 7/7 通过，但仓库既有记录曾报告 linux-x64 的 SIGILL 风险，未取得 Linux 原生探针前不把跨平台发布安全性视为已证明。

## 发布边界

本审计支持提交依赖升级候选，不代表正式版本已经可发布。发布前仍需在具备 HTTP/2 的 CI/真实服务环境重新取得完整 light/full 与 scheduled 证据，并复核远端 PR、CodeQL、Dependabot alerts 及发布包安装合同。

## 2026-11-02 后续安全迁移

- 上述依赖候选已随 PR #384 合入 `main`（`ad8f7a01`）。该 PR 将 `@types/vscode` 声明改为 `^1.100.0`，但 caret 范围仍允许锁定 1.144.0；本 PR 评审据此指出原审计把声明范围当成实际安装版本。本次将声明及锁文件都固定为精确 `1.100.0`，用最低宿主的 API 类型重新验证。
- 本次独立修复将 `@vscode/vsce` 从 3.9.2 升至 4.1.0，并将间接依赖 `minimatch` 显式固定为 11.0.3。vsce 4 使用 tinyglobby，移除存在 GHSA-3g3m-2fjr-6pw4 的 glob；override 修复 GHSA-g3hv-75wg-m249。只更新 minimatch 会留下 glob 告警，故前文延期的 vsce 4 在本次单独迁移。
- vsce 4 要求 Node.js >=22，符合扩展现有开发文档与 CI 的 Node 22 基线。本机验证使用 Node 24.15.0 / npm 11.12.1；不能据此替代 CI Node 22 的结果。扩展 `engines.vscode` 仍为 `^1.100.0`，编译类型固定为 `@types/vscode` 1.100.0。
- `npm ci --ignore-scripts`、59/59 单元测试及实际 `npm run package:vsix` 均通过；VSIX 包含 language server 及其运行时依赖，共 41 个文件、约 6.89 MB。制品仅留在忽略目录，未发布至 Marketplace。
- `npm run test:host` 在真实 VS Code 1.100.3（最低宿主系列）通过，扩展已加载且宿主退出 0。
- `npm audit --json --registry=https://registry.npmjs.org` 退出 0，报告 0 个漏洞；`npm ls minimatch glob @vscode/vsce --json` 确认 vsce 4.1.0、minimatch 11.0.3 且无 glob。默认 npmmirror 的审计端点返回 404 NOT_IMPLEMENTED，不能作为审计通过证据。
- GitHub API 已通过本机 `127.0.0.1:7890` 代理恢复，现有 keyring 登录有效，无须重新提供 token。代理仅按进程传入，不是全局配置；正式发布仍须复核最终提交的 CI、CodeQL、远端安全告警、完整 HTTP/2/parity 与包安装门禁。
