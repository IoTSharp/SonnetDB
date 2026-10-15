# SonnetDB.CAP

一个 NuGet 包，同时提供 DotNetCore.CAP 的 Document Store 存储与 SonnetMQ 传输。通过 `SonnetDB.Data` 的 `SndbDocumentClient` / `SndbMqClient` 访问嵌入式和远程 SonnetDB；支持 .NET 10 与 CAP 10.0.2，不依赖 EF Core 或 MongoDB Driver。

```csharp
using DotNetCore.CAP;
using Microsoft.Extensions.DependencyInjection;

services.AddCap(cap =>
{
    cap.UseStorageLock = true;
    cap.UseSonnetDbDocumentStorage(options =>
        options.ConnectionString = "Data Source=./data/documents");
    cap.UseSonnetDbTransport(options =>
    {
        options.ConnectionString = "Data Source=./data/mq";
        options.Namespace = "orders";
    });
});
```

远程连接使用 `Data Source=https://your-server/orders;Token=...;Protocol=Rest`，数据库需预先创建。凭据从应用配置读取。两个注册入口可分别使用，与其它 CAP 存储或传输组合；每个 CAP 实例仍只配置一个存储和一个传输。可通过 `options.Client` 复用已有 SDK 客户端，由调用方负责释放。

## 业务与 Outbox 原子提交

```csharp
using SonnetDB.CAP.Storage.Document;

using var transaction = publisher.BeginSonnetDbDocumentTransaction();
transaction.InsertBusinessDocument("order-42", """{"amount":42}""");
await publisher.PublishAsync("orders.created", "order-42");
await transaction.CommitAsync();
```

业务文档与 CAP 消息必须在同一 collection（默认 `cap`）的 ordered BulkWrite 中提交，成功后才交给 CAP dispatcher。业务实际 ID 为 `business:` 加应用 ID，可用 `GetBusinessDocumentId` 获取。支持插入、基于版本的替换/删除、回滚；单批最多 1000 项并受引擎 16 MiB 及 WAL 预算限制。不包含其它 collection、关系表、MQ 或外部副作用。未知事务对象明确拒绝。

## 消费与恢复边界

- 消费组和 topic 保留稳定映射，命名空间在发布端和订阅端必须一致。通配符订阅暂不支持。
- `ConsumerThreadCount=1`；订阅的 `GroupConcurrent` 使用默认值 0 或 1。传输逐条拉取，CAP 持久保存 Inbox 后确认，拒绝时保留 offset。
- 订阅先幂等持久登记消费组，防止其它组确认后回收本组尚未处理的消息。登记的离线/废弃组会影响回收边界，需要运维管理；新组只能访问仍保留的历史消息。
- 同一组同一 topic 需要一个活动消费者。当前仅在同一适配实例中检查重复订阅，跨进程部署须由应用保证单活动实例；没有分布式消费者租约或 exactly-once 声明。
- 存储锁使用条件原子文档更新，提供所有者检查和到期/续锁；系统时钟需保持同步。发送与文档提交仍是两个持久化边界，消费者应幂等。
- Server MQ 持久化属于实例 `.system/mq`，单数据库备份不包含它。重启恢复测试不代表强杀、掉电或固定硬件长稳验收。
- 自有 JSON 使用 source generation；CAP 上游默认 serializer 和订阅发现有反射边界，本包的分析器通过不代表完整 CAP 宿主支持 NativeAOT。

远程服务需包含 `/documents/{collection}/indexes/ensure` 与 `/mq/{topic}/ensure-group` 入口；旧 Server 缺少这些入口时明确失败。
