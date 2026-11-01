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
