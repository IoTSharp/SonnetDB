# SonnetDB Quartz.NET 作业存储

在应用启动前调用 `SonnetDbQuartzSchema.EnsureCreatedAsync` 创建 Quartz 表和索引。已有数据库需授予初始化连接建表权限。远程数据库不存在时，初始化连接需要建库权限。初始化可重复执行，保留已有调度数据。

```csharp
using Quartz;
using SonnetDB.Quartz;

await SonnetDbQuartzSchema.EnsureCreatedAsync(connectionString, cancellationToken);
services.AddQuartz(quartz =>
{
    quartz.SchedulerName = "device-platform";
    quartz.UsePersistentStore(store => store.UseSonnetDB(connectionString));
});
```

重启时沿用调度器名称和作业及触发器标识，再由应用决定如何保留或更新已注册的触发器。组件使用 Quartz.NET 4.8.1、`QRTZ_` 表前缀和 System.Text.Json 序列化器，支持单个活动调度器实例以及重启后的调度恢复。

## 从 Quartz.NET 3.18.2 升级

1. 停止旧调度器并备份调度数据库。
2. 升级应用的 Quartz 依赖及作业代码。独立应用通过 `QuartzSchedulerBuilder.Create` 配置调度器。宿主应用继续在 `UsePersistentStore` 的回调中调用 `UseSonnetDB`，该扩展现在接收 `IPersistentStoreBuilder`。
3. 启动新调度器前调用 `SonnetDbQuartzSchema.EnsureCreatedAsync`。它新增 Quartz 4.8 所需的表和列，保留已有作业、触发器及暂停记录。每项变更独立提交，取消或失败后可再次调用以补齐其余列。升级时由一个应用或维护进程调用。
4. 保留旧库 `SCHED_NAME` 对应的调度器名称及作业类型标识，核对调度状态后启动 Quartz 4.8。回退时一起恢复升级前的备份和应用版本。
