# WB71 Native 运行时选择与进程快照合同

WB70 已取得实际 WebView2 的 `/json/version` 响应，版本为 **154.0.4258.62**；父门禁核验的是 **154.0.4258.53** 的一个明确文件。当前 SonnetDB 启动合同没有把这个 prerequisite 路径传成实际浏览器运行时选择约束。随后失败的是 Native 进程快照 helper 的内部预算门禁；粗粒度 `actual WebView2 loopback CDP` 阶段名不能解释为 CDP HTTP 不可达，也不能证明版本差异导致失败。

本报告是 WB71（本会话第 2/5 个独立任务）的静态合同与既有实跑证据盘点。0 actual、0 产品构建、0 Node 合同测试；未修改 runner、helper、产品或旧证据。下面的后续合同尚未实现，WB70 不重跑。

## 证据与源码基线

唯一 WB70 run 是 `studio-native-real-440d3333-4672-4199-a1af-bfecf0bfab4c`。本文的“当前源码”指读取时文件；“记录”指该 run 原文件或当时父门禁，不是新的运行观测。所有 SHA256 如下：

| 文件 | SHA256 | 用途 |
|---|---|---|
| [runner](../../../web/e2e/run-studio-native-real.mjs) | `49FA869E7F210813C19EF3135390780ED21634E704A72B07432080322FA01FB3` | WB70 执行源码；当前字节相同 |
| [Native helper](../../../web/e2e/studio-native-process.ps1) | `C1599A0917278629BAF0CA4C541D9D05BC06E41ABCBBEA68675083954720E57E` | snapshot/close/kill 静态合同 |
| [证据投影](../../../web/e2e/studio-native-evidence.mjs) | `A3F17DCDF9AE116984ED9AFF1DCEBB0211DFA25526FB5256916BCCB9CAEA70D4` | 已有三标量白名单 |
| [当前 Program.cs](../../../src/SonnetDB.Studio/Program.cs) | `2FF63C6E994F520649032F240C7BE7BACAB208781C098E6BF285EB71B66FB4B1` | builder 与 Studio 启动选项静态边界 |
| [父准入收据](../../../artifacts/wb70-studio-database-window-20261008/pre-actual-acceptance.json) | `9E2389C989E7B7E22098E5112FDFAC8441E361795FAFB0B5FC4C5473E89EE482` | 23:18:38.5101447Z 的文件/runtime/import/ports 门禁 |
| [实际 CDP version](../../../artifacts/wb70-studio-database-window-20261008/actual/studio-native-real-440d3333-4672-4199-a1af-bfecf0bfab4c/actual-cdp-version.json) | `7ECB2D539E5FC40EE7045EBEB9052ADA5B24FAB5F8807ECD170FFCC26CEEC8C1` | `Edg/154.0.4258.62`、协议 `1.3` |
| [外层进程记录](../../../artifacts/wb70-studio-database-window-20261008/actual-processes.jsonl) | `87C49939781B855F7D0F9D3A0A69E48F4CB3AF389A389AB2AD0384FBC8289300` | 17 个原 tuple；实际浏览器路径 |
| [Native process-events](../../../artifacts/wb70-studio-database-window-20261008/actual/studio-native-real-440d3333-4672-4199-a1af-bfecf0bfab4c/process-events.json) | `8A28340459017233C8834D3A4D61F8F86BEDF6E674E47FE9E3804AFA949DE6B0` | 四 helper 原统计 |
| [实际 result](../../../artifacts/wb70-studio-database-window-20261008/actual/studio-native-real-440d3333-4672-4199-a1af-bfecf0bfab4c/result.json) | `7D76FE061CE5CF161CC480E299B4F1B37A1DA40EAB07EA06055D0D71EDED1307` | 原 FAIL/阶段/时长 |
| [实际 cleanup](../../../artifacts/wb70-studio-database-window-20261008/actual/studio-native-real-440d3333-4672-4199-a1af-bfecf0bfab4c/cleanup.json) | `C350DB01F36DBD7D935FC1C369C90B07B607CF64091DA4B8469262117AA2FE27` | 原 cleanup false |

当前 Program.cs 并未列入原 runner 的执行源码 hash 集；本文没有新构建、反编译或 source-to-binary 等价验证。实际 Studio/Server 复用既有产物，父门禁核验 Studio exe `E7A4DF6D…06FCC`、dll `371A2E9B…2664E`，记录的 product version 带 `c5716912…`。当前 `project.assets.json` 登记 NativeWebHost/NativeWebHost.Windows `2.0.0`，不能把这份当前 restore 登记或当前 Program 的静态阅读当成当时产物的重新构建证明。

接收边界：HANDOFF `346151` 字节/SHA `73F9BD7A967505B3FEDBB15AC2FEA9B1CA453BDD9457558FEE3B5F07FC525341`；旧同一会话团队完整语义阅读 1–1104 的证明由 WB70 inheritance 和 WB71 根 byte-prefix 验证继承，本 owner 新鲜读取 1–80、945–1126。最初 1–224 块被截断，不计完整阅读。AGENTS 1–336 新鲜完整读取；最新 ROADMAP/queue 由专属 inheritance reviewer 完整读取。它们是团队完整接收，并非 owner 本轮重新完整阅读全部旧 HANDOFF。首短 shell 未仪表化，过程完整性不记 PASS。

## prerequisite 路径与实际运行时选择

1. WB70 父 `preflight.ps1` 只对 `C:\Program Files (x86)\Microsoft\EdgeWebView\Application\154.0.4258.53\msedgewebview2.exe` 做准确文件/hash/version 准入。父收据记录 SHA `30285E24A33BE3C0B2D0A7C3909FA1CC21D2FC0F0C2AB4EB79E46A9304B4256D`，并明确只授文件/runtime/import/端口准入。runner 第 913–919 行把 `.53` 保存为 `runtimePrerequisite` 文案，未以该值选择浏览器。
2. runner 第 710–724 行先复制环境，再删除所有匹配 `WEBVIEW2_` 的继承项，随后只设置 `WEBVIEW2_USER_DATA_FOLDER` 和 `WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS`。因此通过继承的同前缀变量传入 runtime-folder/release-channel 等选择信息不会保留。设置的是私有 profile 和 loopback CDP arguments，未设置 runtime executable folder、期望版本或 release-channel preference。
3. runner 第 932–943 行只传 Studio/Managed Local、目录、连接库、Server exe、route 与窗口参数。当前 Program 第 33–46 行配置 title/custom scheme/content root/start URL/width/height，并使用无参数 `new NativeWebView2AdapterFactory()`；第 67–116 行的 StudioHostOptions 没有运行时目录或版本参数。当前 SonnetDB 入口没有把 `.53` prerequisite 路径传给该 adapter。
4. 原 `actual-processes.jsonl` 中 PID `87368`、parent `71428`、creation `2026-10-07T23:19:52.0456060Z` 的 command 指向 `…\Application\154.0.4258.62\msedgewebview2.exe`，包含当前 run profile 和 port `9371`。原 CDP version 同样返回 `.62`。这是两个已记录来源对实际版本的一致观察；本报告未取得 `.62` 文件 SHA，也未新鲜探测该安装目录。

在已读 SonnetDB 边界内，实际运行时选择委托给 NativeWebHost/WebView2。NuGet 包 README 仅说明 Windows 使用系统 WebView2 或 app-packaged fixed runtime；本文没有读取 provider 的完整实现、注册表/策略或通道优先级，不能断言具体选择原因、升级时刻或系统优先级。NativeWebHost 包版本、Studio binary hash、WebView2 实际浏览器版本是不同证据对象。

## fatal 标签与 helper 调用链

runner 第 970 行设置的阶段名覆盖 `/json/version`、CDP 连接、context/page 条件和后续所有权采集。第 971–978 行已经写出实际 version 收据，之后第 979–991 行才进行 CDP 附着、唯一页面选择和 `captureOwned('cdp-attached')`。

| 记录 helper | 按冻结源码可对应的 snapshot 调用 | 原统计 CIM/cache/秒 | 原退出 |
|---|---|---|---|
| `59844` | runner 第 929 行，取得 runner identity | `8 / 8 / 9.8483298` | `0`、timedOut=false |
| `73616` | runner 第 949 行，取得首次 Studio identity | `9 / 9 / 12.7285097` | `0`、timedOut=false |
| `96692` | 第 991 行 → 第 206–208 行 `captureOwned` → descendants=true snapshot | `13 / 17 / 21.5372204` | `1`、timedOut=false |
| `10704` | 第 1103 行 cleanup-discovery → 同一 descendants=true snapshot（final=true） | `13 / 17 / 20.540533` | `1`、timedOut=false |

这份对应关系是冻结 runner 串行调用顺序与四条原 helper 记录形成的静态推定，不是直接记录的 callsite；旧记录没有保存每次 payload 或逐查询阶段 trace。原 result 的错误文案保存的是拒绝点四舍五入到两位的 `21.50`，error envelope 的最终统计为 `21.5372204`；cleanup 同理为文案 `20.53` 与 envelope `20.540533`，不得互相改写。

`captureOwned` 的具体调用链是 runner 第 206–224 行 → `snapshot` 第 200–203 行 → `processAction` 第 136–197 行 → `studio-native-process.ps1 -Action snapshot`。Node 使用 structured stdin，先验 handshake PID/parent/PS7，再从决定原 success/error 的同 envelope 投影既有三统计；helper exit1 时抛出原 error message，不由标量授动作权限。

Native helper 第 117–132 行先查询 helper 自身及至多 12 级父链，输出 handshake，然后读取 payload。snapshot 第 133–173 行对 1–8 seeds 作 BFS：每个尚未访问 PID 转 identity、取父链，descendants=true 时按 ParentProcessId 查询 children，将完整 child CIM row 放入同 invocation cache，再入队。child row 可直接命中 cache，父链也可命中；null entry 表示本次 absence，非 Fresh 查询不会再次将它解释为新身份。第 24–39 行拒绝同 cache 中已存在 PID 的 creation/parent/command/executable 改变。

parentChain 对 exited 或不可检查的祖先保存明确 boundary（第 85–97 行）；这不是补齐完整父身份，也不把共享祖先授为自有 stop 目标。close/kill 的 Fresh identity 与 ancestry 复核在第 175–229 行，单 PID kill 保持 `Kill(false)`；本文未执行动作或放宽该合同。

## 已有预算与当前可知边界

| 层 | 冻结合同 | 影响 |
|---|---|---|
| Native main/cleanup | runner 第 61–63、101–104 行：database scenario 总 900s，main 预留 90s，cleanup 截至 total−15s | main 的 AbortSignal 与 final 检查边界不同 |
| helper 次数 | runner 第 138 行：database main 64、final 总 96 | 本次仅四次，不证明次数耗尽 |
| Node helper 等待 | runner 第 169–182 行：每次 ≤25s，并受 main/cleanup 剩余时间约束 | timeout 不会直接 kill 无完整 identity 的 helper |
| helper 内部 | helper 第 11–20 行：Stopwatch ≥20s 或 CIM count ≥160 拒绝 | 标量不能定位具体耗时操作 |
| 单个 CIM 操作 | helper 第 48、159 行：`OperationTimeoutSec 20` | 与 helper 整体 20s 是不同上限；没有按整体剩余时间缩短 |
| cache/集合 | helper 第 36–39、81–98、135、143–172 行：cache160、parent depth12、seed8、BFS64/descendant depth12 | query 数包含 self/parent/PID/children 查询；cache 数包括 child enumeration rows 与 null |
| 协议/统计 | runner 第 142、153–195 行与证据投影第 45–56 行：payload/output <或≤512KiB边界、至多3行；counts safe integer0..160、elapsed finite非负 | 无 per-query duration、执行阶段或已花费预算分类 |

内部 guard 在查询前和 cache 更新/迭代处检查。一个 blocking CIM 操作可从仍有剩余预算的检查进入，然后在后续 guard 被拒绝；源码没有把每个同步操作限制在 helper 当前剩余墙钟内。这个静态嵌套预算关系解释了为何不能把“内部上限20s”写成“保证精确20s返回”，但并不证明 WB70 的具体越界发生在某个 CIM 查询。

本次两个失败 envelope 的 elapsed 均超过 20s、CIM13/cache17 均低于160；原明确错误是内部 budget exceeded，Node `timedOut=false`。可认定内部墙钟条件符合拒绝，不能认定查询/cache数量门槛耗尽。cache17大于query13符合 children rows可加入cache的合同，不是重复查询或 cache 泄漏证据。

原 process-events `complete=true`、segment1、helpers4、events0 只证明这份终态结构；`captureOwned` 要成功返回后才在第 222 行追加 event，因此零 events 不等于 Native 没有启动后代。原 normalExit/cleanupProven false；原 cleanup fallback/helperReclaims/identityChecks/directories 为空，不能由后续根回收324对象补写。WB70 原严格审计失败、未提交、rootClosureTimely/checkpointVerified false均保持。

## 下一可执行合同（NOT_IMPLEMENTED）

WB66 已盘点 20s/160/缓存预算，WB67 已实现 success/error 三标量透传；不把它们重新包装为待实施。WB71 新确认的是 prerequisite 与实际版本之间没有选择绑定、version HTTP 已成功以及 snapshot 失败仍只有聚合统计。

建议下一独立切片只补 **Native helper 既有 snapshot 路径的阶段与查询耗时观察**，先合同与本地夹具、0 actual。限定 helper、既有 evidence projector、必要 runner 同 envelope 接收边界及专属测试；不改 Studio/Server、runtime选择、预算、采样次数、Stop/Fresh/父链或 cleanup authority。固定结构只含有限 phase enum、实际累计 scalar 与 unknown，不保存 PID/命令/创建时间、原错误或新正文，不从错误字符串反推 trace。

验收应先冻结：self-handshake/parent-chain/seed-lookup/child-enumeration 等固定分类；同一次已有 CIM 调用前后取单调时钟，不增加 CIM 或协议行；≤160操作槽和独立观察墙钟；失败/timeout/异常/迟返不遮原 primary，缺失或非法观察为 unknown。用注入查询/时钟的小输入和最终定向合同验证 cache hit/null、child rows、父链边界、内部deadline、query failure、count边界与观察失败，逐项证实原调用次数、原抛出对象及拒绝语义保持。具体 DTO、观察上限和文件归属必须在实施任务重新冻结，本文不授扩展权限。

有了该合同仍需在另一个经 source/tools 审阅、fresh runtime/port/identity/cleanup 门禁准入的有界窗口，才可取得新实际分段耗时；不得重开 WB70 的 actual1/1。运行时准入的后续应把“明确文件存在/hash”与“实际选择 path/version”分列，先从既有 recorded tuple/CDP 接收观察；是否采用 fixed-runtime pin 必须单独确认 provider API、产物合同与选择目标，不能把 `.53` 硬塞为未经验证的产品配置。
