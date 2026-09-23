# M27 #340 残余审计（2026-09-21，2026-09-23 更新）

本审计把 M27 #340 的真实代码状态与部署/现场证据分开记录。它不把已有能力重新包装成实现任务，也不把缺少真实公网、固定设备或安装环境的证据误写成代码缺口。

## 已实现且有本地回归的范围

| 范围 | 代码证据 | 回归证据 | 判定 |
|---|---|---|---|
| ServerRelay 稳定 envelope、run/cursor/sequence/toolCallId 与终态约束 | `src/SonnetDB/Endpoints/Handlers/CopilotServerRelayRunStore.cs`、`src/SonnetDB/Endpoints/Handlers/CopilotChatEndpointHandler.cs` | `CopilotChatEndpointTests`、`CopilotServerRelayContractTests` | 已实现 |
| 完成 run 的跨进程/重启重放 | `CopilotServerRelayRunStore` 在 `<DataRoot>/.system/copilot-relay-journal.json` 使用 source-generated JSON、临时文件原子替换和 `.lock` 单写者锁；启动加载 `RestoreCompleted` | `ServerRelayRunStore_PersistsCompletedRunForRestartReplay` | 已实现，重放窗口受 10 分钟 TTL 与 64 个 replay run 上限约束 |
| 未完成 run 的重启 fail-closed | 启动加载 `RestoreInterrupted`，只追加稳定 `error`/`done`，不重新调用 provider/工具 | `ServerRelayRunStore_RestartSealsUnfinishedRunWithoutProviderContinuation` | 已实现；不宣称实时接管 |
| bounded replay 与身份隔离 | active/replay/tombstone 上限分别为 64/64/2048；binding 绑定 owner、database、request fingerprint | `CopilotChatEndpointTests` 中容量、owner 隔离、冲突、cursor 与过期回归 | 已实现 |
| Web 页面刷新重放 | `CopilotDock` 仅在 ServerRelay 模式保存受限 pending marker；刷新后按 session/database/fingerprint 校验，省略 cursor 从 sequence 1 重建完整 journal；不保存 token、正文或工具结果 | `web/e2e/copilot-runtime.spec.ts` pending marker/fingerprint 回归；`npm run build` 通过 | 已实现；本机 Server 重启 smoke 已单列记录，部署后的浏览器刷新联调仍后置 |
| 本机 Server 进程切换与 journal 重放 smoke（2026-09-23，未归档） | `tests/SonnetDB.Tests/Copilot/scripts/test-m27-server-relay-restart.ps1` 启动两个真实 Server 进程，使用普通临时数据库和 loopback provider | PowerShell 7 Release smoke：两次 PID 不同，事件严格为 `start,retrieval,final,done`，两次 sequence 均为 `1,2,3,4`，重放 payload/final answer 与首轮一致，provider `calls=2`（planner/answer 各一次） | `LOCAL_ONLY`；浏览器刷新、公网、OAuth/PKCE、StudioNative 和真实 provider 未覆盖 |

本机定向测试命令及结果：

```text
dotnet test tests/SonnetDB.Tests/SonnetDB.Tests.csproj --no-restore --filter "FullyQualifiedName~CopilotChatEndpointTests|FullyQualifiedName~CopilotServerRelayContractTests|FullyQualifiedName~CopilotInfrastructureTests"
通过 73，失败 0，跳过 0（2026-09-21）
```

### 2026-09-23 本机 smoke 复现记录（未归档）

- 基线：`HEAD 5a0cb0b4799c6e87d4bc3db9180eb29594524a9d`；本次工作树为 dirty，包含本审计所述脚本、成本规划器、测试和文档改动。
- 命令：`& 'C:\Program Files\PowerShell\7\pwsh.exe' -NoLogo -NoProfile -File tests/SonnetDB.Tests/Copilot/scripts/test-m27-server-relay-restart.ps1 -ServerDll src/SonnetDB/bin/Release/net10.0/SonnetDB.dll -TimeoutSeconds 60`
- 配置：真实 `SonnetDB.dll` Release 进程；HTTP、provider 均绑定 `127.0.0.1`；数据库为临时 `m27_restart_db`；provider 是脚本内 deterministic loopback mock；MQTT/CoAP/UDP/Modbus 关闭；脚本不使用浏览器或 OAuth。
- 观察（2026-09-23 12:09 +08:00）：`status=LOCAL_ONLY`、`serverRestart=PASS_LOCAL_ONLY`、`browserRefresh=NOT_RUN`；Server/provider loopback 端口为 `60637/60243`，首轮/重放事件均为 `start,retrieval,final,done`，sequence 均为 `1,2,3,4`，首轮 journal `completed=true`，重放 payload/final answer 与首轮一致，provider `calls=2/plannerCalls=1/answerCalls=1`，两次 Server PID 不同（本次分别为 `71840`、`51232`），Server 直接派生的 `conhost.exe` 也完成身份核对和回收。
- 证据边界：Release DLL SHA-256 为 `C5F412D57FAB6FD48F8551F4BBBCD966286AEB40BE6E3BC395ADB4890282D6E9`，smoke 脚本 SHA-256 为 `80194C2E4C572389AC7EEF3F0B493AEF1FED45887504441E3EFF4243A49BA6B6`；原始 stdout 未保留为仓库 artifact，机器硬件、磁盘和资源指标也未采集。因此该记录只是可复现的本机未归档 smoke，不满足固定硬件或发布证据门禁。

## 2026-09-23 BrowserDirect OAuth/PKCE 获取入口（已完成）

本轮已补齐可信配置到页面入口的 Authorization Code + PKCE S256 实现，状态为
`COMPLETED_LOCAL_CONTRACT`；生产构建通过，Web 完整套件 148 通过、2 项既有真实 KV 测试跳过，完整请求头强化后 OAuth 12/12 复验通过。命令、产物与证据边界见[验收报告](m27-browser-oauth-closure-20260923.md)。
此前“没有 authorization endpoint、verifier/challenge、redirect 校验或 token exchange”的描述已不适用。

| 已落地范围 | 代码证据 | 验收边界 |
|---|---|---|
| 可信 HTTPS 公共客户端、PKCE S256、随机一次性 state 与 RFC 9207 `iss` 校验 | `web/src/copilot/browserDirectOAuth.ts` | 显式批准 issuer/authorization/token origins；固定同源 `/admin/copilot/oauth/callback`，不接受 query/hash 配置；真实 IdP 尚待部署验证。 |
| popup source/origin、callback 清 query、token exchange 与迟到响应拒绝 | `browserDirectOAuth.ts`、`web/src/views/CopilotOAuthCallbackView.vue`、`web/src/router/index.ts` | callback 独立匿名放行，不触发数据库 setup/login；IdP CORS、宿主 CSP 与 COOP 必须在实际部署验证。 |
| 页面连接/断开/取消、到期、身份变化和销毁清理 | `web/src/components/CopilotDock.vue`、`web/src/copilot/browserDirectEntry.ts` | 配置不足只显示“尚未配置”；access token 仅在内存，UI 只见 expiry；数据库 token 只作本地同值拒绝，不进入 OAuth 请求。 |
| 资源和生命周期边界 | `browserDirectOAuth.ts` | 交易 5 分钟、exchange 30 秒、token 最长 2 小时、响应最多 64 KiB/1024 分片；不请求/保存/使用 refresh token，不支持 client secret、Device Flow 或 implicit grant。 |

公开环境变量表、IdP 注册与部署前提见 [Copilot Provider 的 BrowserDirect OAuth/PKCE 登录](../copilot-providers.md#browserdirect-oauthpkce-登录)。
回归入口为 `web/e2e/copilot-oauth-core.spec.ts`、`web/e2e/copilot-oauth.spec.ts`；受控 IdP/HTTPS fixture
只证明对应协议和浏览器合同。测试报告必须分别写明是否实际覆盖 CopilotDock 按钮、生产 callback 页面、
真实 IdP、生产 CSP/CORS/COOP 和双网，不能把一类结果提升为另一类证据。

现有内存凭据清理已贯穿授权客户端：取消交易、断开或退出后，迟到 token 响应必须 fail closed；
数据库 token/tokenId/用户名/权限身份变化清理原授权，模式切离与组件销毁也不保留凭据。刷新页面要重新
授权，这不是 BrowserDirect 自动续流或持久登录能力。Web 公共客户端入口不构成 StudioNative broker、
系统凭据库或外部宿主 OAuth 的交付，M27 #340 仍保留下面的代码与现场边界。

## 2026-09-23 StudioNative 宿主通道

StudioNative transport、六个固定 broker 操作、Windows Credential Manager、原生短期 token 输入与 Dock 生命周期已实现。宿主完整测试 56/56，Native 浏览器/共享协议专项 26/26，生产构建通过；配置及准确边界见[合同](../studio-copilot-contract.md)和[本轮验收](m27-studio-native-closure-20260923.md)。缺少宿主、批准目标或有效凭据时仍保持不可用。此项不再列为缺失代码；真实原生窗口、WebView2、provider 与双网部署仍需现场证据。

## 仍明确未实现的代码/产品边界

正在执行中的多实例实时接管与高可用共享 session。journal 使用文件锁合并完成快照；新进程不会接管旧进程仍在执行的 provider/工具，符合当前 fail-closed 合同。升级为实时接管需要 provider lease、所有权租约、取消转移、跨实例事件订阅和故障注入，不能以已完成重放替代。

## 下一项可执行切片

优先级仍为 M27 #340。BrowserDirect OAuth/PKCE 获取入口与 StudioNative 本地宿主合同已完成实现和专项验收；后续推进多实例实时接管，以及真实 IdP/双网/部署后浏览器刷新和 StudioNative 现场旅程，不重复派已完成代码。页面刷新重放和本机 Server 进程切换 smoke 已按以下合同落地：

- 仅 `ServerRelay` 模式允许自动恢复；`BrowserDirect`、`StudioNative` 和 `Disabled` 不共享该状态。
- 只在发送请求前保存受限的 `runId`、会话 ID、数据库名和请求 fingerprint；不保存数据库 Bearer、public token、消息正文或工具结果。
- 刷新后使用相同 session/database/messages 和原 `runId`，省略 cursor 请求完整有界 journal，让客户端状态机从 sequence 1 重建；不能从中段 cursor 直接跳入，否则工具调用上下文不完整。
- 恢复成功必须再次看到唯一 `final/error` 与 `done`；`relay_run_unknown`、`relay_run_expired`、binding conflict 或任意 fingerprint 不一致都清理 pending 状态并 fail closed。
- 仅在完整 `done` 后删除 pending marker；停止、登出、会话切换/删除和组件卸载清理 marker，避免旧请求在新会话中重放。

现有自动化已覆盖存储字段边界、assistant 追加后 fingerprint 重建和 build；本机真实 Server 进程切换与 `.system` journal 重放已有 `LOCAL_ONLY` smoke，仍需部署后的浏览器刷新联调确认 Web host/auth 行为。该历史 smoke 不覆盖本轮 OAuth/PKCE 入口，也不覆盖 StudioNative broker。

## 现场/发布证据（不计作代码缺口）

真实 IdP 注册及 RFC 9207 `iss`、token endpoint CORS、popup COOP、真实双网联调、公网 continuation/CSP/CORS、无公网出口的本地旅程、Studio 干净安装/WebView2、真实 provider 质量/成本以及固定硬件/长期窗口仍按 M27 和真机验证待办执行。它们不能由上述 73 个历史本地测试或本轮受控 OAuth fixture 替代；真实环境尚未执行的项目保持 `NOT_READY`/`DEFERRED`，不能写为 `PASS`。
