# M36 KV 远程原子切片验证

记录日期：2026-09-05。基线 `e46f158ebd2e46623ce2ed2af46dd5e80c9e370a`，结果来自验证时该基线上的未提交工作区，gap catalog 的 `latestLocalValidation.workingTree` 记录的是这一验证时状态。对应 M36 #316 / KV-001 及 #310/#311 的 KV 子集。验证阶段没有 commit、push、PR、远程 dispatch、部署、包发布或长期自动化；后续提交与推送由用户另行授权，不改变测试运行时的工作区状态。既有历史审计保留原结论，本文追加范围明确的新证据。

## 实现与用户旅程

- Core 保留旧签名，增加可取消 Set/GetAndSet/GetAndDelete/CAS/ExpireAt/Persist 重载。锁/背压等待检查取消，第一次 WAL append 后完成提交路径；相关 append/sync 不确定时拒绝继续写入。
- REST、Frame、SDK 直接调用相同 Core 原子语义：NX/XX、TTL、CAS、旧值/版本、空值与缺失。Frame 只追加 opcode，不更改旧帧或存储格式；所有生产 JSON 保持 source generation。
- SDK 拒绝坏响应；原子 Frame 写失败不转 REST 重发；KV 独立连接池策略禁用 307/308 跳转，其他客户端默认行为不变。
- Web 复用既有审批，绑定目标/连接/凭据并隔离旧响应，停止后续批次，保留部分成功与未知结果。新原子响应用十进制版本字符串避免 JavaScript 精度丢失，旧服务器的不安全数字不伪装成精确版本。
- [合同和约 20 行成功样例](../kv-atomic-contract.md)；[可运行 JSON/CAS/取消/重开样例](../../samples/SonnetDB.KvQuickstart/README.md)。复用同一程序切换 embedded/rest/frame-http2/auto，无每协议手写业务流程。

## 本地证据

工具：PowerShell 7.6.5、.NET SDK 10.0.400、Windows x64；受控构建 `DOTNET_PROCESSOR_COUNT=4`、`-m:1`、禁用 MSBuild node reuse/shared compiler。所有命令有期限，任务自有宿主和数据目录按归属回收。未安装编译器或浏览器；使用已安装 Chrome。两个缺失子模块仅初始化到仓库固定提交，gitlink 未变。

| 层次 | 执行结果 | 覆盖和限制 |
| --- | --- | --- |
| 完整 Core suite | 4141/4141 PASS，0 skipped，166 秒 | 当时全部 Core 回归；在后续三路径 sync 故障补丁、严格 REST 响应和 redirect 修复之前执行。不可称为最终全量 suite。 |
| 最终 Core 定向 | 134/134 PASS，0 skipped | 47 取消/存储故障、40 独立 wire codec、21 handler 传输故障、18 真实 loopback REST 响应、7 真实 loopback redirect/凭据隔离及1既有 factory 回归。先以1个小请求验证有界 loopback helper。 |
| Server 定向 | 140/140 PASS，0 skipped | 35 同 fixture embedded/rest/HTTP2/auto 原子旅程、20原始 HTTP/Frame 错误合同及85既有 KV/Frame/MCP 相关测试；严格 REST/redirect/sync 修复后复跑。随后只增加 Frame 响应保留标志拒绝，由最终 Core/native 验证。 |
| Web setup 测试 | 12/12 PASS | 加载实际 Vue setup 和 api/kv.ts，只 mock HTTP；涵盖目标/凭据/取消/部分失败、>2^53 版本、刷新不覆盖编辑内容。不是浏览器或真实 Server 测试。 |
| Web 构建 | PASS | vue-tsc + Vite；存在既有 >500 kB chunk 提示，没有类型错误。 |
| Native AOT 发布 | Server 与 KV Quickstart win-x64 PASS | 实际发布路径，0 IL/AOT warning；运行结果另列，不能把发布成功当作运行成功。 |
| 原生远程运行 | REST/HTTP2 Frame/auto 各写入+重开 PASS，共6次客户端进程 | 真实 SonnetDB.exe + Quickstart.exe，Server 强制进程终止后从同一目录启动；逐库核对值、精确 version、100 ns UTC expiry ticks、删除和重复取删。共回收2个Server/6个客户端进程。 |
| 原生嵌入式运行 | 写入+重开 PASS，共2次客户端进程 | 同一原生样例与独占目录，核对值/版本/TTL/删除；这是正常客户端退出后的重开。 |
| 真实浏览器 | 2/2 PASS，0 skipped | 已安装 Chrome，1600x1000 与390x844，直接连原生 Server 的构建后管理界面，无HTTP mocks；审批/NX成败/空旧值交换/重复取删、响应与权威状态核对，手机命令与结果标签不重叠。 |

本机日志/TRX 位于 `artifacts/kv-remote-20260905/`：`results/core-kv-final.trx`、`results/kv-boundaries-verified.trx`、`results/server-kv-current.trx`、`kv-web-complete.stdout.log`、`web-build-complete.stdout.log`、`server-native-publish.stdout.log`、`kv-native-publish-verified.stdout.log`、`native-kv-{0,1}-{rest,frame-http2,auto}.stdout.log`、`native-kv-embedded-{0,1}.stdout.log`、`kv-browser-real.stdout.log`。这是本地忽略产物，不作为已提交远程 CI artifact。

浏览器截图在 `output/playwright/kv-atomic-real/` 的 desktop/mobile 子目录，每种视口保留 NX 审批、空旧值交换结果、重复取删结果。首次浏览器尝试停在新实例安装向导，尚未执行 KV；随后运行器在独占数据目录完成真实初始化再执行，不 mock setup。真实测试发现并修复了手机表单最小宽度、隐藏结果入口、结果头重叠和写后刷新覆盖编辑框的问题。此前构建因缺子模块失败、两条测试可空类型编译错误，以及两条首帧错误的 HTTP 200 错误预期均已修正；失败尝试不计 PASS。

可复现入口：

```powershell
dotnet test tests/SonnetDB.Core.Tests -c Release --filter 'FullyQualifiedName~KvAtomic|FullyQualifiedName~KvRedirect|FullyQualifiedName~RemoteHttpClientFactoryTests'
dotnet test tests/SonnetDB.Tests -c Release -p:BuildAdminUi=false --filter 'FullyQualifiedName~KvAtomic|FullyQualifiedName~KeyValue|FullyQualifiedName~KvEndpoint|FullyQualifiedName~FrameEndpoint|FullyQualifiedName~Mcp'
dotnet publish src/SonnetDB -c Release -r win-x64 -p:SonnetDbPublishAot=true -p:BuildAdminUi=false
dotnet publish samples/SonnetDB.KvQuickstart -c Release -r win-x64 -p:KvSamplePublishAot=true
```

Web 的真实规范为 `web/e2e/kv-atomic-real.spec.ts`，需要同时设置 `SONNETDB_KV_REAL_BASE_URL`、`SONNETDB_KV_REAL_TOKEN`、`SONNETDB_KV_REAL_DATABASE`，指向已完成初始化的独占测试实例和数据库，测试结束由启动者清理；默认套件未设置这些变量时跳过，跳过不算真实 PASS。本机有界运行器在同一 artifacts 目录，包含进程身份、期限和清理记录。

## 远程 CI 只读观察

最新 [Parity 33950712561](https://github.com/IoTSharp/SonnetDB/actions/runs/33950712561) 仍绑定修复前 `3b5ff768`。light/full 都在 MCP 启动时因 `McpDatabaseListResult` metadata 缺失而失败：`stack_start_failed`，0 scenarios，空 suites。最新七次 scheduled 全部失败，尚未观察到修复后 Parity。工作流只接受 schedule/dispatch，push 不会自动触发它。

修复后基线的 [main CI 33958714577](https://github.com/IoTSharp/SonnetDB/actions/runs/33958714577) 中 Ubuntu/Windows Restore、Build、TRX upload 通过，但 Test 失败；公开页面未提供失败测试名，不能归因为 MCP。本次未取得鉴权日志：直连超时，通过临时代理执行的鉴权 gh 读取被自动审批以 `blocked by policy` 拒绝，不能据此断言凭据失效。完整只读记录在本地 `artifacts/kv-remote-20260905/ci-audit/read-only-evidence.md`。

## 未完成范围

M20 修复后的 light/full、七天 scheduled；M36 #310/#311 的其他模型及九模型备份/宿主旅程；KV #317 大 keyspace cursor/pipeline/诊断；故障注入后的真实远程 I/O 响应与磁盘满/断电；固定硬件性能、ARM64、安装包/WebView2、生产门禁均未由本证据关闭。Core I/O hook 是故障注入，Vue setup 的 HTTP mock 是 mock，真实 Kestrel/原生进程/浏览器单独记录，不混称生产验证。

下一功能切片为 M36 #323 / OBJECT-001 的有界对象分页，验收原始 key ordinal、Unicode/编码 key、版本/删除标记、delimiter 和 continuation；再接 #322 Transfer Manager。不能以大 bucket 每页全量扫描的现有列表实现冒充有界传输地基。
