# SonnetDB 的 .NET 11 升级评估

评估日期：2026-10-26，Asia/Shanghai。源码基线：`dbc8178988a0293ed67d24e6f70b78b970465aa0`，当前 `main` 工作树；已有 staged/unstaged 文档工作保持原状。本次范围为公开官方资料核验、源码静态检查与升级建议，不执行版本迁移、安装、部署或生产漏洞验证。

**建议：现在开始独立验证 .NET 11；生产主线继续维护 .NET 10 LTS，并优先核对 10.0.14 安全补丁。正式版发布后，根据同等持久化语义下的性能、恢复与消费者证据决定是否切换。**

截至评估日，.NET 11 尚未 GA。Learn 功能页仍按 RC2 更新，而官方发行仓库已经列出 2026-10-25 的 `11.0.0-rc.3` 安全更新；计划 GA 为 2026-11-10。当前 STS 支持期是两年，不是旧政策中的 18 个月；11 的计划支持截至 2028-11-09，10 LTS 截至 2028-11-14。STS 与 LTS 的区别是支持时长，不能据此断言 STS 的工程质量更低。

## 证据与判断边界

| 证据 | 本次观察 | 对应判断与后续动作 |
| --- | --- | --- |
| E01：官方 [.NET 11 发行记录](https://github.com/dotnet/core/blob/main/release-notes/11.0/README.md)、[RC3 说明](https://github.com/dotnet/core/blob/main/release-notes/11.0/preview/rc3/11.0.0-rc.3.md)、[支持政策](https://github.com/dotnet/core/blob/main/release-policies.md)、[版本表](https://github.com/dotnet/core/blob/main/releases.md) | 最新为 RC3，11 的 GA 尚未到来，10 仍受支持 | F01：没有因 10 即将失去支持而立即迁移的必要；先做候选验证 |
| E02：[global.json](../../global.json)、[Directory.Build.props](../../Directory.Build.props)、[Dockerfile](../../src/SonnetDB/Dockerfile)、[包版本](../../Directory.Packages.props) | SDK 基线 10.0.100/latestMinor，TFM 为 net10.0，LangVersion=latest；容器为 sdk/aspnet:10.0，Hashing/Tensors 与 EF 有各自版本 | F02：安装 SDK 11 不会自动完成整个产品迁移；SDK、TFM、包、镜像及发布物需要逐项确认 |
| E03：[数值聚合](../../src/SonnetDB.Core/Query/NumericAggregateVector.cs)、[向量距离](../../src/SonnetDB.Core/Vector/Compute/Distance.cs) | 已使用安全 Span、Vector 和 TensorPrimitives，保留标量及零向量边界 | F03：适合验证 JIT/SIMD 的增量收益；无需重做已有向量化 |
| E04：[新 Frame Client](../../connectors/dotnet/SonnetDB.Client/SndbFrameClient.cs)、[Frame 服务端](../../src/SonnetDB/Endpoints/Handlers/FrameEndpointHandler.cs) | 严格 HTTP/2、ReadOnlyMemoryContent、总期限/预算/终态；服务端已有 PipeReader/PipeWriter 和自写 ReadOnlyMemoryStream | F04：优先测 async/Pipelines，并评估用 BCL 流替换自写实现；保留内存所有权及未知提交结果 |
| E05：[WAL](../../src/SonnetDB.Core/Wal/WalWriter.cs)、[组提交](../../src/SonnetDB.Core/Engine/WalGroupCommitCoordinator.cs)、[目录刷盘](../../src/SonnetDB.Core/Wal/DirectoryFsync.cs) | 已有 CRC、Flush(true)、group commit 和目录元数据持久化处理 | F05：升级不替代数据库的恢复协议；性能比较必须保持相同确认与刷盘策略 |
| E06：[备份服务](../../src/SonnetDB.Core/Backup/BackupService.cs)、[备份 JSON context](../../src/SonnetDB.Core/Backup/BackupJsonContext.cs) | 当前为 checkpoint 后的目录备份、SHA-256 manifest、暂存目录恢复及发布 | F06：ZIP 新校验不会直接增强当前目录备份；可以为未来归档封装增加一层检测 |
| E07：[旧远程 HTTP 工厂](../../src/SonnetDB.Data/Remote/RemoteHttpClientFactory.cs)、官方 [10.0.14 安全更新](https://github.com/dotnet/core/blob/main/release-notes/10.0/10.0.14/10.0.14.md) | 工厂启用 AutomaticDecompression=All；公告覆盖 HttpClient 的 Brotli 自动响应解压路径 | F07：优先核对实际交付物的 10.0.14 修复状态；源码触达条件不等于生产已经确认受影响 |
| E08：[CMake](../../connectors/c/CMakeLists.txt)、[发布工具](../../eng/tools/connectors-release/Program.cs)、[发布核验](../../eng/verify-release-artifacts.ps1) | Unix 原生库名写死为 SonnetDB.Native.so | F08：11 的默认 lib 前缀是具体迁移阻断，须统一文件名与装载/打包路径后再验收 |
| E09：[密码哈希](../../src/SonnetDB/Auth/PasswordHasher.cs)、[Token](../../src/SonnetDB/Auth/ApiToken.cs) | 已使用 PBKDF2-HMAC-SHA256、随机盐、安全随机数和恒定时间比较 | F09：这些现有安全能力不应重复登记为 11 的新收益；新密码学原语需独立设计密钥管理 |
| E10：[入站读取工具](../../src/SonnetDB/Endpoints/Support/EndpointIngestUtils.cs)、[Influx 写入口](../../src/SonnetDB/Endpoints/Handlers/InfluxLineProtocolEndpointHandler.cs) | gzip 解压后经 ArrayPool 缓冲读取到 EOF，辅助函数没有显式解压后字节上限 | F10：引入更多压缩方式前，先核对并补齐解压后预算、超时与异常回收；本次未做端到端可利用性验证 |

这些判断来自当前源码和当日官方页面；性能收益、.NET 11 构建、Native AOT 发布、真实网络、生产补丁状态及恢复结果均为 `NOT_RUN`。

## 性能：优先验证哪些变化

| 优先级 | .NET 11 的变化 | SonnetDB 的对应位置与收益假设 | 接入与限制 |
| --- | --- | --- | --- |
| 高 | JIT 的边界检查消除、去虚化、分支折叠及 SIMD 代码生成改进 | 聚合扫描、SQL/Frame 编解码、向量距离；可能减少每行/每向量 CPU 开销 | 用相同源码、数据与依赖先比较 10/11；JIT、ReadyToRun、Native AOT 分开计量，不套用微软微基准倍数 |
| 高 | Runtime Async 与 Task/ValueTask continuation 优化；Pipelines 减少高并发锁竞争 | Frame 的异步收发、对象流、摄取与服务端请求处理；可能降低分配、CPU 和尾延迟 | Runtime Async 对应用仍为 preview opt-in，11 的运行时库已使用它；应用开关需单独实验，不因 TFM 升级就在全部项目启用 |
| 高 | Native AOT 接口分派改进 | embedded/native 以及依赖接口调用的 reader、command 路径；可能减少代码体积和分派开销 | 必须用真实 C ABI 消费者测量；JIT 测试不能替代 AOT 测试 |
| 中高 | ReadOnlyMemoryStream、ReadOnlySequenceStream 等内存流 | FrameEndpointHandler 自写的只读流可以评估替换；分段缓冲可在合适的 Stream 边界避免展平复制 | 现有自写流已经避免整块预复制，因此首先是维护质量收益；PipeReader.AdvanceTo 之后的缓冲生命周期必须保持正确 |
| 中高 | Span 版 Deflate/ZLib/GZip、可 Reset 的编解码器、Zstandard | 大批量摄取、日志/文档导出、备份传输及部分对象场景，可能减少 Stream 包装和临时分配，改善压缩率/吞吐折中 | 先测非持久化传输或外层归档；小请求与已压缩媒体可能更慢。不能直接替换时间戳/数值的专用编码 |
| 中 | InitialHttp2MaxConcurrentStreams、连接淘汰回调 | Frame 新连接并发突发和长连接更新 | 新属性只控制 SETTINGS 到达前的并发，不替代服务端 admission/背压。使用现有严格 HTTP/2 和未知结果合同 |
| 中低 | 新向量 lane 操作与 Half 硬件支持 | 将来明确选择 FP16 或特殊量化布局时可评估 | 当前主要距离输入是 float；不会仅靠升级自动减半索引内存。硬件、误差、Recall@K、磁盘和 Frame 编码需一并验收 |

不能承诺全库提升固定百分比。磁盘 fsync、锁竞争、索引访问次数或模型推理可能占据大部分耗时；这些因素不会因为更换运行时自动消失。尤其 `WalGroupCommitCoordinator.Wait()` 的同步边界，需要在原有写入确认合同下评估，不能为提高压测数字删掉。

`System.Numerics.Tensors` 在当前仓库固定为 10.0.12。第一轮对比应尽量保持依赖一致，第二轮再单独测 11 系列包，避免把包升级与运行时升级的收益混在一起。

## 代码质量：适合采用的变化

1. **用标准内存流替换自写适配器。** `FrameEndpointHandler.cs` 的私有 `ReadOnlyMemoryStream` 从 1658 行开始。评估替换时核对 CanSeek/Position、取消、Dispose 与 PipeReader 生命周期差异；它能减少需要长期维护的 Stream 重载，而不是重新实现零复制功能。
2. **使用更完整的 source-generated JSON 合同。** 11 支持更多非 public 成员/构造器、init 默认值及类型化元数据访问，可以减少 DTO 的可变公开成员和专用胶水代码。所有生产 Serialize/Deserialize 仍必须传已注册的 JsonTypeInfo/context；不能照搬依赖默认反射 resolver 的文档示例。Utf8JsonWriter 新 Reset 重载可以在有明确所有权的热点中复用，不共享同一个 writer 给并发请求。
3. **用 C# 15 union/closed hierarchy 表达内部状态。** 适合执行计划节点、解析结果、流终态，以及“已提交/已拒绝/结果未知”等互斥结果，借助穷尽 switch 检查暴露遗漏。先用于内部类型，保留磁盘布局、Frame 数字码及现有 public API 合同。除 memory safety 外，官方列出的这些 C# 15 特性无需 LangVersion=preview；SDK 本身当前仍为预发布。
4. **改善工程工具的进程与测试控制。** 新 Process 捕获输出、退出状态及 Windows KillOnParentExit 能减少部分自写启动代码；仍要执行 PID/启动时间/归属核验、总超时及子树清理。11 SDK 的 dotnet test 总超时、最大失败数、每模块结果目录值得评估，但这些新选项有 Microsoft.Testing.Platform 前提；当前仓库是 xUnit 2/VSTest，不能只升级 SDK 就把选项加入现有 CI。
5. **评估 NuGet 的最小发布年龄。** 11 SDK 可通过 --min-publish-age-hours 配置公开源新包冷却期，降低刚发布恶意包被立即选择的窗口；它不替代版本固定、来源约束、许可证和漏洞核验，也不是安全保证。

目前 `LangVersion=latest` 会随所选 SDK 改变。验证时明确记录语言版本，避免把编译器、语言语义、运行时和业务改写一次混入。C# 15 memory safety 仍为 preview，部分变化放宽指针声明，并不表示可以突破本项目 safe-only/禁止 unsafe 的约束。

## 数据安全与可靠性：实际收益和适用范围

**先补受支持版本的安全更新。** 2026-10-25 的 10.0.14 和 11 RC3 都包含 Brotli 解码器安全修复（CVE-2026-111497），并补全 SocketAsyncEventArgs 相关修复。旧 RemoteHttpClientFactory 的自动解压设置与公告的适用路径有关，因此优先核对实际 SDK、框架依赖部署、容器和自包含/AOT 发布物。自包含或 AOT 的应用要重新构建、发布；只更新机器上的共享 runtime 不会修补已经携带旧运行时的二进制。本次未访问生产主机，生产安装版本为 `UNKNOWN`。

| .NET 11 的相关能力 | 可以改善什么 | 对 SonnetDB 的实际边界 |
| --- | --- | --- |
| TLS 握手边界处理加固、Linux 标准证书失败告警；Unix Negotiate channel binding 校验 | 畸形握手处理、错误定位及特定集成认证安全性 | TLS 由谁终止必须分别确认；反向代理终止的那一段不会因应用运行时升级自动改变 |
| SslStream 服务端验证客户端证书时默认关闭 AIA 下载 | 避免握手等待远程 CA，减少由客户端证书影响外连目的地的风险 | 主要适用 mTLS；完整中间证书链需要由客户端或服务端预置，缺链可能使原先能连的客户端失败 |
| BackgroundService 失败在 StopHost 模式下使 RunAsync/StopAsync 传播异常 | 不再以正常退出掩盖 worker 故障，使服务管理器能据失败状态处理 | 适用于未捕获并逃出 ExecuteAsync 的异常；需验证退出码、重启策略和关闭刷盘，不会修复已经被业务吞掉的异常 |
| Linux/macOS 进程内崩溃报告 | 增加现场诊断信息，帮助定位运行时崩溃 | 需要显式配置；诊断不等于 WAL 修复，报告也不保证崩溃时刷盘 |
| ZIP CRC32 与 TAR 头校验 | 检测归档损坏/截断 | 当前备份是目录+SHA-256 manifest，新增校验仅适用于将来归档层；CRC32 不提供真实性或防篡改认证 |
| AES Key Wrap、X25519；实验性 HPKE/复合 ML-KEM | 未来 DEK/KEK 信封加密和特定密钥交换场景 | 不会自动给数据库加密。静态加密仍需 AEAD、nonce、密钥轮换、KMS、恢复和版本设计；AesGcm 等基础能力在 10 已可用，实验性算法暂不作为生产升级理由 |

当前密码盐、安全随机数及 FixedTimeEquals 已有实现，不应把它们列为升级才能取得的能力。密码哈希成本、授权隔离、备份访问控制、篡改检测、WAL 恢复仍是自己的工程责任。

E10 是独立的静态可靠性关注点：新增 gzip/zstd 接入前，需要明确限制**解压后的字节数**，并检查取消、时间预算、池缓冲归还和畸形输入。限制 Content-Length 或网络请求体大小只约束压缩输入时，不能推导出解压后内存有界。现有 Influx handler 会在 finally 归还租借缓冲，但这不限制峰值大小。本次不将该观察写成已验证生产漏洞，也不扩大为代码修改。

## 会阻断直接切换的兼容性变化

1. **CPU 基线提高。** x86/x64 的 JIT/AOT 最低基线从 x86-64-v1 提到 v2，需要 SSE4.2、POPCNT 等；不满足时可能直接不能启动。Windows/Linux 的 R2R 目标提高到 v3，不代表所有支持运行的 CPU 都必须具备 AVX2：部分机器会回退 JIT并增加启动开销。Linux Arm64 最低基线没有提高，R2R 新增 LSE；Windows Arm64 最低基线要求 LSE。必须核对实际老服务器/边缘设备，不以开发机代替。
2. **Unix Native AOT 文件名改变。** 11 默认把 `SonnetDB.Native.so` 变为 `libSonnetDB.Native.so`。当前 CMake 第 69/119 行、发布工具 NativeLibraryName、第 101 行发布核验等仍使用旧名。可统一采用新名字，或显式 UseNativeLibPrefix=false 固定既有文件合同；这是构建策略选择，无需新增 SDK/API/ABI 兼容外壳。C/Go/Rust/Python/Java 各消费者和发布归档都要读回真实文件名。
3. **数值语义变化。** 浮点转小整数改为饱和转换，Math.Round/MathF.Round 与 Decimal/BigInteger 转换改进可能影响边界值或最后几位。检查 SQL CAST、量化、编码及回归快照。Decimal32/64/128 是 IEEE 十进制浮点，不能替换现有 System.Decimal 的 SQL/Frame/磁盘合同。
4. **压缩与归档行为改变。** 空 GZip/Deflate 也写头尾，ZIP/TAR 更严格校验可能使以前接受的输入失败；迁移测试应覆盖空输入、截断、坏校验和超大解压输出。
5. **网络新行为不能改变 Frame 合同。** HttpClient 的 Windows 认证自动降到 HTTP/1.1，并不是本项目允许的回落方案；继续验证 RequestVersionExact。当前 Frame Client 明确拒绝 Content-Encoding，新 HTTP 压缩包装器不能直接套上，需先独立制定双方接受的编码、字节预算与版本合同。发送后的未知写入结果继续保持未知，不能因连接淘汰、重连或取消而自动重试。
6. **AOT 与消费者适配单独验收。** 11 对 UnmanagedCallersOnly 的非 blittable 签名有更严格处理；检查 Native 导出、回调及真实 C ABI。目标框架升级与 EF Core 11 provider 适配分别评估；不能把 EF 的新功能等同于自研存储引擎已支持，也不要求本轮无条件升级所有 NuGet 大版本。LoongArch/国产平台沿现有独立发行路径核对运行时来源和支持范围。

## 建议的最小验证顺序

1. **保持 10 主线可交付。** 核对并在独立变更中处理 10.0.14 安全修复，包括镜像和自包含/AOT 发布物。先保留已验证的部署与可恢复备份；不在生产直接拿 RC 覆盖。
2. **建立同一代码基线的 10/11 候选对比。** 单独记录 SDK、TFM、语言、依赖、OS、CPU 指令集和镜像 digest。第一轮不改文件格式或启用额外新 API；Runtime Async 对应用的 opt-in 再作为单独变量。开发期使用隔离验证分支/环境，不要求全部 NuGet 长期双目标。
3. **复用现有有价值的基准。** `NumericAggregateSimdBenchmark`、`VectorCosineBatchBenchmark`、`FrameEncodingBenchmark`、`WalGroupCommitBenchmark`、`WalBufferingBenchmark` 和 `ServerBenchmark` 可作为入口。固定硬件、数据集、并发、预热及刷盘策略，分别测 JIT/AOT、吞吐、P95/P99、CPU、分配、RSS、启动和产物大小。冷/热缓存与 x64/ARM64 分列。
4. **验证数据库不变量。** WAL replay、已确认写入、崩溃重启、compaction 发布、损坏检测、备份恢复、MQ 实例恢复分别取证；数据库目录备份不能冒称包含 Server 实例 MQ。进程强杀与真正断电属于不同证据，均不能从单元测试推导。
5. **验证网络和消费者。** 严格 HTTP/2、取消/超时/半帧/缺终态、错误证书、权限/会话隔离和未知写入结果；同时核对 C/Go/Rust/Python/Java 的实际原生库、装载与资源释放。用新 SDK 跑 restore、原级别 Format Check、测试和零 AOT/Trim 告警发布。
6. **决定是否发布。** 建议以一项核心生产负载得到可重复的约 10% 吞吐/CPU 改善或显著分配下降、P99 无明显回退为性能决策起点；这些是建议阈值，尚无实测。正确性、恢复和消费者阻断必须清零，并完成候选长跑/现有 nightly 门禁。若收益很小，继续 10 LTS 合理；若收益成立，先升级可控服务端，再推进依赖广泛的嵌入式分发。

GA 当天应先核对最终版本、变更与已知问题；没有必要仅为了等首个补丁而设固定日历门槛，也不能只因 GA 或 Go-Live 标识便跳过产品验收。

## 静态扫描记录

本次对 E03/E04/E05/E10 的六个热点文件做了有界文本规则扫描，先用单文件试运行再处理六文件，总计 1827 行（按换行拆分计）。规则匹配是源码出现次数，可能包含注释，不能作为缺陷个数。额外读查 Frame handler、备份、认证、打包路径以验证上下文，不宣称全仓审计。

| 已执行规则 | 匹配数 |
| --- | --- |
| Result/Wait/GetAwaiter().GetResult 同步等待候选 | 1 |
| ValueTask；string.Format | 0；0 |
| stackalloc | 4 |
| IndexOf 字面量无比较参数；StartsWith/EndsWith 同类；Contains 同类 | 0；0；0 |
| Substring；ToLower/ToUpper 无参数；Replace；params | 0；0；0；0 |
| Select/Where/OrderBy/GroupBy/Any/All/ToList | 1 |
| new Dictionary/List；static readonly Dictionary；Regex/GeneratedRegex/Compiled | 0；0；0 |
| new byte[]；ArrayPool.Rent | 6；7 |
| JsonSerializer 调用；new JsonSerializerOptions | 1；0 |
| new HttpClient | 1 |
| class 声明；sealed class | 6；3 |

上下文复核：六个 class 中 3 个 sealed、3 个 static，无需为“未 sealed”进行重构；1 个 JSON 调用使用 ServerJsonContext，1/1 为显式 source generation；1 个 HttpClient 在 SndbFrameClient 构造时创建并持有，1/1 由长期客户端管理，不是每次请求重建；1 个 LINQ 命中是 Token 参数校验的 Any，不是数据库逐行计算。4 处 stackalloc 复核未发现放在重复循环体内的用法，6 处数组分配包含独立拥有的响应数据，不能无条件池化。

| 性能规则分类 | 本次确认数 | 处置 |
| --- | --- | --- |
| Critical | 0 | 未从上述规则确认死锁、重复 await ValueTask 或数量级回退；不代表全库安全或性能验收 |
| Moderate | 0 | 未把匹配数直接升级成性能缺陷 |
| Info / 需要测量的设计点 | 1 | WAL 同步等待属于持久化确认边界，先测线程池/并发成本，保留正确性 |

静态规则不能代替 profiler 和基准；本报告中的收益均为待验证假设，未将官方微基准或源码扫描换算成 SonnetDB 的实际加速比。

## 官方参考

- [11 的 Runtime 新特性与 CPU 表](https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-11/runtime)
- [11 的类库、内存流、压缩、JSON、密码学与网络](https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-11/libraries)
- [11 的 SDK 与 MTP 测试选项](https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-11/sdk)
- [C# 15 与 memory safety preview](https://learn.microsoft.com/en-us/dotnet/csharp/whats-new/csharp-15)
- [11 兼容性变更索引](https://learn.microsoft.com/en-us/dotnet/core/compatibility/11)
- [Native AOT Unix lib 前缀](https://learn.microsoft.com/en-us/dotnet/core/compatibility/interop/11/nativeaot-lib-prefix)
- [BackgroundService 失败传播](https://learn.microsoft.com/en-us/dotnet/core/compatibility/extensions/11/ihost-runasync-stopasync-throw-backgroundservice-failure)
- [SslStream 服务端 AIA 默认关闭](https://learn.microsoft.com/en-us/dotnet/core/compatibility/networking/11/sslstream-aia-downloads-disabled)

官方页面可能继续更新；本报告按评估日的 RC3 发行状态与 RC2 功能说明形成，GA 后需要刷新最终合同。
