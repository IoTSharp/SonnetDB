# 下一批并行实现与分级证据

本轮于 2026-10-02 开始，验证延续至 2026-10-03（Asia/Shanghai）。基线为本地 main 的 `6072f770`，在 `codex/roadmap-parallel-next-20261002` 实施；前轮 `48053e69` 和 `6072f770` 的文档排序、持久目录和会话窗口不重复交付。本轮没有推送、发布、部署或远程合并。

## 缺口与独占分工

| 智能体 | 实际缺口 | 独占实现 |
| --- | --- | --- |
| stream_dispatch | 订阅已有手动 Read/ACK/条件重试，缺少自动驱动 | 新增 `FileStreamingSubscriptionDispatcher`、独立 options/result 和 19 个测试 |
| cdc_stream_bridge | 组合旅程只有固定副本导出，CDC spool 与流缺少恢复接线 | 新增 `CdcStreamingBridge`、独立源生成状态、订阅 receipt partial 和 28 个测试 |
| graph_materialization | 显式预算直接拒绝 Graph 源，raw 节点/边先生成全 List | 修改两个 Graph 执行器，新增 46 个测试 |
| 主智能体 | 公共准入、组合入口及证据一致性 | `SqlExecutor`/`SqlMaterializationContract`、既有预算合同、`--bridge` 样例、2 个组合测试、文档/索引/验证/提交 |

三个智能体同时实施且无文件归属冲突。Graph 作者审查 CDC，dispatcher 作者审查 Graph，CDC 作者审查 dispatcher 与样例。审查促成 Windows 路径/派生 lease 保护、缺失桥接状态拒绝新基线、多事件前缀回收后的恢复，以及关闭资源前等待 callback 收敛。首次编译的命名空间/测试调用错误和首次专项的不受支持 fixture 语法已修正，再运行专项全部通过。

## 最终合同

- [Graph 累计物化准入](../benchmarks/m42-graph-result-bounds.md)：直接原生节点/边和固定一跳的标量过滤/投影/分页逐行收费；metadata 共用根预算，复杂路径预检拒绝。图 snapshot/pages 和瞬时分配仍独立于结果估算。
- [自动投递](../m43-streaming-dispatcher.md)：数量和墙钟双界、串行失败重投、成功后 ACK；耗尽默认保留或显式条件 DLQ。超时/取消后 callback 实际结束前保留同对象消费租约，宿主独占消费且需响应取消。
- [CDC→流桥接](../m43-cdc-streaming-bridge.md)：专用单分区 outbox、稳定映射及目标 receipt，交付证明先于源 ACK；已完成 receipt 在消费者回收后保持。未知 receipt 且目标帧已回收须人工恢复交接，状态缺失不能重新初始化。
- [真实组合样例](../../samples/SonnetDB.CdcStreamingJourney/README.md)：新增 `--bridge`，实际文档 change feed 经桥接/自动投递进入持久 COUNT，故意在窗口提交后抛出处理异常，核对重投去重及两次重开后的位点。

## 验证事实

| 检查 | 结果与范围 |
| --- | --- |
| 工具 | PowerShell `C:\Program Files\PowerShell\7\pwsh.exe` 7.6.6；dotnet `C:\Program Files\dotnet\dotnet.exe` 10.0.401 |
| 新切片与旧预算定向 | Release 136/136，0 skipped：Graph 46、bridge 28、dispatcher 19、真实组合 2、既有物化预算 41 |
| 完整 Core 回归 | `dotnet test tests/SonnetDB.Core.Tests/SonnetDB.Core.Tests.csproj -c Release --no-build --no-restore`，5926/5926，0 skipped，4 分 20 秒 |
| 完整 Release | `dotnet build SonnetDB.slnx -c Release --no-restore --disable-build-servers -m:1 -p:BuildInParallel=false -p:UseSharedCompilation=false`，0 warning / 0 error |
| 真实托管样例 | `dotnet samples/SonnetDB.CdcStreamingJourney/bin/Release/net10.0/SonnetDB.CdcStreamingJourney.dll --bridge` 返回 `PASS_LOCAL_ONLY bridge source_offset=4 target_sequence=3 window_count=4 redelivery_attempt=2 pending=0` |
| 首次完整 restore | `dotnet restore SonnetDB.slnx` 通过 |
| Native AOT 发布构建 | `dotnet publish samples/SonnetDB.CdcStreamingJourney/SonnetDB.CdcStreamingJourney.csproj -c Release -r win-x64 --self-contained true -p:PublishAot=true`，原生代码生成成功，0 warning / 0 error；本地编译，不是外部发布 |
| Native AOT 实际运行 | `SonnetDB.CdcStreamingJourney.exe --bridge`，同托管样例返回源 4、目标 3、COUNT 4、attempt 2、pending 0；反射 JSON 关闭 |
| 最终完整 restore/format | 再次 `dotnet restore SonnetDB.slnx` 和 `dotnet format SonnetDB.slnx --verify-no-changes --no-restore --severity warn --exclude extensions/` 均 exit 0；format 检查 2149 个文件，无修改 |
| 差异与索引 | `git diff --check` 通过；十四能力索引所有 evidence 路径存在 |

首次 format 找到样例对象初始化器的四处换行，修正后重新完整 restore/format。诊断输出仅有 Docker `.dcproj` 没有语言关联的工作区提示，不是 C# 编译/AOT 警告。成功命令输出、Native AOT 二进制 SHA-256 和进程候选身份元数据保存于[本轮证据](roadmap-parallel-next-evidence-20261002/summary.json)。归档文本统一为 UTF-8/LF 并去掉末尾空行，实质 stdout/stderr 未变。

## 证据等级与未完成门禁

| 等级 | 本轮证据 |
| --- | --- |
| mock/可控失败 | callback 故意失败/忽略取消，以及内部持久边界中断注入；用于合同和恢复顺序，不能充作生产耐久性 |
| 本地真实嵌入式 | 真实文档 change feed、spool、订阅、窗口和文件重开，托管及 win-x64 Native AOT `--bridge` 样例均已实际运行 |
| 本地真实 HTTP 服务 | 本轮未执行新 HTTP/Frame E2E，不把嵌入式路径称为远程端点证据 |
| 远程 | 未执行 parity、远程传输/租约或多机故障验证 |
| 生产门禁 | 未执行固定硬件、掉电、168 小时 mixed workload、七天 scheduled 或业务副作用事务 |

M42/M43 保持进行中。桥接不是跨目录原子事务或业务 exactly-once；自动驱动不能强制终止不协作的宿主函数。状态路径拒绝 reparse/链接别名，源 ACK、目标发布和消费须按合同独占。旧文件格式未改，新增状态拥有独立版本化边界。

## 执行边界与资源

主智能体集中执行编译，避免共享 obj 并发修改；使用禁用 build server/node reuse 的环境、单 MSBuild worker 和两颗逻辑处理器的运行配置。每个长命令设置明确墙钟上限和最大轮询次数，每轮一秒阻塞等待，记录 PID、创建时间、完整命令行及父进程；取消标记可提前停止。子智能体未创建外部长进程、下载或独占 MCP 服务，未使用 Graphify，未触碰 LaneApp。

最终只读进程快照核对最初 286 个候选身份，当前仍存活的候选为 0。归档交叉复核发现临时 runner 用历史父 PID 推断子树，未核对当前父进程身份，因 PID 复用混入另一项目的 21 个身份；已从交付列表排除该子树，保留 265 个候选并明确标为归属未完全验证。runner 的清理虽核对目标 PID、创建时间、命令行及父 PID，但该条件不足以证明任务归属；日志没有逐次终止动作，无法据此确认此前清理是否影响了独立进程。不再使用该临时 runner，本轮测试与构建结果不因此提升为进程归属合规证据。

已逐项核实本轮专属临时目录没有 reparse 项且只包含本轮日志、TRX、执行脚本和 Native AOT 输出；自动审批两次以 `blocked by policy` 拒绝递归删除，因此 `D:\source\SonnetDB\.codex-temp\parallel-next-20261002` 保留，未绕过策略改用其它删除工具。
