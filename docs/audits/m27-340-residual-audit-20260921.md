# M27 #340 残余审计（2026-09-21）

本审计把 M27 #340 的真实代码状态与部署/现场证据分开记录。它不把已有能力重新包装成实现任务，也不把缺少真实公网、固定设备或安装环境的证据误写成代码缺口。

## 已实现且有本地回归的范围

| 范围 | 代码证据 | 回归证据 | 判定 |
|---|---|---|---|
| ServerRelay 稳定 envelope、run/cursor/sequence/toolCallId 与终态约束 | `src/SonnetDB/Endpoints/Handlers/CopilotServerRelayRunStore.cs`、`src/SonnetDB/Endpoints/Handlers/CopilotChatEndpointHandler.cs` | `CopilotChatEndpointTests`、`CopilotServerRelayContractTests` | 已实现 |
| 完成 run 的跨进程/重启重放 | `CopilotServerRelayRunStore` 在 `<DataRoot>/.system/copilot-relay-journal.json` 使用 source-generated JSON、临时文件原子替换和 `.lock` 单写者锁；启动加载 `RestoreCompleted` | `ServerRelayRunStore_PersistsCompletedRunForRestartReplay` | 已实现，重放窗口受 10 分钟 TTL 与 64 个 replay run 上限约束 |
| 未完成 run 的重启 fail-closed | 启动加载 `RestoreInterrupted`，只追加稳定 `error`/`done`，不重新调用 provider/工具 | `ServerRelayRunStore_RestartSealsUnfinishedRunWithoutProviderContinuation` | 已实现；不宣称实时接管 |
| bounded replay 与身份隔离 | active/replay/tombstone 上限分别为 64/64/2048；binding 绑定 owner、database、request fingerprint | `CopilotChatEndpointTests` 中容量、owner 隔离、冲突、cursor 与过期回归 | 已实现 |
| Web 页面刷新重放 | `CopilotDock` 仅在 ServerRelay 模式保存受限 pending marker；刷新后按 session/database/fingerprint 校验，省略 cursor 从 sequence 1 重建完整 journal；不保存 token、正文或工具结果 | `web/e2e/copilot-runtime.spec.ts` pending marker/fingerprint 回归；`npm run build` 通过 | 已实现，真实 Server 重启/浏览器刷新联调仍后置 |

本机定向测试命令及结果：

```text
dotnet test tests/SonnetDB.Tests/SonnetDB.Tests.csproj --no-restore \
  --filter "FullyQualifiedName~CopilotChatEndpointTests|FullyQualifiedName~CopilotServerRelayContractTests|FullyQualifiedName~CopilotInfrastructureTests"
通过 73，失败 0，跳过 0（2026-09-21）
```

## 仍明确未实现的代码/产品边界

这些不是本轮适合盲补的“小修复”，因为每项都需要外部身份、部署拓扑或桌面宿主合同；当前代码应继续 fail closed：

1. 可信 OAuth/OIDC Device Flow 或 PKCE 获取入口。仓库只有 Cloud Token/refresh-token 配置和现有登录态，没有 authorization endpoint、code verifier/challenge、redirect 校验或 token exchange。相关文档应保持 `NOT_READY`，不能把内存 token provider 或模拟公网 endpoint 当成 OAuth 证据。
2. StudioNative Copilot transport / AI broker / 系统凭据库。`web/src/api/studioNativeBridge.ts` 只提供 loopback bridge 的 manifest、文件、连接库和 managed-server 操作；`StudioNative` runtime 尚未注册 transport。现有握手已将 endpoint/token 从 URL、query 和 storage 移除，这属于安全前置，不等于 AI broker 已交付。
3. 页面刷新后的自动续流现已补齐最小安全切片：Web 只在 ServerRelay 保存 `runId`、session/database、请求 fingerprint 及非敏感模式元数据，刷新后加载服务端会话并从 sequence 1 重放；fingerprint 不一致、run unknown/expired/conflict 或不支持安全 SHA-256 时清理 marker 并 fail closed。该切片不保存正文、Bearer/public token 或工具结果，也不覆盖 BrowserDirect/StudioNative。
4. 正在执行中的多实例实时接管与高可用共享 session。journal 使用文件锁合并完成快照；新进程不会接管旧进程仍在执行的 provider/工具，符合当前 fail-closed 合同。把它升级为实时接管需要 provider lease、所有权租约、取消转移、跨实例事件订阅和故障注入，不应在本切片中猜测实现。

## 下一项可执行切片

优先级仍为 M27 #340。页面刷新重放已经按以下合同落地；下一步应单独拆真实 Server 重启/浏览器刷新联调证据，再进入 OAuth/PKCE 或 StudioNative broker：

- 仅 `ServerRelay` 模式允许自动恢复；`BrowserDirect`、`StudioNative` 和 `Disabled` 不共享该状态。
- 只在发送请求前保存受限的 `runId`、会话 ID、数据库名和请求 fingerprint；不保存数据库 Bearer、public token、消息正文或工具结果。
- 刷新后使用相同 session/database/messages 和原 `runId`，省略 cursor 请求完整有界 journal，让客户端状态机从 sequence 1 重建；不能从中段 cursor 直接跳入，否则工具调用上下文不完整。
- 恢复成功必须再次看到唯一 `final/error` 与 `done`；`relay_run_unknown`、`relay_run_expired`、binding conflict 或任意 fingerprint 不一致都清理 pending 状态并 fail closed。
- 仅在完整 `done` 后删除 pending marker；停止、登出、会话切换/删除和组件卸载清理 marker，避免旧请求在新会话中重放。

现有自动化已覆盖存储字段边界、assistant 追加后 fingerprint 重建和 build；仍需一次真实 Server 重启/浏览器刷新回归，确认 `.system` journal 在进程切换后可重放。OAuth/PKCE 和 StudioNative broker 不应与该现场证据混做。

## 现场/发布证据（不计作代码缺口）

真实双网联调、公网 continuation/CSP/CORS、无公网出口的本地旅程、Studio 干净安装/WebView2、真实 provider 质量/成本以及固定硬件/长期窗口仍按 M27 和真机验证待办执行。它们不能由上述 73 个本地测试替代，也不应把 `PASS` 写入当前路线图。
