# 本地持久订阅自动投递

`FileStreamingSubscriptionDispatcher` 驱动已打开的 `FileStreamingSubscription`，以稳定 `DeliveryId` 串行调用宿主处理函数；函数正常结束后才持久 ACK，普通处理异常按 `RetryDelay` 重投。订阅已有的 `MaxDeliveryAttempts` 保持生效。默认耗尽策略保留批次；显式 `DeadLetter` 策略通过已有 revision、delivery ID 和 attempt 条件隔离。它不自行启动后台任务，宿主显式调用 `RunAsync`。

`FileStreamingDispatcherOptions` 同时限制成功/隔离批次数、总投递次数、运行墙钟时间及单次处理时间。完成、暂停、数量耗尽和超时返回明确原因；调用方取消抛出取消异常。存储提交异常仍向上抛出，可能已提交的 ACK 不能被解释为业务回滚。

处理函数必须响应取消。函数超时或被取消后，驱动器停止且不 ACK；同一订阅对象的执行租约保留至函数实际结束，避免下一次驱动与仍运行的函数并发。宿主可等待 `HandlerCompletion` 确认收敛；永久忽略取消的函数可能永远不结束，驱动器不能强制终止用户代码。宿主须独占该订阅消费入口，不能同时直接读取或 ACK，也不能在函数仍使用资源时关闭资源。

处理可能成功后未 ACK，因此外部副作用仍需幂等。持久窗口可先调用 `ApplyBatchAsync`，利用最后批次身份和内容摘要恢复去重，再由驱动器 ACK。驱动器不改变订阅文件格式，不提供远程调度、分布式租约或跨业务事务。真实文件组合入口见[样例](../samples/SonnetDB.CdcStreamingJourney/README.md)，证据见[本轮报告](audits/roadmap-parallel-next-20261002.md)。
