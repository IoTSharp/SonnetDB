# Issue #397：.NET typed KV Frame 数据访问切片

日期：2026-11-06（Asia/Shanghai）。起点 `2a2f77c762badcad2a16dc2cad2c8e9abd09eebe`，工作包 M49-C03。本文只记录 .NET 的 KV Get、Put 和显式单页 Scan；#397 的九模型、各语言及公开消费者矩阵继续开放。

`SonnetDB.Client` 原先只提供原始单帧/多帧传输和 typed SQL Execute，KV 调用方需自行编码，并且既有 KV codec 位于 Core。新增 `KvDataFrameCodec` 放在独立 Protocol，将已有 Core 的三个请求编码及 Get/Put 响应解码转接到同一实现；保留 Core 的公开类型及签名。新增 `GetKvAsync`、`PutKvAsync`、`ScanKvPageAsync` 直接通过既有 HTTP/2 Frame 端点访问，Client 仍只依赖 Protocol，不加载 Core/Data/Native。

| 合同 | 本片行为 |
| --- | --- |
| 既有协议 | Frame v1、KV Get=1/Put=2/Scan=3 及 payload 字节布局不变，无 REST/JSON/Base64 回落。 |
| 精确值 | 原始二进制键/值、Int64 版本和 UTC ticks 过期时间；缺失键与空数组分开。 |
| 所有权 | 返回的键/值数组独立拥有，不借用后续请求或网络缓冲。 |
| 请求预算 | 编码分配前检查累计 payload、名称 UTF-8 512B、键/前缀/起始键 64KiB 上限。 |
| 响应预算 | 保留传输 payload 上限、总期限、帧身份/完整 EOF 验证；单页 Scan 在条目数组分配前检查请求 limit 及 payload 最小长度。 |
| 单页扫描 | limit 为1～10,000，默认100；以末条原始键作为排除性的 afterKey，不自动预取、拼接所有页或承诺跨请求快照。 |
| 写入终态 | 只确认完整可信的 Put 版本；取消、断连、损坏 payload 保留未知结果，不自动重放。 |

已有 Core 请求编码的键超长异常类别保持不变；名称超过既有 Server 的512B限制时在编码前拒绝。没有新增持久格式、Server opcode、资源生命周期或条件写语义。

实际验证均为本机 Windows x64，使用 `.NET 10`、`DOTNET_PROCESSOR_COUNT=2`、`-m:1`、禁用共享编译器和节点复用。每次外部命令设置300秒或600秒期限，Cordonix 回执记录 PID 及所属子树清理结果。

| 验证 | 结果 | 范围 |
| --- | --- | --- |
| 独立 Client 全套 | PASS，234/234，0失败/0跳过 | 新增15项含字节合同、恶意终态、版本溢出、单页条数、预算前置、预取消与无重试；既有依赖隔离及真实 h2c 传输回归保留。 |
| 产品 Server 扩大组 | PASS，42/42，0失败/0跳过 | 新增6项 typed KV 旅程和既有 KV/Object/Doc Frame、KV 原子 HTTP 合同。 |
| Core KV 协议 | PASS，56/56，0失败/0跳过 | 既有 Get/Put/Scan/Atomic 编解码，验证 Core 转接后的兼容行为。 |
| AOT/trim 分析 | PASS，相关生产工程构建0警告/0错误 | Protocol/Core/Client/Server 的 `IsAotCompatible=true` 分析路径；没有实际新增 NativeAOT 发布产物。 |
| 全 solution restore / 规定 Format | PASS，均退出0 | 完整restore 29.8秒，规定Format 176.3秒；Format有工作区加载警告，原文单列，不称零工具警告。检查后仅更新验证文档，未改代码。 |

产品 Server 使用真实 Kestrel HTTP/2；setup 创建数据库后，旅程内所有请求均独立断言为 `/v1/frame` 和 `HTTP/2`。用二进制键/值、完整过期 ticks、空值、前缀两页及两次宿主重开验证持久化；ReadOnly/未认证写入拒绝且未落数据；提交后故意断连只产生一次请求，读回值及版本只增加一次；在途取消由 Server `RequestAborted` 观察，并验证同一 Client 后续可用；响应预算32B允许17B请求通过，再拒绝256B值响应。

失败和修正保留：首次冷构建在300秒到期，任务子树4→0，未计作测试通过；首次6项均因 setup 默认HTTP/1访问仅HTTP/2宿主返回400，修正为 exact HTTP/2；第二轮5通过/1失败是响应预算夹具16B小于17B请求，独立审阅指出后改为32B，只改变夹具。原失败TRX及最终通过TRX均保留，未放宽生产预算、降低断言或增加业务重试。

本机原始证据在 `artifacts/issue397-20261106/`：`client/client.trx`、`server/typed-kv-server.trx`、`server/typed-kv-server-2.trx`、`server/typed-kv-server-final.trx`、`core/kv-codec.trx`，并保存原生执行/清理回执。生成产物及本机日志不纳入源码提交。

尚未执行：TLS/ALPN、其它操作系统/CPU、其它语言、公开新版 NuGet/全新公开缓存消费者、固定硬件性能和生产验收。此处 payload/页预算不等于引擎端到端有界或进程 RSS 硬上限；KV 资源/schema、条件写/CAS/TTL 修改和其它模型继续按 M49 分包推进。没有发布包、移动正式 tag、部署或关闭母 Issue。

## PR #409 后续正常 main 整合

原 KV 切片提交 `3dfd8af31e27ba7041c2bf3c6f902234c548c679` 已创建 PR #409；随后正常 merge 精确 main `1420b1b14278216f396943213129c2c39958dd42`，接受已合入 PR #408 的两处 Int64Rle 聚合 guard 修复及16项回归。KV 产品及测试源码未继续修改，原234/42/56本机结果仍属于原切片，不能转移为新 merge head 的完整 CI 证据。

唯一冲突为 HANDOFF 的前置记录；按双方共同历史原字节提取两个新增前缀并合并，保留双方新增记录和全部889609字节历史，历史 SHA256 保持 `2e0d76d84b78e5a94b54bf9069d337affaa842c474b7ffe8c8fe557341f372fc`。CHANGELOG 自动合并。本轮未 rebase 或 force push，也未重复全套本机测试；正常 merge 提交前仍执行最终树完整 restore 和规定 Format，新 head CI 另行跟踪。
