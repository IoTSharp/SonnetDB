# M43 持久订阅任务目录本地合同

本切片为本机文件订阅提供一个有界任务目录。`FileStreamingTaskCatalog` 保存任务标识、相对目录名、`StreamingSubscriptionDefinition`、`FileStreamingSubscriptionOptions` 和单调目录 revision；注册、更新和删除都要求调用方提供当前 revision，陈旧命令会拒绝且不写文件。

目录使用 `tasks.catalog.lock` 取得单执行者文件锁，状态写入 `tasks.catalog.json` 时先以 source-generated JSON 序列化并保存 SHA-256，再通过同目录临时文件替换。缺失文件表示空目录；坏 JSON、未知字段、哈希不匹配、重复身份、超出条目或字节边界均 fail closed，不会重置或覆盖原文件。

任务目录名必须是目录根下的相对路径，不能包含盘符、绝对根或 `.`/`..` 段。分页按任务标识的 Ordinal 顺序返回，续页游标绑定目录 revision；发生任何写入后旧游标失效，页大小与总文件大小均有配置上限。

目录不复制 spool、检查点、watermark、投递次数或暂停状态。`OpenSubscriptionAsync` 每次按已登记定义调用真实 `FileStreamingSubscription.OpenAsync`，因此订阅自己的定义/容量校验和单执行者锁仍然生效；`GetStatusAsync`、`PauseConsumptionAsync`、`ResumeConsumptionAsync` 都从该真实订阅读取或提交运行状态。此本地合同不提供分布式租约、远程调度、自动后台重发或 exactly-once 业务事务。
