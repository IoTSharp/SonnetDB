# GH-Issue #397：KV GetTimeToLive Frame 切片

日期：2026-11-15（Asia/Shanghai）。本切片只补齐 KV `GetTimeToLive`（Frame opcode `10`）的独立 Protocol 与 .NET Client 入口，不改变已经完成的 Get/Put/Scan/GetAndDelete 字节合同，也不扩展九模型 SDK。

## 实现范围

- `src/SonnetDB.Protocol/KvDataFrameCodec.Ttl.cs` 新增 key-only `GetTimeToLive` 请求编码和 TTL 响应解码。
- `connectors/dotnet/SonnetDB.Client/SndbFrameClient.KvTtl.cs` 新增 `GetTimeToLiveKvAsync`，使用 HTTP/2 exact Frame、既有请求预算、取消和未知终态合同，不自动重试。
- TTL 响应保持现有 Core/Server 语义：`-2` 表示缺失键，`-1` 表示永不过期，非负值表示剩余毫秒并必须带 UTC 过期 ticks；所有尾字节、标记和状态不一致均拒绝。

Server 已有 `KvFrameOp.GetTimeToLive` 分发和 Core `KvFrameCodec` 实现，本切片只补独立 Protocol/Client 之间缺失的 typed 桥接。没有新增 opcode、磁盘格式、REST 回退、JSON/Base64 或业务重试。

## 验证

| 层级 | 命令/范围 | 结果 |
| --- | --- | --- |
| 独立 Client/Protocol | `dotnet test tests/SonnetDB.Client.Tests/SonnetDB.Client.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~KvTtlFrameClientTests` | PASS，7/7 |
| 真实 Server/HTTP2 | `dotnet test tests/SonnetDB.Tests/SonnetDB.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~KvTtlFrameClientTests` | PASS，1/1；覆盖三态、持久化重开和 `/v1/frame` HTTP/2 |
| 既有 Core 原子 Frame 回归 | `dotnet test tests/SonnetDB.Core.Tests/SonnetDB.Core.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~KvAtomicFrameCodecTests` | PASS，40/40 |
| 格式 | `dotnet format SonnetDB.slnx --verify-no-changes --no-restore --severity warn --include` 四个本切片文件 | PASS；工作区加载警告单列 |

Client 测试覆盖请求头/名称/原始 key、`-2/-1/正值+UTC ticks` 三态、负值/过期标记/尾字节损坏、未知结果不重试以及预取消不发送。真实 Server 测试覆盖管理员写入后读取、无 TTL、缺失键、关闭重开后的 TTL 保留和精确 HTTP/2。

## 未覆盖边界

本切片不关闭 #397。九模型完整 typed Frame、KV resource/schema/计数/完整条件操作、关系事务/多结果、TLS/ALPN、其他语言/平台、公开 NuGet 消费、NativeAOT 客户端、固定硬件和生产验收仍分别为 NOT_RUN 或未完成。正式包、推送和 issue 状态未在本切片执行。
