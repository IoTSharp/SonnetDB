# M43 文档源自动捕获调度器本地合同（2026-10-01）

核查日期：2026-10-01。本记录只证明本地 Core 中的有界源捕获调度切片，不标记 M43 #385~#390、远程复制或 M43 总里程碑完成。

## 交付范围

新增 `SonnetDB.Cdc.CdcDocumentSourceCaptureScheduler`，在调用方任务中连续推进已有的 `CdcDocumentSourceCapture`：

- `CdcDocumentSourceCaptureScheduleOptions.MaxBatches` 和 `MaxEvents` 同时限制一次运行的最大循环次数与持久化事件数；事件数达到上限时，调度器把最后一批读取上限缩小到剩余配额，不会超额追加。
- `Interval` 可选地限制相邻批次的等待时间（零表示立即推进），等待和每批开始前都响应取消令牌。
- `RunAsync` 没有后台线程或无界循环，只执行配置的有限批次；源返回 `HasMore=false` 时立即结束。
- 返回 `CdcDocumentSourceCaptureScheduleResult`，包含起止 source sequence、累计事件/批次数、`HasMore`、达到批次或事件上限的标志以及逐批 `Progress`。
- 捕获器增加同程序集的有界批次入口，复用原有 spool append、连续位点校验、失败回滚和重开恢复合同；现有无参数 `CaptureAsync` 语义不变。

## 本地证据

`tests/SonnetDB.Core.Tests/Cdc/CdcDocumentSourceCaptureSchedulerTests.cs` 覆盖：

1. 空文档源只执行一次空批并返回无后续工作；
2. 配置批次大小为 2、事件上限为 3 时跨两批推进且只追加前三个事件；
3. 已取消令牌在开始前阻止捕获，spool 保持为空；
4. 首次运行后关闭并重开数据库、捕获器和 spool，从 high-watermark 继续追加剩余事件并保持连续 sequence。

定向验证：

```text
dotnet test tests/SonnetDB.Core.Tests/SonnetDB.Core.Tests.csproj --configuration Release --no-build --no-restore \
  --filter FullyQualifiedName~CdcDocumentSourceCaptureSchedulerTests
通过：4，失败：0，跳过：0（net10.0 Release）
```

## 边界和后续工作

本切片只负责编排单源、单实体、单 schema、单分区的本地捕获。调度器不拥有源、spool 或副本，也不提供后台服务生命周期、分布式租约、跨分区顺序、远程 Frame/REST 传输、离线同步、冲突解决、schema 演进、固定硬件容量或现场掉电证据。远程拓扑和生产级调度运维仍需独立任务与真实环境验证。
