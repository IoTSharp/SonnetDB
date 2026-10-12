# CAP 4.1.0 本地验收

日期：2026-10-09（Asia/Shanghai）。接续错误会话 `01a11dd5-4863-71f3-a578-dccfa4a7b25a`，原始基线 `5424b66672e37f49a2a5633fdde0b54a9b1f0290`，本地分支 `codex/cap-integration`。

## 本次交付

按用户最新指令，将原有两个未提交适配项目合并为一个 `SonnetDB.CAP` 项目/包；存储和传输分别注册，统一通过 Data SDK 支持嵌入式与远程访问，不依赖 EF Core。新增幂等文档索引和持久消费组登记，提供单 collection 业务+Outbox 事务。合同见 [设计说明](../design/cap-integration.md)。

## 已执行的验证

| 检查 | 实际结果 | 证据范围 |
|---|---|---|
| CAP 专属测试 | 34/34，0 失败/跳过 | 真实嵌入式、Kestrel HTTP Server、CAP 宿主；`artifacts/cap-analysis-20261009/tests/cap-final.trx` |
| CAP 行覆盖率 | 1227/1366，89.82% | 适配程序集，含 source-generated context；Cobertura 目录 `822e6e33-e71f-4722-b0d3-ded951da994b` |
| 非生成源码行覆盖率 | 558/592，94.26% | 从同一 Cobertura 按文件/行去重，排除 `.g.cs`/source generation 路径 |
| 既有 Core 文档/MQ 回归 | 296/296，0 失败/跳过 | `cap-core-regression.trx`，未运行完整 Core 测试集 |
| 既有 Server 定向回归 | 15/15，0 失败/跳过 | `cap-server-regression.trx`，未运行完整 Server 测试集 |
| 发布产物合同 | 17 项通过 | 含缺失 CAP 包的拒绝；合成 ZIP/包夹具，不能当真实发行物验收 |
| 单包 `dotnet pack` | 成功 | 真实 `SonnetDB.CAP.4.1.0.nupkg`，README、DLL/XML 和 nuspec 已读取 |
| 包依赖审计 | 通过 | 直接依赖 `SonnetDB >= 4.1.0`、`DotNetCore.CAP >= 10.0.2`；恢复图无 EF Core/MongoDB Driver/Newtonsoft.Json/Dapper |
| solution restore | 成功 | 43 项目恢复/已有资产；不是完整 solution build/test |
| 原 CI format | 成功 | `dotnet format SonnetDB.slnx --verify-no-changes --no-restore --severity warn --exclude extensions/`；workspace warning 保留 |
| CAP 项目 format | 成功 | 额外验证独立 CAP 项目，补充 CI 排除 extensions 的范围 |

CAP 包路径：`artifacts/cap-analysis-20261009/cap-package-4.1.0/SonnetDB.CAP.4.1.0.nupkg`。该次候选 SHA-256 为 `D26F792A2020021310E8526D5AF274FF2B2AEA0FE732FD5D61F2C9B45A6BBE00`，依赖收据为同目录 `package-audit.json`；若最终格式整理后重打包，以新收据为准。

生产项目的 Core/Data/Server/CAP 编译开启 trim/AOT 分析，相关构建没有 IL/AOT 警告。CAP 上游 serializer 和 subscriber discovery 的反射边界仍在，未进行完整 CAP NativeAOT 发布。

## 保留的失败与正式发布阻塞

- 接续开始的测试编译缺 Data 显式引用，随后缺内部 ServerJsonContext 访问及 xUnit 异步断言问题，均已修复。首个完整运行 24/28 通过，实际发现 CAP 默认 GroupConcurrent=0 被误拒绝；修正为允许 0/1 后完整发布/订阅通过。
- 恢复测试发现尚未 ACK 的消费组没有持久登记，会在其它组 ACK 后重开时失去旧消息。新增幂等登记和现有 ACK WAL 复用后，未确认慢组的两条消息与已确认组下一 offset 在嵌入式和 Server 重启后均验证保留。
- 全量 `eng/release.ps1 -Tasks nuget -Version 4.1.0` 失败于第一个 Core 包。Core 现有配置仅豁免 4.0.0，4.1.0 又使用 3.0.1 API 基线，报告 CP0002（旧构造函数/Deconstruct 等不存在）及 CP0011（SQL TokenKind 枚举值变化）。这些涉及本片未修改的既有 API；未压制检查或更改基线。完整八包候选库存未通过。
- `gh auth status --hostname github.com` 显示 maikebing 的 keyring token invalid。未读取或输出 token，未提交到远端、创建 tag、调度 CI 或向 NuGet 发布。正式 4.1.0 还须恢复认证、解决配套 Core/Data 发布门禁，并取得该提交的 CI/AOT/Parity 等发布证据。
- release recovery 保留旧 4.0.0 七包合同，从 4.1.0 开始要求 CAP 第八包；PowerShell 语法与四个版本的真实声明片段检查通过。初次微检查同时匹配 `=` 与 `+=` 而拒绝，修正观察器为精确 `=` 后通过，未改变正式脚本语义以迁就检查。

## 下一步和边界

先解决正式 4.1.0 的依赖包兼容性基线/发行门禁，再按已有预演与发布流程交付。候选包不能单独被宣称可安装使用，因为它依赖尚未由本次流程发布的 Data/Core 4.1.0。

本地正常停止/重开不是强杀或掉电证明；不提供跨 collection 事务、跨进程消费者租约或业务 exactly-once。Linux、固定硬件、长稳、安装包和全量发行流水线另验。`.vscode/settings.json` 为接收时已有外来修改，不纳入 CAP 提交；workbench 暂停状态和 parity-results 保持。
