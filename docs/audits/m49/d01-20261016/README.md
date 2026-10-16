# M49-D01：共享协议与原生 .NET Frame 入口

日期：2026-10-16。范围为 D01 的一片依赖拆分和远程传输基础，不关闭 D01 或整个 M49。

`SonnetDB.Protocol` 从 Core 直接接收 Frame 信封、八类 service opcode 与 SpanReader/SpanWriter；Core 引用该独立程序集。`SonnetDB.Client` 只引用 Protocol，不引用 Core、Data 或 Native。没有建立旧 Core 类型转发门面。**这是托管 API 的程序集归属变更，现有消费者必须重新编译。** 磁盘格式和 Frame v1 字节编码未更改。

`SndbFrameClient` 当前只执行单请求/单响应原始 Frame：请求精确 HTTP/2，实际响应也须 HTTP/2；拒绝重定向、错误 Content-Type/编码、关联字段/版本/标志不符、超长、截断及额外数据。整个发送与读取共享期限，关闭客户端会取消在途读取。发送后的协议/网络错误、调用方取消、总期限和客户端关闭保守保留 `OutcomeUnknown`，没有业务自动重试。已知多帧 SQL/向量/图/对象下载和 MQ 双工操作在发送前明确拒绝。

| 验证 | 实际结果 | 边界 |
|---|---|---|
| 共享 IO/信封与 Client 首轮 | 189/189 PASS | 复用原语料，不是 189 个新用例 |
| 加真实 h2c 宿主 | 193/193 PASS | 往返/认证头/重定向/取消/期限 |
| 最终生命周期 | 195/195 PASS，0 skip | 再补传输取消与客户端关闭在途响应；其中 5 个真实 Kestrel h2c 用例 |
| Core 定向回归 | 175/175 PASS | 原 Core 测试 DLL 的 IO/FrameHeader 范围；不是完整 Core 套件 |
| Native 托管回归 | 10/10 PASS | 新依赖下重编译和运行；不是新 AOT 二进制验收 |
| Server Release build | PASS，0 warning/error | 不是 Server 全套业务测试、AOT 或生产验收 |
| 两个本地 NuGet 工程包 | PASS | `0.0.0-local.m49d01.3`，未公开发行 |
| 干净包消费者 | PASS | 仅本地 feed、独占包缓存、无 ProjectReference，真实 HTTP/2 往返及无 Core/Data/Native 文件 |

TRX、显式提取的全部计数、源码/DLL/PDB/包哈希见同目录 `acceptance.json` 和对应 `.trx.gz`。原始 TRX/输出有 Windows 换行，按 GZip 原样压缩保存；解压后的 SHA 对应原 TRX/输出 SHA，`archiveSha256` 对应入库压缩文件。原文件没有改写，源码和可编辑摘要使用 LF。计数从带命名空间的 XML Counters 节点通过 `GetAttribute` 提取，不使用会被 PowerShell JSON 转换为空数组的 XML 节点本身。

`consumer/` 为可复制的包消费者源文件，宿主只回显合法 Frame，用于传输/包依赖验收；它不是真实九模型 Server。重新验证时先构建并打包 Protocol 和 Client 为相同本地版本，再把这两个文件复制到独占临时目录；NuGet.Config 清空默认 feed，只指向工程包输出目录，restore 使用独占 `--packages`。随后以 `--no-restore` 执行 Consumer；请求、启动和停止均有明确超时。旧电脑上的临时副本在交付结束时回收，源代码留在此目录。

工程包生成于待提交源码，nuspec 的 repository commit 指向父提交 `1d02c1c5`，真实源码身份以 acceptance 中源码哈希为准，不能将这两个本地包冒称该父提交已包含的不可变正式候选。包本体和构建 DLL 不纳入 Git；换机后可重新生成，不应按旧机器路径或二进制哈希推断新构建结果。

未完成：Native 仍依赖混合 Data，其他语言绑定和 BASIC 桥接尚未迁移/删除；新 Client 尚无九模型 typed API、SQL 参数/事务、多帧结果或订阅；Core 仍物化 Rows。TLS/ALPN、Linux/macOS/ARM64、远端 CI、SDK NativeAOT 发布、正式包发行均 NOT_RUN。

Core 原 4.x 包验证基线与正式发行清单保持，**本片未运行 Core pack/API 兼容验证，也未关闭、压制或降低门禁**。类型归属直接变化和新 Protocol 包依赖必须在正式发行前按新 API/版本合同处理，并接入正式包清单；本地两个新包通过不代表现有整套发行门禁通过。
