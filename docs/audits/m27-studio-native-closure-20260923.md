# M27 #340 StudioNative 宿主凭据与聊天通道验收

**状态：✅ `COMPLETED_LOCAL_CONTRACT`。** Windows Studio 的原生 AI broker、系统凭据库、前端 transport、连接界面和 typed MCP continuation 已完成实现、专项验收及完整 Web 回归。宿主测试 56/56、StudioNative 与共享协议专项 26/26、主 Web 套件 161 项通过，生产 Web 构建通过。不会将本地合同扩大为真实公网或整个 M27 已完成。

## 交付范围

- 宿主 manifest 发布 `copilot.nativeBroker.v1`。六个固定操作沿用精确 Origin 与 bridge header token 校验，拒绝查询参数、入站 Authorization、控制操作 body 和任意代理目标。公网只能使用宿主明确批准的 HTTPS 地址，禁止重定向和 cookie。
- 原生密码窗口输入已有短期公网 runtime token，readiness 验证后写入 Windows Credential Manager。凭据目标按完整提供方 base URL 隔离；状态只返回公开配置和到期时间。未配置、缺凭据或过期保持不可用，不回退到其他 runtime。
- Web 注册 StudioNative transport，生产 Dock 支持连接、取消、断开、停止、到期与身份切换清理。迟到 bootstrap、授权成功或 readiness 响应均不能恢复已清理的连接。缺失权限模式时宿主显式补为 `read-only`，不依赖公网部署默认值。
- 聊天首段、只读本地 typed MCP、逐字工具结果回显和 continuation 使用共享的有界协议。数据库 token 只到本地 MCP；公网 token 只在宿主请求中使用，页面不持有该 token。
- 修复完整回答同时出现在会话和临时流缓存中的重复显示。重放 JSON 改为构建结构后只序列化一次，消除嵌套转义膨胀；数值规范化采用线性尾部扫描，避免长零串正则回溯。保留大整数与重复键的精确比较，首次 MCP 调用前拒绝会被 JavaScript JSON 解析改写的参数。

配置、固定路由、资源限制与操作流程见 [StudioNative 合同](../studio-copilot-contract.md)。这是宿主输入已有 token，未实现桌面 OAuth 获取或 refresh-token 生命周期。

## 最终运行记录

环境：Windows 11 x64、PowerShell 7.6.6、.NET SDK 10.0.401、Node.js 24.15.0、Playwright 1.55.1、已安装 Google Chrome 150.0.7871.115。没有安装新依赖、下载浏览器或新增运行时 NuGet 包。构建/测试串行，关闭 MSBuild/编译器常驻服务，外层每次命令最多 600 秒。

| 验证 | 结果 | 最终证据 |
|---|---|---|
| 完整 Studio Release 测试 | 56/56，0 失败/跳过；其中新增 39 项 | `studio-host-verified.trx`、同名前缀构建日志及进程记录 |
| StudioNative 界面与共享协议 | 26/26，0 失败/跳过/重试；13 项真实浏览器界面、13 项协议边界 | `studio-native-accepted.json`、`.stdout.log`、`.processes.json` |
| 生产 Web 构建，含 vue-tsc | PASS；保留既有 Vite 大 chunk 提示 | `studio-web-build-final.stdout.log` / `.stderr.log` |
| 完整 Web 兼容回归 | 161 通过、15 跳过、0 失败/重试；运行约 3.3 分钟 | `web-complete-final.json`、`.stdout.log`、`.processes.json` |

主 Web 套件的 15 项跳过包括 13 项只在 StudioNative 模式运行的界面测试，已全部在上面的独立专项通过；另外 2 项是既有真实 KV 用例，因没有目标 URL/token/database 未执行，不计为 PASS。两次 Web 运行中的 13 项共享协议测试重叠，不能将运行次数累加为新增用例。本次新增 39 个宿主用例和 26 个 Web/协议用例。

原始产物位于 `artifacts/roadmap-closure-20260923/`，不提交构建产物。早期失败日志保留用于追溯，不能代替表内最终报告。最终 source/evidence SHA-256 清单与任务进程清理记录在同一目录单独归档。

复现（外层使用有界执行器，PowerShell 7）：

```powershell
dotnet test tests/SonnetDB.Studio.Tests/SonnetDB.Studio.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 -p:UseSharedCompilation=false
# 在 web 目录，使用已安装的 Chrome，不自动下载浏览器：
$env:SONNETDB_E2E_RUNTIME = 'StudioNative'
$env:SONNETDB_E2E_EXECUTABLE_PATH = 'C:\Users\mysti\AppData\Local\Google\Chrome\Application\chrome.exe'
$env:SONNETDB_E2E_VIDEO = 'off'
node e2e/run-playwright.mjs copilot-studio-native.spec.ts copilot-public-protocol.spec.ts --workers=1 --global-timeout=480000 --retries=0
$env:SONNETDB_E2E_RUNTIME = 'BrowserDirect'
node e2e/run-playwright.mjs --workers=1 --global-timeout=540000 --retries=0
```

## 实际覆盖与未覆盖

Windows 测试实际调用当前用户 Credential Manager，使用独占 GUID 目标写入、跨实例读取、删除并确认消失；所有路径在 finally 再次清理。真实 Kestrel loopback bridge 覆盖 connect/status/readiness/chat/continue/disconnect、来源与鉴权、请求/响应预算和关闭。新增竞态验证：原生输入已返回但 readiness 迟到成功时，断开后不会保存凭据；首个流块已到客户端后，断开或 HTTP 中止会取消上游 ReadAsync，后续块不再发送。

浏览器用例执行生产 Vue Dock、真实 Chrome 网络请求、原生 bootstrap 客户端和 typed MCP 状态机；bridge、公网 runtime 和本地 MCP 服务由受控浏览器 fixture 响应。宿主测试中的原生输入由注入 prompt 替代，公网由可观测 HttpMessageHandler 替代。两组证据分别成立，没有宣称浏览器实际连接了测试中的 Windows host 或真实公网模型。

生产 JSON 全部使用 source-generated context 或手写 Utf8JsonWriter；没有反射序列化回退、unsafe 或新依赖。Studio 原有 `net10.0-windows` / WinForms / `IsAotCompatible=false` 边界未改变，不能据本轮构建宣称 Studio NativeAOT 发布完成。

真实 WebView2 原生窗口交互、部署后 CSP/CORS/权限、真实 provider/IdP、服务器无公网出口的双网现场旅程、真实模型质量/成本和执行中多实例接管仍独立待验收。BrowserDirect OAuth 获取与本次 StudioNative 本地合同完成后不再重复派单；M27 #340 与整个 M27 仍保留这些剩余范围。
