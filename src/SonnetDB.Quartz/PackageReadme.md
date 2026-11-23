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

重启时沿用调度器名称和作业及触发器标识，再由应用决定如何保留或更新已注册的触发器。组件使用 Quartz.NET 3.18.2 的 `JobStoreTX`、`QRTZ_` 表前缀和 System.Text.Json 序列化器。验证范围为单个活动调度器实例及重启后的调度恢复。
