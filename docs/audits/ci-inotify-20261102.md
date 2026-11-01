# Linux CI 测试宿主监听器耗尽（2026-11-02）

## 失败证据

`main` 提交 `ad8f7a0181c8d2bba8a6472f339bc4bafadc8763` 的 CI run `41339384277` 中，Ubuntu job `124945958761` 构建成功，但测试失败。EF 项目为 273 PASS / 22 FAIL，Server 项目为 603 PASS / 949 FAIL；失败集中在 `WebApplication.CreateSlimBuilder` 创建 JSON 配置文件监听器时，错误为用户 inotify 实例达到 128 上限。

同一 run 的 Core 7,995/7,995、Client 219/219、两个 Windows 分片、Format Check 和三个平台的 AOT 发布均通过。不能把这批宿主构建失败解释成 971 个业务断言同时回归，也不能据其它分片通过而忽略 Linux 门禁。

## 修复范围

Server、EF 和 Accuracy 三个测试宿主 helper 都先生成独立临时目录及 appsettings，然后创建真实 Kestrel；这些测试不会在启动后改写配置文件。本次为 helper 的默认命令行增加 `--hostBuilder:reloadConfigOnChange=false`，在创建配置 provider 前关闭文件监听。显式 `extraArgs` 仍可覆盖默认参数。

此参数由 ASP.NET Core 10 的 `WebApplicationBuilder.ApplyDefaultAppConfigurationSlim` 读取，控制默认 JSON 配置的 `reloadOnChange`。依据为 dotnet/aspnetcore `v10.0.0` 的 `src/DefaultBuilder/src/WebApplicationBuilder.cs`，不是创建宿主后才移除已经分配的 provider。

产品启动入口、默认热加载、Kestrel 和请求协议不改。没有提高 runner 的 inotify 上限、跳过失败测试、减少测试集合或放宽断言。helper 以外的配置相关测试保留原行为。

## 验证与边界

- 本机 EF 全套：295/295 PASS，包含真实 REST 与 HTTP/2 场景。
- Server 定向 `CopilotChatEndpointTests`、`RemoteAdoEndToEndTests`、`SqlExecutionPreviewEndpointTests`：130/130 PASS。
- Accuracy 全套：9/9 PASS。
- 本机验证显式设置 `NO_PROXY=localhost,127.0.0.1,::1`，真实本地 HTTP/2 此次通过；旧会话的 `RequestVersionExact` 环境失败不是本次结果。
- Linux inotify 问题的完整修复以新 PR 的 Ubuntu 全量 CI 为准；Windows 定向验证不能替代该结果。完整 parity/light/full、7 天 scheduled、正式发行包安装仍是独立门禁。
- 测试 TRX 保存在本机忽略目录 `artifacts/ci-watchers-20261102/`，未提交制品。

## 后续完整验证

- 本机 Server 全套 1,552/1,552 PASS，0 FAIL / 0 SKIP，持续约 2 分 50 秒。
- PR #386 的提交 `40f3b186c24c9af940ec6453acf7dc38313012be`，CI run `41342898839` 的 Ubuntu `Build & Test` 已成功，三平台 AOT 与 Format Check 也成功。这是包含全部 Linux 测试集合的实际收据，原 inotify 阻塞不再是该提交的失败项。
- 记录时两个 Windows 分片和 CodeQL 仍在运行；尚未合并 PR，最终状态需回读，不能把本段扩展为所有发行门禁已通过。
