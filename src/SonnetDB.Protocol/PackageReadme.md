# SonnetDB.Protocol

HTTP/2 Frame 的共享二进制信封、服务/操作码及 SpanReader/SpanWriter。
此程序集只依赖 .NET，不加载 SonnetDB.Core、ADO.NET provider 或 Native AOT 引擎。

M49-D01 直接迁移这些类型的程序集归属，不提供旧 Core 类型转发门面；现有托管消费者需要重新编译。
本片保留原命名空间和字节编码，Frame header 仍为 version 1；没有更改磁盘格式。
模型 payload codec、SQL 写入/参数和正式包发行仍按后续切片验收。
