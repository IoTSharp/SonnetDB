# M49 换机接续入口

更新：2026-10-17，Asia/Shanghai。**本次交付为 F01 关系 SQL 自动提交写入切片；M49 与其 15 包仍未整体完成。**

## 取得正确分支

- 仓库：GitHub `IoTSharp/SonnetDB`；分支：`codex/m49-connector-batches-roadmap`。先取得此分支最新提交及固定 submodule，再核 `git branch --show-current`、`git rev-parse HEAD`、`git status --short`。准确交付 SHA 以分支最新提交和远端回读为准。
- 本片基线为 `39437db0189e9f8acbc3efebf890087ed17052f6`。历史 Native 首片为 `1d02c1c5f410b2ca90e83c56fd9993e52c1f77f4`，D01 为 `6a606c6ba4170f775f1a3fe6b3c9e333f09f2409`；它们均不是接续时应固定停留的 HEAD。
- 旧机实施目录是 `C:/Users/mysti/.codex/worktrees/connector-batch-roadmap/SonnetDB`，默认 `D:/source/SonnetDB` 是另有修改的共享 main。因此此前在默认目录里看不到 `embedded/`；它已提交在此分支的仓库根，并没有丢失。新电脑使用自己的正常 checkout 路径即可。
- 先读根 `HANDOFF.md` 最新章节、`AGENTS.md`、[确认需求](../design/connector-embedded-split-requirements-20261016.md)、[实施矩阵](../design/m49-implementation-matrix.md)及 [SQL Execute 协议](../sql-execute-frame.md)。不要复制旧 TEMP、凭据或构建 DLL 作开发基线。

## 本分支实际已落地

1. `embedded/native/SonnetDB.Native` 是唯一 AOT 导出工程。NativeResult 持有 reader/command，按 next 读取，不再在 ABI 层全量复制；EOF、失败、取消、提前 free、父连接关闭均清理资源。Core 自身仍完整物化。
2. `src/SonnetDB.Protocol` 已脱离引擎，承载 Frame 信封/opcode/Span IO。类型迁移没有旧 type forwarding，消费者须重编译；没有修改磁盘和既有 Frame v1 字节。
3. `connectors/dotnet/SonnetDB.Client` 只依赖 Protocol。单帧入口验证实际 HTTP/2、身份/预算/终态，拒绝 HTTP1、重定向和回落，整请求期限覆盖响应读取，取消/关闭释放在途流，未知结果不重试。
4. 本次新增 `Sql/Execute=2`、独立 payload v1、精确参数/类型化 NULL 和 committed/影响行数终态。允许关系表 CREATE/DROP TABLE、无 RETURNING 的 INSERT/UPDATE/DELETE；Server 复用 Write 权限、SQL admission、AST 参数绑定和既有引擎原子性。
5. 新测试直接通过新 Client 访问真实 h2c 产品 Server，验证精确值、完整宿主重开、权限、约束整句回滚、拒绝能力/畸形输入，以及引擎已提交后断响应仍保持未知且没有重试。最终计数和绑定身份见入库审计。

## 仍未完成及已纠正的中间尝试

- Execute 首片显式拒绝事务控制、RETURNING、过程/多结果、只读 SQL、其它 DDL/模型操作与 Workbench Preview。新 Client 尚无 SQL 多帧查询 API。禁止借旧 REST 补新 Client 的能力缺口。
- 中间版本曾把旧 ADO 非事务写直接切到单响应 Execute，完整 ADO 153 项中出现 31 失败，含已有 RETURNING 功能回归和旧 REST/版本断言。**已撤出对 RemoteConnectionImpl、FrameChannel 和原 ADO 测试的修改，最终逐字与基线一致。** 旧混合 Data/ADO 的 REST/session 路径继续存在；先补 Frame 多结果/事务，再迁移，不能用拒绝已有功能虚称重构完成。
- Core `SelectExecutionResult.Rows` 仍全量物化。R01/R02/R03、Native 脱离混合 Data、其余 embedded 绑定/BASIC 旧桥接迁移、各语言原生远程、九模型其它操作、性能/恢复及正式发行继续待办。
- TLS/ALPN、SDK/Server NativeAOT 发布运行、其它平台/语言、完整 Core/Server、固定硬件/长稳、正式包消费者与远端 CI 均不能从本片局部通过推断。

## 证据与新机复验

| 范围 | 入库证据 |
|---|---|
| Native 历史托管 10/10 与 AOT/C 工程消费者 | `docs/audits/m49/native-lifetime-20261016`，原 TRX/补充计数；旧临时 EXE 已回收 |
| D01 Client 195/195、Core 定向 175/175、Native 10/10、干净工程包 h2c | `docs/audits/m49/d01-20261016`，消费者是协议回显宿主 |
| 本次 F01 新 Client、真实产品 Server、旧 ADO 回归 | `docs/audits/m49/f01-20261017`，以 `acceptance.json` 最终计数、源码/运行哈希和 TRX 为准，首轮失败保留 |

完整原始进程/命令收据在旧机 `artifacts/m49-f01-20261017`，重建不依赖它们。新机重新核 PS7、.NET 10、submodule、代理与授权凭据；编译器仅按 PATH/显式配置发现，不扫描大目录。长命令须有墙钟/次数边界、进程身份与完整自有子树清理。

```text
dotnet restore SonnetDB.slnx
dotnet test tests/SonnetDB.Client.Tests/SonnetDB.Client.Tests.csproj -c Release
dotnet test tests/SonnetDB.Tests/SonnetDB.Tests.csproj -c Release --filter "FullyQualifiedName~SqlExecuteFrameEndpointTests|FullyQualifiedName~SqlFrameEndpointTests|FullyQualifiedName~RemoteAdoHttp2TransportTests"
dotnet test tests/SonnetDB.Native.Tests/SonnetDB.Native.Tests.csproj -c Release
dotnet format SonnetDB.slnx --verify-no-changes --no-restore --severity warn --exclude extensions/
```

每次提交仍须在最终源码上完整 restore 和原样 Format Check；不能拿本片 PASS 替代新机复验。正式包 API/兼容性门禁保持开启，没有因 Protocol 拆分而关闭或降级。

## 下一会话顺序与保护边界

1. SQL Frame 多结果/RETURNING 的协议与新 Client 解码；再补适用事务、取消/未知提交和权限。完整合同齐备后迁移旧 ADO，逐项复验，避免丢失现有功能。
2. R01/R02/R03 接简单关系/measurement 的真实 Core pull：快照/上下文/预算/取消保留至消费结束，不在完整 Rows 外包装分页。
3. 继续 D01/C03：Native 脱离混合 Data，迁移 C 头/包/其它 embedded 绑定，删除 BASIC 嵌入式桥接；逐模型打通 .NET/C，然后扩 Go/Rust/Python/Java8 JNI 与 BASIC 远程。
4. 每个正式批次独立收口：AOT、真实 HTTP2、模型 demo、恢复/性能、干净正式消费者和目标平台。首批仍只做九模型数据访问，AI/RAG/CDC/通用治理、外部对象存储和普通32位引擎后续。

旧机 CHANGELOG/ROADMAP 的 M42/M47/D01 继承改动和 `docs/design/m42-relational-hot-write-concurrency.md` 未混入提交；不能 reset/clean/stash-all。只暂存本次自有路径/hunk，parity-results 及远端引用永不合并或删除。当前授权为实现、验证、交接、commit/push；公开发包、Release、部署、付费或对他人发消息另行处理。