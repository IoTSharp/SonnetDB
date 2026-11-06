# SonnetDB.Protocol

HTTP/2 Frame 的共享二进制信封、服务/操作码及 SpanReader/SpanWriter。
此程序集只依赖 .NET，不加载 SonnetDB.Core、ADO.NET provider 或 Native AOT 引擎。

M49-D01 直接迁移这些类型的程序集归属，不提供旧 Core 类型转发门面；现有托管消费者需要重新编译。
本片保留原命名空间和字节编码，Frame header 仍为 version 1；没有更改磁盘格式。
模型 payload codec、SQL 写入/参数和正式包发行仍按后续切片验收。

KV 的共享 payload codec 包含 Get、Put、单页 Scan 和 GetAndDelete。GetAndDelete 使用既有
opcode 6，原始二进制键及 db/keyspace 名称与 Core 的 key-only 请求字节保持一致；请求编码前
检查名称、键和完整 payload 预算，不新增或更改 Frame wire 格式。

`DecodeExchangeResponse` 保留原子交换的固定 little-endian Int64 变更版本、旧值存在标记、
固定 Int64 旧版本、完整 UTC ticks 和原始值；它与 Get 的变长版本布局不同。
`KvDataFrameExchangeResult` 区分缺失旧记录与存在的空值，值数组独立拥有，零变更版本映射为
空引用；通用 exchange 在缺失旧记录时仍允许返回正变更版本。非法标记、负版本、旧记录缺少
变更版本、截断和尾字节均拒绝。SDK/实际 Server、TLS、候选包消费与公开发行仍分别验收。
