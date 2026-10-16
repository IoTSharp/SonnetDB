# M49 换机后接续入口

更新时间：2026-10-16（Asia/Shanghai）。用户要求当前片实现、交接、提交推送后更换电脑，新会话继续。**本机在本片交付后停止新增实现；F01 留给新会话。整个 M49 继续 IN_PROGRESS，15 个工作包均未整体完成。**

## 取得正确代码

- 远端：GitHub `IoTSharp/SonnetDB`；分支 `codex/m49-connector-batches-roadmap`。
- 首个 Native 切片已提交推送为 `1d02c1c5f410b2ca90e83c56fd9993e52c1f77f4`；D01 共享协议/Client 切片最终提交为 `6a606c6ba4170f775f1a3fe6b3c9e333f09f2409`，远端分支已回读一致。以该远端分支最新 HEAD 为新会话起点，不停在首片提交。
- 新电脑取得完整仓库和固定 submodule 后，核 `git branch --show-current`、`git rev-parse HEAD`、`git status --short`，先阅读根 HANDOFF 最新章节、AGENTS、M49 确认需求和实施矩阵。不要把默认 main 当成本分支，也不要复制旧电脑 TEMP、凭据或构建输出作开发基线。
- 旧电脑实际实施目录为 `C:/Users/mysti/.codex/worktrees/connector-batch-roadmap/SonnetDB`；`D:/source/SonnetDB` 是另有共享修改的 main 树，因此之前用户在它的目录视图里看不到新 embedded。**新电脑可使用本机自己的正常 checkout 路径，后续命令显式使用该路径。**

## 已落地

1. `embedded/native/SonnetDB.Native` 是唯一 AOT 导出项目。NativeResult 不再复制完整 reader 到 List；EOF/失败/取消/提前 free/父连接关闭清理 reader/command，已释放结果的 RecordsAffected 也拒绝。Core 本身仍完整物化。
2. `src/SonnetDB.Protocol` 是无引擎依赖的信封/opcode/Span IO 程序集；Core 引用它。**托管类型从 Core 迁入 Protocol，不留旧类型转发，消费者须重编译；没有更改磁盘或 Frame v1 字节格式。**
3. `connectors/dotnet/SonnetDB.Client` 只依赖 Protocol，提供原生 HTTP/2 单帧远程传输。拒绝 HTTP1/重定向/错误帧身份/截断/额外数据，整请求期限覆盖响应读取；调用方取消和客户端关闭释放在途流，发送后失败保留未知结果，无业务重试。
4. 九模型 typed API、多帧 SQL/向量/图、对象下载、MQ 双工未接入新 Client；这些操作目前在单帧入口明确拒绝。不能把该基础入口当成完整 SDK。

## 现有证据

| 证据 | 位置与含义 |
|---|---|
| 首片 Native 10/10、旧 AOT/C 消费者 | `docs/audits/m49/native-lifetime-20261016`，含原 TRX 和补充计数；绝对临时路径是历史记录，EXE 已回收 |
| D01 Client 195/195、Core 定向175/175、Native托管10/10、Server构建 | `docs/audits/m49/d01-20261016` 的 TRX/acceptance；各自 DLL/PDB/源码/包身份分列 |
| 干净包真实HTTP2消费者源 | 上述目录 `consumer/`；只依赖本地生成的 Client/Protocol 包，无 Core/项目引用；宿主是协议回显，不是九模型 Server |
| 本轮门禁 | 完整 solution restore、原 Format Check 和 CI 分片检查由对应提交前真实回执确认；workspace-load warning 单列 |

本地 artifacts、nupkg/DLL、进程原始收据不随 Git 上传；重要计数、原 TRX、哈希、消费者源码和边界已入库。换机后不能继续依赖旧绝对路径，也不能把旧二进制成功算作新源码成功。

## 新机最小复验

先核 PS7、.NET 10 SDK、仓库/分支、submodule、网络代理和授权凭据。工具用 PATH、登记配置和已知小目录发现；旧机器 VS 路径、Ubuntu 工具路径只是历史信息，不能当新机已安装。长命令继续按 AGENTS 设置迭代/数量和墙钟上限、进程身份与自有 TEMP 回收，不直接套用 artifacts 中依赖旧 `D:/source/...` helper 的脚本。

在正确 checkout 中依次运行：

```text
dotnet restore SonnetDB.slnx
dotnet test tests/SonnetDB.Client.Tests/SonnetDB.Client.Tests.csproj -c Release
dotnet test tests/SonnetDB.Core.Tests/SonnetDB.Core.Tests.csproj -c Release --filter "FullyQualifiedName~.IO.|FullyQualifiedName~Protocol.FrameHeaderTests"
dotnet test tests/SonnetDB.Native.Tests/SonnetDB.Native.Tests.csproj -c Release
dotnet format SonnetDB.slnx --verify-no-changes --no-restore --severity warn --exclude extensions/
```

这只是当前片复验，不替代完整 Core/Server、AOT、正式包或全部平台门禁。工程包的复现方法见 D01 审计 README；它们不是已公开的 NuGet 版本。

## 下一片实现顺序

1. **F01 SQL 写入/精确参数 Frame**：当前 `SqlFrameOp.Query=1` 只读，新 Client 拒绝这类多帧操作；设计新 opcode/明确版本与能力拒绝，补服务端参数绑定、写入/适用事务、响应终态和鉴权授权。不得复用编号改变语义，不得回落 REST/HTTP1，不拼 SQL 参数。
2. **R01/R02/R03**：简单关系/measurement 的真实 Core pull 执行；快照、执行上下文、行/字节/期限和取消范围延续到消费结束。处理慢消费、缺 end、未知提交、提前释放和资源归零。不能用现有 MaterializedExecutionResult 或外层切帧冒称全链路有界。
3. **继续 D01/C03**：Native 解除混合 Data/remote 依赖；迁移 C 头文件/构建、各语言 embedded 绑定和包；BASIC 仅远程，旧桥接待删除。把 .NET/C 两条真实链路按模型补齐，再扩 Go/Rust/Python/Java8 JNI、VB6/PureBasic 远程。
4. **发行前单独收口**：Core 旧包兼容/API门禁、Protocol/Client 正式发布清单、NativeAOT、TLS/ALPN、Windows/Linux glibc/musl/macOS及适用x64/ARM64、真实九模型 demo/恢复/性能/干净正式消费者、远端CI。现有门禁没有关闭或降级；允许分批发行，但每批只声明实际通过矩阵。

首批继续只做九模型数据访问。AI/RAG、CDC/通用流处理、备份/用户角色治理、普通32位引擎、外部对象存储和 FFM 非首批必选。Graph 保持 beta；本进程多线程，不新增多进程同目录共享写。确认需求以 `docs/design/connector-embedded-split-requirements-20261016.md` 为准，不再重复旧问卷。

## 保护边界

旧树未暂存的 M42 热点写规划、M47 状态表和 M42 专题是继承内容，未混入 M49 提交；它们可能不会出现在新电脑的这个分支，不能据此认定丢失或重做。M48 原 main 规划保留。只 stage 自有文件/hunk，不 reset/clean/stash-all 共享工作。`parity-results` 及远端引用永不合并或删除。当前授权是实现、验证、代码提交推送；公开发包/Release、部署、付费和对他人发消息不随之扩大。
