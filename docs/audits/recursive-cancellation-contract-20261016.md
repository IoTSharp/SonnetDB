# 递归 CTE 取消测试的确定性修正

2026-10-16，主监督在 `a3932c49` 上独立处理测试修正，没有合入 #251 生产修改。

#251 扩大回归 `core-sql02` 为 3765/3766、`core-native06` 为 174/175；两次唯一失败均为
`SqlRecursiveCteTests.Execute_ExponentialBranch_CancellationStopsCandidateProbe`。
原测试以 10ms 定时器取消，并精确要求 `RoutineExecutionException`。
实际分页迭代器通过 `SqlQueryResources` 直接抛出 `OperationCanceledException`；
例程轮询则以 `routine_cancelled` 包装同一取消令牌。两种路径都已停止查询。
原失败日志及 TRX 保留，不将旧运行改写为 PASS。

## 确定性复现与修正

根使用已核验的 #252 冻结 Core/Data DLL，在递归成员投影注册测试 UDF，分别于第 1、4
个候选行取消。没有计时器、等待重试或生产测试钩子。保留原精确包装断言时，实际
**0 PASS / 2 FAIL**；均从分页直接抛出取消异常，令牌一致且内存归零，证明问题早于 #251。

最终测试只接受原生 `OperationCanceledException`，或错误码确为 `routine_cancelled`
且内部为该取消异常的包装。它仍要求同一令牌、取消已触发、候选调用数恰好等于注入值、
查询预算和 ambient 资源释放，并验证源表数据及后续查询可用。既有预取消测试继续要求
例程包装，不扩大为接受任意异常。

完整 `SqlRecursiveCteTests` 源文件在独立测试项目中编译并运行，复用同一冻结产品 DLL：
**25/25 通过，零失败/跳过**。这是该整类的验证，不是全 Core 回归。最终 #251 整合后
还需在主监督最终源码上执行相应扩大回归。没有改变生产行为、格式或公共合同。

原始记录在监督树 `artifacts/issue-supervision-20261016/recursive-cancellation/`：
`probe/run02.log`、`baseline/baseline.trx`、`fixed/fixed.trx`、两版 fixture、输入 DLL
及 SHA 回执 `evidence.json`。初次探针因外部程序集不可访问内部预算属性而编译失败，
原日志 `probe/run.log` 保留；改为现有 friend assembly 后复现，不作为产品失败。

提交前必须在本最终树完整执行规定 restore / Format Check；实际退出码和最终树位于
同目录 `gate/`，提交回读写入 `commit.json`。独立修正不修改 #251 原失败证据。
