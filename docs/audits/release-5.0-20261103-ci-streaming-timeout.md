# 5.0 旧 cb158831 候选 CI 失败核验及修复方案评审

核验日期为2026-11-03。本报告固定旧候选 `cb158831616a5c69992e62152b0746751691ede4`，不把其结果继承给后续29f或其它提交。只读取得一次十二类工作流快照、CI/CodeQL/Publish jobs、原始失败日志和全部三组TRX；没有dispatch、取消、重跑或修改远端工作流。

## 准确状态

- [CI 41628291729](https://github.com/IoTSharp/SonnetDB/actions/runs/41628291729) 为failure。唯一失败job `125891116888` 是 `Build & Test (ubuntu-latest)` 的Test步骤。
- Windows Core、Windows rest、Format Check及Windows、Linux x64、Linux ARM64三个NativeAOT jobs均success。
- [CodeQL 41628301266](https://github.com/IoTSharp/SonnetDB/actions/runs/41628301266) 的C# Analyze job成功。本次没有下载SARIF或重新统计告警，不能据此声明零开放告警。
- [Publish 41628789827](https://github.com/IoTSharp/SonnetDB/actions/runs/41628789827) 的NuGet构建及两平台bundles成功。该run为workflow_dispatch，正式推NuGet及GitHub Release jobs按合同skipped，不属于正式分发成功。
- 同SHA其它九类工作流在该次快照均success；合计十一类成功、CI失败，不能称十二类READY。

## 原始失败

Ubuntu的唯一失败为：

```text
SonnetDB.Core.Tests.Streaming.FileStreamingDeadLetterTests.
OpenAsync_WithResignedPendingIntentMismatch_FailsClosedAndPreservesOriginalSpool(change: "checkpoint")
```

TRX记录耗时 `00:00:02.5981447`，时间 `2026-11-03T11:41:21.6741176Z` 至 `11:41:24.2747771Z`。

```text
System.TimeoutException : 打开持久订阅超过存储操作超时边界。
---- System.Threading.Tasks.TaskCanceledException : A task was canceled.

at SonnetDB.Streaming.FileStreamingSubscription.OpenAsync(...) in src/SonnetDB.Core/Streaming/FileStreamingSubscription.cs:313
at SonnetDB.Core.Tests.Streaming.FileStreamingDeadLetterTests.OpenAsync_WithResignedPendingIntentMismatch_FailsClosedAndPreservesOriginalSpool(String change) in tests/SonnetDB.Core.Tests/Streaming/FileStreamingDeadLetterTests.cs:273
----- Inner Stack Trace -----
at SonnetDB.Streaming.FileStreamingSubscription.WriteStateAsync(...) in src/SonnetDB.Core/Streaming/FileStreamingSubscription.cs:983
at SonnetDB.Streaming.FileStreamingSubscription.WriteStateAsync(...) in src/SonnetDB.Core/Streaming/FileStreamingSubscription.cs:986
at SonnetDB.Streaming.FileStreamingSubscription.OpenAsync(...) in src/SonnetDB.Core/Streaming/FileStreamingSubscription.cs:301
```

完整原始message、stack、TRX哈希及各项目计数保存在 ignored `artifacts/release-5.0-20261103-pipeline/new-cb158831/ci-failure-analysis/result.json`，简表见同目录 `summary.json`；原始日志 `failed-job.log` 与三组TRX均保留。

## 源码解释及方案边界

失败行273是首次 `OpenAsync`，尚未发布事件、持有检查点锁或注入损坏。该fixture把持久 `OperationTimeoutMilliseconds` 固定为2,000ms，并设另一个45秒测试总截止。`OpenAsync` 正确把这2秒限制施加于真实初始化、检查点保存和状态写入；原栈取消发生在 `WriteStateAsync` 的异步写/flush路径。证据证明这次初始化超过fixture配置边界，没有证明生产丢数据或损坏校验返回错误。

主智能体提出的最小修复经本智能体独立评审：只将本fixture的操作边界改为10,000ms并修正中文注释，初开和重开继续使用同一持久选项。保留45秒总截止、真实held-lock导致Timeout的全部message/inner分支断言、`Assert.False(deadline.IsCancellationRequested)`、重签损坏后的 `InvalidDataException`，以及原spool和checkpoint未前进的断言。此方案保留测试的fail-closed和数据保留意图，不更改生产默认、取消映射、真实刷盘或持久恢复语义。

需要对最终修改实际执行全部 `FileStreamingDeadLetterTests` 及相关checkpoint/subscription回归，并在最终新SHA取得完整CI和现行十二类门禁。本报告只是失败定位及方案评审，不把尚未取得的修复后结果写成PASS，不通过重跑旧失败覆盖历史。
