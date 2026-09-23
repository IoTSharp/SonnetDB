# StudioNative Copilot 宿主合同

StudioNative 由 Windows Studio 宿主连接公网 AI runtime，WebView 只访问受保护的本机 bridge；数据库请求继续直接访问当前 SonnetDB。公网凭据通过原生密码输入框取得并存入 Windows Credential Manager，不返回页面、URL、localStorage、连接库 JSON 或日志。

这是原生宿主输入已有短期 token 的入口。它没有执行桌面 OAuth 授权码获取、刷新令牌交换或真实模型质量验收。BrowserDirect 的 OAuth/PKCE 是独立客户端入口。

## 部署配置

Studio 实际打开的 `--server-url` 下 `/admin` 页面必须使用以下构建配置；只设置宿主环境变量不会改变已编译的 Web runtime 模式。

| Web 构建变量 | 用途 |
|---|---|
| `VITE_COPILOT_RUNTIME_MODE=StudioNative` | 显式选择宿主通道，缺少兼容宿主时保持不可用。 |
| `VITE_COPILOT_STUDIO_NATIVE_ALLOW_DATA_EGRESS=true` | 明确允许将获准只读工具的结果送到公网 AI。未设置时禁止工具结果出域。 |
| `VITE_COPILOT_STUDIO_NATIVE_ALLOWED_TOOLS=list_measurements,...` | 获准工具名列表；必须同时满足本地 typed MCP 的只读、权限和结果预算规则。 |

公网地址和凭据寿命只在启动 Studio 的进程环境中配置：

| 宿主变量 | 合同 |
|---|---|
| `SONNETDB_STUDIO_COPILOT_PUBLIC_BASE_URL` | 固定 HTTPS runtime base URL，可包含部署路径前缀，不能包含用户名、密码、查询或 fragment。 |
| `SONNETDB_STUDIO_COPILOT_APPROVED_ORIGINS` | 逗号分隔的明确 HTTPS origin 列表，必须包含 base URL 的 origin，最多 16 项。 |
| `SONNETDB_STUDIO_COPILOT_TOKEN_TTL_SECONDS` | 凭据最长本机保留期限，默认 3600 秒，范围 1～7200 秒；不延长提供方 token 的真实有效期。 |

缺少或无效配置显示“尚未配置”。配置有效后，用户选择“连接 AI 服务”，在宿主原生窗口输入由部署方发放的短期公网 runtime token。宿主验证公网 readiness 后才保存。凭据目标名是 `SonnetDB/Studio/Copilot/` 加完整批准 base URL 的 SHA-256，同一提供方不同路径也隔离。用户断开或到期时删除该目标，不枚举或修改其他 Windows 凭据。

输入值必须是公网 runtime 的 Bearer token，不能使用 SonnetDB 数据库 token 或 bridge bootstrap token。bootstrap token 始终通过原生启动消息进入页面内存；bridge API 拒绝入站 `Authorization` 和查询参数。

## 固定通道

宿主 manifest 声明 `copilot.nativeBroker.v1`。所有新路由沿用 bridge 的精确可信 Origin 和 `X-SonnetDB-Studio-Bridge-Token` 校验，CORS 额外显式暴露公网合同响应 header。没有任意 URL、任意方法或任意 header 的代理接口。

| Bridge 操作 | 方法 | 效果 |
|---|---|---|
| `/studio-bridge/copilot/status` | GET | 只返回配置、连接状态、公开地址、到期时间及错误标识。 |
| `/studio-bridge/copilot/connect` | POST，无 body | 打开原生输入；页面不能提交 secret。 |
| `/studio-bridge/copilot/disconnect` | POST，无 body | 取消输入和在途请求，删除本目标凭据。 |
| `/studio-bridge/copilot/readiness` | GET | 调用批准地址下 `v1/copilot/readiness`。 |
| `/studio-bridge/copilot/chat` | POST | 向固定 `v1/copilot/chat/stream` 发起首段。 |
| `/studio-bridge/copilot/continue` | POST | 同一公网路径，必须带上一工具结果的 continuation。 |

公网服务必须返回 `X-SonnetDB-Copilot-Contract: m27-browser-direct-v1`。readiness 返回 JSON `status: ready` 或 `ok`；聊天返回 NDJSON 或 SSE。HTTP 重定向、错误合同和不支持的 Content-Type 均拒绝。提供方错误 body 不转发到页面。

聊天载荷只接受固定字段和只读模式，禁止由 renderer 指定目标 URL、鉴权 header 或任意代理选项。本地工具必须通过原有 typed MCP schema、只读注解、允许名单及出域预算。工具结果作为不可信数据逐字回传，公网下一段必须逐字回显才能继续；重复 toolCallId 只复用相同参数的结果，不重复执行本地工具。

## 资源和生命周期

宿主最多同时处理 4 个公网请求和 1 个原生连接窗口。原生输入最多 5 分钟；readiness 最多 15 秒，单段转发最多 60 秒且不超过凭据保留期限。前端连接 120 秒、readiness 10 秒的更短期限会沿请求取消链传到宿主。请求上限 256 KiB/256 次读取，响应上限 4 MiB/2048 次读取；readiness 上限 4 KiB/64 次读取。continuation 的工具结果上限 64 KiB。

Web 公共协议循环整轮最多 120 秒，默认 8 次工具调用（配置上限 64）；单段最多 8 MiB/8192 次读取或行，单事件最多 262,144 个 UTF-16 代码单元。JSON 指纹最多 64 层、16,384 节点、1,048,576 字符和 5 秒；公网工具参数进一步限制为 32 层、4096 节点和 262,144 字符，并拒绝重复属性或不能无损传给 JavaScript MCP 客户端的数值。界面支持连接、取消、断开、停止生成；身份改变、登出、到期和组件销毁取消在途工作。宿主关闭取消公网操作；未显式断开的系统凭据只可在其短期有效范围内再次读取。宿主和页面任一不可用都不会自动切换到 ServerRelay 或 BrowserDirect。

## 验收边界

浏览器测试执行生产 Dock、bootstrap、bridge 客户端和 typed MCP 循环，宿主及公网响应由受控 fixture 提供。Windows 测试另行覆盖真实 Credential Manager 的独立目标写入、重新读取、删除，以及真实 loopback bridge HTTP；其中公网服务和原生用户输入由可观测的测试边界替代。

上述本地合同验证不能替代真实原生窗口人工输入、WebView2 实机完整旅程、真实公网 provider/IdP、已部署双网或多实例接管证据。M27 整体状态仍按这些独立验收项记录。
