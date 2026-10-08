# WB72 Native CIM 阶段观测

日期：2026-10-08。WB72 已实现 snapshot 路径的有界阶段观测并通过静态独审，修正后的单项 PowerShell 夹具通过；完整定向测试和最终集成未执行。冻结窗口到期后以失败检查点关闭，本会话计数为 1/5。0 actual、0 产品构建，不证明 WB70 慢查询原因或 Native 数据库恢复。

## 已实现合同

复用 WB66 的 invocation cache/20s/160 查询预算及 WB67 三标量。仅围绕原 lookup 与 child-enumeration 两个 CIM 调用点观察，不增加查询、协议行、helper 次数、停止权限或运行时选择行为；close/kill 的观测为 null。

`cimObservation` 含 schemaVersion=1、state=complete/incomplete/unknown 和最多 160 个 operations。每项固定为 ordinal、phase、startedMilliseconds、elapsedMilliseconds、outcome、resultCount；四类 phase 为 self-handshake、parent-chain、seed-lookup、child-enumeration。结果数仅允许 lookup 0..1、children 0..64，缺失为 null。时钟相对 helper 原 Stopwatch，超过 20 秒的耗时不截断。观测累计记账达到 100ms 后停止增加槽，排除 CIM 等待；同步调度不能保证精确在上限返回。

cache hit、null cache 及 children rows 后续 BFS 复用不生成虚假查询。查询前建槽，finally 保留 returned/threw 和耗时；随后原 cache/budget 守卫仍可拒绝。观察异常不替换原 primary。Node 仅从决定原 success/error 的同一 envelope 读取 own data descriptors，在 100ms/160 项限额内投影固定字段至 helper record/compact；非法、缺失、超时或无身份准入的观察保持 unknown，不授动作权限。

## 验证与保留失败

工具及源码独审报告保存在 `artifacts/wb72-native-cim-observation-20261008`。原 fixture 对 20s 墙钟与追加失败前缀覆盖不足的静态拒绝保留，fixture 已补齐两种断言。首次 PS 微试 exit0/declaredPassed1 但打印 `PASS s`，实际未执行 self 场景，明确保留为语义失败。修复采用 string[]、unknown case 拒绝及 executedCases/simulatedQueries；最终 fixture SHA 为 `7C44E1F4E49700A0FC67D38CCCBEE8CAF3FDEF56AF19F12084EE8F3EC95ABB24`。

修复后原窗口唯一再试于 00:57:50.9218125Z 结束：exit0、`PASS self simulatedQueries=1`、executedCases=[self]、1/1、actualCimQueries0、childLaunches0；原失败日志和静态漏检未倒写。最终 PS AST 0 错误。Node micro、PS14、Node 套件、restore、完整 format 与提交均 NOT_RUN。

root 截止 01:25Z，接续心跳于 02:29:53Z 到达；不在过期合同上补跑测试。三个 managed wrappers 的保留句柄退出均为 0，closure 观察身份审计为 13 identities/3 retained/0 survivor/reuse/unknown、0 stop/delete。短命/启动 shell、未观察的瞬时后代与审计器退出边界保持；whole-session orphan-freedom、processIntegrity、rootClosureTimely、localIntegration 和 overall 均 false。

## 下一步

WB73 应冻结新的本地验收与集成窗口，重新绑定这六个源文件、原静态报告及正确微试，完成 Node micro、PS14 与 Node 定向套件，再由根串行完成 owned HANDOFF 私有候选、最终树完整 restore/CI format 和本地提交。该任务验收已有实现，不包装新功能；0 actual，不重跑 WB70。原 WB70/WB71、三宿主、安装、AOT、固定硬件、长期和发布证据保持分列，Graph Beta。
