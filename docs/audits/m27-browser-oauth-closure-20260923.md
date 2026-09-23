# M27 #340 BrowserDirect OAuth/PKCE 验收（2026-09-23）

**状态：✅ `COMPLETED_LOCAL_CONTRACT`。** 本报告只验收 Web 公共客户端的 Authorization Code + PKCE 获取入口。StudioNative broker 与系统凭据库后续已有[独立本地验收](m27-studio-native-closure-20260923.md)；多实例实时接管、真实 IdP 和已部署双网环境仍独立待办，不以受控身份服务替代生产验收。

## 交付合同

- `web/src/copilot/browserDirectOAuth.ts` 使用显式批准的 HTTPS issuer、授权和交换端点；S256、独立随机 state/verifier、RFC 9207 `iss` 与精确 popup source/origin 绑定。回调固定为当前 HTTPS origin 的 `/admin/copilot/oauth/callback`，拒绝重复参数、implicit token 和非批准路径。
- 交易仅在内存存活五分钟，单次交换限三十秒、64 KiB/1024 分片；仅接收最长两小时有效的 Bearer token。不请求、保存或使用 refresh token、client secret 或持久登录状态。数据库 token 仅用于本地同值拒绝，交换请求没有数据库 Authorization 或 Cookie。
- CopilotDock 提供连接、取消、断开和到期处理；身份变化、登出、模式切离、组件销毁会取消交易并清理凭据，迟到交换响应不能重新写回 token。UI 只订阅到期时间，配置不足保持“尚未配置”。
- 独立匿名回调绕过数据库登录/setup 初始化，清除当前历史条目的授权参数。`web/index.html` 在 favicon、脚本和预加载资源之前声明 `no-referrer`；生产构建输出也已核对该顺序。Vite 响应同步提供 `Referrer-Policy: no-referrer`，覆盖其在 HTML meta 之前注入开发客户端的情况；最终测试对所有资源严格校验，无开发环境例外。

完整环境变量、公共客户端注册、RFC 9207、CORS、CSP 和 COOP 前提见 [Provider 配置](../copilot-providers.md#browserdirect-oauthpkce-登录)。

## 验收记录

基线 commit 为 `e818185590cdf0b14045dbfabe0c4d70f7907be2` 加本次工作树修改。运行环境为 Windows、PowerShell 7.6.6、Node.js 24.15.0、Playwright 1.55.1 和已安装的 Google Chrome 150.0.7871.115；没有下载浏览器或安装依赖。

| 验证 | 结果 | 证据 |
|---|---|---|
| `npm run build`（含 `vue-tsc --noEmit`） | PASS | `oauth-web-build-verified.stdout.log` / `.stderr.log`；Vite 保留既有大 chunk 提示。 |
| 默认完整 Web 套件 | 148 通过、2 跳过、0 失败/重试 | `web-playwright-verified.json`、`web-regression-verified.stdout.log`；运行约 190 秒。 |
| 完整请求头与 Referrer 加固后的定向复验 | 12/12，0 跳过/失败/重试 | `oauth-final-verified.json` / `.stdout.log`；运行约 40 秒。 |

本次新增 56 项用例：OAuth 核心 43、真实浏览器 12、模式切换凭据清理 1。完整 Web 套件中 Copilot 共 95 项，另有管理工作台 51、初始化 2；两个跳过项为既有 `kv-atomic-real.spec.ts`，缺少独立真实 KV 服务的 URL/token/database，未计作 PASS。最后只修改了请求头采集断言与 Vite Referrer 策略，因此复验针对 12 个受影响 OAuth 浏览器用例；不把重复运行累加为新测试数量。

实际 CopilotDock 的“连接 AI 服务 → 生产 callback → 已连接 → 断开”已通过；截图为 `browser-direct-oauth-connected.png`。授权请求实际携带受控 IdP session cookie，token POST 经 `allHeaders()` 快照证明确实不携带 Cookie/Authorization；公网请求只携带独立 public token。回调所有资源均检查 Referer 不含 code/state，刷新、取消、登出、到期、错误 issuer/state、重复参数、错误 popup/source/path、重放和交换重定向均有回归。

原始产物保留在仓库 `artifacts/roadmap-closure-20260923/`，源码/文档、最终 TRX 和浏览器报告的 SHA-256 见该目录的 `source-manifest.json`。`*.processes.json` 记录命令、退出码、耗时及进程身份；中间失败日志保留用于排错，不能冒充最终通过报告。

默认 `npm run test:e2e` 的 Vite 子进程注入固定 `.test` 域名的非秘密 fixture 配置，不写 `.env`，不改变生产构建默认 ServerRelay。OAuth 测试通过浏览器路由模拟 HTTPS 身份服务，真实执行 Chrome WebCrypto、popup、生产 Vue callback、token POST 与 CopilotDock 按钮；其它域名被 fixture 拒绝。

PowerShell 7 复现命令（在 `web` 目录，外层保持有界执行；已安装 Chrome 的机器可指定路径）：

```powershell
$env:SONNETDB_E2E_EXECUTABLE_PATH = 'C:\Users\mysti\AppData\Local\Google\Chrome\Application\chrome.exe'
$env:SONNETDB_E2E_VIDEO = 'off'
$env:PLAYWRIGHT_JSON_OUTPUT_FILE = 'D:\source\SonnetDB\artifacts\roadmap-closure-20260923\web-playwright-verified.json'
npm run test:e2e -- --global-timeout=540000 --timeout=45000 --retries=0 --reporter=list,json --output=../artifacts/roadmap-closure-20260923/web-playwright-verified
```

## 未执行的外部证据

真实 IdP 登录、生产 token endpoint CORS、宿主 CSP/COOP、真实 AI continuation、服务器无公网出口的双网旅程、StudioNative 和多实例接管均未执行。受控浏览器 PASS 只完成本报告的客户端获取合同，不把整个 M27 #340 标记完成。
