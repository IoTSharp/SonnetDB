# M49 实施矩阵与首个切片

日期：2026-10-16（Asia/Shanghai）；基线 `67a5c8c8d64c36002aee5ebcfcb40634e7d5f482`。
本表记录实施状态，需求以[确认记录](connector-embedded-split-requirements-20261016.md)为准。

首个工程切片为 C01/C02 的小基线和 R02/R03 的 native SQL reader 生命周期。
只有移除 ABI 层的完整结果副本，不能关闭 R01、R02、R03 或 C03。
后续依次拆分 D01、补齐 F01，并打通 Core pull 执行；不保留混合 local/remote 包门面。

## 九模型操作与当前 Frame 缺口

以下为源码核对，不是运行验收。已有 operation 编号不复用为不同含义。

| 模型 | 基线 Frame | 当前待补齐的数据访问 |
|---|---|---|
| 时序 | `Tsdb/WriteColumnar`；`Sql/Query` | schema、必要索引和其它写操作；端到端有界范围读取 |
| 关系 | `Sql/Query` 只读 | DDL/DML、精确参数、适用事务与多结果；pull 读取 |
| KV | Get/Put/Scan、条件写、GetAndSet/Delete、CAS、Expire/Persist/TTL | 资源/schema、独立删除、计数及有界分页的完整合同 |
| 文档 | Find/Insert | collection/schema/索引、复杂过滤/投影/排序、修改/删除/聚合 |
| 全文 | 无专用 service | 索引/analyzer、检索/评分/高亮、适用组合查询 |
| 向量 | Search | schema/维度/索引、写入更新、过滤与检索生命周期 |
| 对象 | Get/Put | bucket、元数据、Range、条件写、列表/删除、分块/multipart |
| MQ | Publish/Batch/Pull/Ack、Subscribe/Unsubscribe、Nack/OffsetReset | topic/消费组生命周期、操作预算和恢复的完整核对 |
| 图 | Expand | schema/索引、点边 CRUD、遍历/路径/分页；保持 beta |

不得用 SQL 或 REST 可达推断专用 Frame 操作已完成。现有 Server 权限、预算和持久性合同保留。

## C02 首片生命周期合同

1. native result 接管 command 和 reader，创建结果时只读取列元数据，不预取数据行。
2. 消费者每次 next 至多调用一次上游 Read；字符串指针仅在当前行有效，下一次 next 或 free 后失效。
3. EOF 清除当前行、释放 reader 和 command；列名仍可读至 free。EOF 后不得返回最后一行的旧值。
4. 读取失败保留原异常终态，后续 next 继续报告失败，不能转换成成功 EOF；失败和提前 free 都释放拥有的资源。
5. connection close 先终止其活动 result 再释放连接；后续访问已关闭 result 明确失败。不同结果的释放互不影响。
6. 这是单层生命周期合同；尚未证明底层 Core、Server 或网络有界，不据此声明 RSS 上限或批次预算成立。

ABI 新版本、精确类型、参数、每批/累计预算、取消、未知提交与 Frame end 的黄金语料仍按后续切片推进。
对于没有可信确认的写入，继续保留未知提交，不自动重放。

## 平台与语言验收账本

目标为 Windows x64/ARM64、Linux glibc/musl x64/ARM64、macOS x64/ARM64；当前不承诺全部可运行。
首个纵向候选以 Windows/Linux x64 的 .NET/C 为起点，各语言/模型/操作逐项扩展。

| 维度 | 当前状态 |
|---|---|
| Windows x64 托管 native 生命周期测试 | 迁移后 10/10 PASS，含真实命令取消和真实数据库关闭重开；最终补充检查继续在交接记账 |
| Windows x64 NativeAOT 动态库/C 消费者 | 首个实际 AOT 构建 PASS；原 C quickstart 与解压工程包的独立 C 消费者 PASS；不是完整九模型 SDK 验收 |
| Linux glibc x64 托管/AOT/C | NOT_RUN |
| Linux musl、其余 ARM64、macOS | NOT_RUN |
| .NET/C 原生 HTTP/2 Frame SDK | .NET 单帧传输基础已实现；九模型 typed/多帧待补，C 远程 NOT_STARTED |
| Go/Rust/Python 两条产品线 | NOT_STARTED |
| Java 8 远程/JNI | NOT_STARTED；当前 PATH 是 JDK21，不冒称 Java8 验收 |
| VB6/PureBasic 仅远程 | NOT_STARTED；实际授权编译器/HTTP2运行仍 NOT_RUN |
| 性能/真实 HTTP2/故障/正式包/远端 CI/公开发行 | NOT_RUN；仅 C SQL 生命周期有本地工程包消费者证据 |

PowerShell 已核为 7.6.6；dotnet 位置 `C:/Program Files/dotnet/dotnet.exe`，版本由本片回执绑定。
没有安装工具、扫描大目录或启动外部服务。构建仅使用有界 Kernel Job、2 个处理器、禁用共享编译器，保存 PID/创建时间/完整命令行/父链及清理结果。

Ubuntu 的有界 `command -v` 已核得 `/usr/bin/gcc`、`/usr/bin/clang`、`/usr/bin/dotnet`、`/usr/bin/cmake`，进程环境为 64 位 x86_64；尚未执行 Linux 构建或消费者，不能升级为 Linux PASS。首次将 wsl 直接交给通用进程 helper 的参数传递失败（exit 127），改为 PS7 结构化调用后发现成功，原失败保留。

| 最终目标 | 本片嵌入式证据 | 新远程 SDK 证据 |
|---|---|---|
| Windows x64 | Native 托管 10/10、旧 AOT/C SQL 生命周期与独立工程包消费者 PASS；D01新AOT未验 | .NET 单帧传输195/195；九模型 typed 和 C 远程未完成 |
| Windows ARM64 | NOT_RUN | NOT_STARTED |
| Linux glibc x64 | 仅 Ubuntu 工具位置/64位环境已核；运行 NOT_RUN | NOT_STARTED |
| Linux glibc ARM64 | NOT_RUN | NOT_STARTED |
| Linux musl x64 | NOT_RUN | NOT_STARTED |
| Linux musl ARM64 | NOT_RUN | NOT_STARTED |
| macOS x64 | NOT_RUN | NOT_STARTED |
| macOS ARM64 | NOT_RUN | NOT_STARTED |
| Windows x86 VB6 客户端 | 不适用：不构建 VB6 嵌入式/32位 AOT 桥接 | NOT_RUN；仅远程客户端例外 |

## 首片真实证据与限制

本地回执根为 `artifacts/m49-implementation-20261016`。原实现 4 个确定性基线全部 FAIL，修复后的前 3 轮各 8 PASS/1 FAIL（测试 SQL 夹具不符合本库类型/主键/列清单语法），第 4 轮 9/9 PASS；迁移后新增真实命令取消，10/10 PASS。旧失败没有改写。

首个 Windows x64 CMake/AOT 构建通过，独立 C 工程包只携带匹配 DLL、import lib、头文件、demo 和版本/边界说明；解压后的 C 项目未引用仓库工程或运行 dotnet，实际编译/执行通过。它只验收 SQL 生命周期，不是 C08 九模型正式发行。

最终补充了释放后 `RecordsAffected` 的拒绝，托管仍为 10/10 PASS；同一最终源码重新 AOT/CMake 构建通过，无 IL/AOT 或编译警告诊断。`0.0.0-local.m49-lifetime.2` 工程包解压后重新配置、编译、执行通过，包含 C 端关闭后读取 RecordsAffected 的真实拒绝。原 C quickstart 同候选重跑通过。后续增量构建约 4 秒且未再次发布 AOT，生成依赖审计确认 `obj` 内 C# 输入为零。

构建时发现 CMake 把 `obj` 下生成的 C# 也当输入，已排除 `bin/obj`。一次重复发布被主动取消，清理 helper 报 retained identity changed；保留原 ERROR，Job 关闭后对那 12 个记录 PID 的独立核查为零存活。一次 90 秒复跑超时，原 ERROR 和 cleanupComplete=true 分别保留，不能合并成构建 PASS。

完整 solution restore 和 CI 分片覆盖（core=1/rest=44/total=45，零交集）已通过；原样全 solution Format Check 的最终结果在根 HANDOFF 登记。工作区加载警告单列，不宣称没有任何工具警告。

当前仍依赖混合 `SonnetDB.Data`；Core 的 `SelectExecutionResult.Rows` 仍完整物化；同一 native result 的 next/value/free 与父连接关闭要求调用方串行。C API 尚无新的取消入口、精确类型或 ABI 版本协商。模拟缺终态的 IOException 不是实际 Frame 缺 end 测试。以上均不升级为 C02、D01、R01～R03 或整个 M49 完成。

## F01 关系 SQL Execute=2 切片（2026-10-17）

新独立 Client 已接通关系表自动提交写入：显式 payload v1、精确参数/类型化 NULL、4 MiB 累计输入、HTTP/2 exact、权限与准入、完整终态和未知提交无重试。支持 CREATE/DROP TABLE、无 RETURNING 的 INSERT/UPDATE/DELETE；事务/多结果/其它模型及 Preview 明确拒绝。协议与类型表见[SQL Execute](../sql-execute-frame.md)，真实宿主重开、原子性/断连与最终计数见[本片审计](../audits/m49/f01-20261017/README.md)。

早期尝试自动迁移旧 ADO 导致 RETURNING 与旧协议回归，已撤出这些修改，旧 Data/ADO/session 行为保持基线；其迁移是尚未完成的 D01/F01 工作。不能用新 Client 的局部通过关闭旧路径、多结果/事务、九模型 F01 或 R01～R03。Core 仍完整物化，TLS/ALPN、NativeAOT 发布运行、跨平台、性能和正式消费者未验收。

## D01 共享协议与 .NET 远程依赖切片

`SonnetDB.Protocol` 已独立于引擎，Core 引用它；`SonnetDB.Client` 只依赖 Protocol。相关托管类型直接改变程序集归属，消费者须重编译，Frame v1/磁盘字节不变。新远程入口验证实际 HTTP2、单帧完整性/预算和总期限，拒绝重定向/回落/已知多帧操作；取消、客户端关闭和传输失败保留未知结果。

本片 Client 195/195（含5个真实h2c）、原Core定向175/175、Native托管10/10及Server零警告构建通过；两个本地工程包的独占缓存消费者真实h2c通过。[入库证据](../audits/m49/d01-20261016/README.md)保存原TRX、显式计数和源码/运行/包哈希。首片旧冻结 Counters 空数组另用[历史补充](../audits/m49/native-lifetime-20261016/README.md)纠正，并标清临时 EXE 已回收，不覆盖原记录。

这些证据不升级为真实九模型 Server、Core 有界、SDK AOT、正式包清单、Core旧包兼容门禁、TLS/ALPN、其它平台或远端CI通过。D01/F01/R01～R03 继续未完；用户要求当前片交付后换电脑，后续从[换机交接](../handoffs/m49-next-computer.md)实施 F01。
