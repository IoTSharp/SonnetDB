---
title: SonnetDB 从 KV 到 .NET 缓存：IDistributedCache 与 EasyCaching
categories: SonnetDB
tags: dotnet, KV, IDistributedCache, EasyCaching
draft: false
---

# SonnetDB 从 KV 到 .NET 缓存：IDistributedCache 与 EasyCaching

SonnetDB 提供基于持久 KV 的 .NET 缓存适配器，应用可以通过 `IDistributedCache` 或 EasyCaching 保存可重建的数据。本文按“连接、命名空间、读写、过期、失效”的顺序说明接入方法，并指出当前实现中显式滑动续期、启动探针和并发操作的边界。

![SonnetDB .NET 缓存接入：IDistributedCache 与 EasyCaching](https://img2024.cnblogs.com/blog/1725297/202610/1725297-20261014111434493-1637101932.png)

图：AI 主题示意图，并非实测截图；两种适配器是可选入口，TTL、并发与运行时边界以正文及源码为准。

## 1. 先确定缓存承担什么职责

适合放入缓存的是商品摘要、设备展示状态或计算结果。这些数据应能从业务主数据重新生成。关系数据迁移、订单提交结果和跨服务锁，不会因为接入缓存 Provider 就自动得到保障。

两种适配器都经 `SonnetDB.Data` 的 KV 客户端访问数据库：

| 入口 | 应用看到的接口 | 配置中的关键身份 |
|---|---|---|
| `SonnetDB.Caching.Distributed` | `IDistributedCache`，值为字节数组 | ConnectionString、Keyspace、Namespace |
| `SonnetDB.Caching.EasyCaching` | EasyCaching 命名 Provider | 上述三项，再加 Provider 名称 |

Namespace 用于组织键前缀，不提供独立鉴权。不要把租户权限只编码在 Namespace 中，也不要直接打开或复制底层 KV 文件。

本文于 **2026-10-14** 复核本地源码提交 `dcadffc7137f5653ec46c55a4171cf5f2d83de03`，参考链接固定到已公开且与本次专题文件一致的 `5424b66672e37f49a2a5633fdde0b54a9b1f0290`。当日正式 GitHub Release 与 NuGet Core 页面为 **4.0.0**；这不证明每个缓存扩展的同版本包均已发布。以下走仓库项目引用和样例，不要求安装未核实的扩展包。4.5 与 Workbench 预览按各自范围理解，Graph 仍为 Beta。

## 2. 从现有生态样例确认接线

准备 .NET 10 SDK，在仓库根目录执行：

```powershell
dotnet run --project samples/SonnetDB.EcosystemSample
```

默认连接为 `Data Source=./sample-data`，路径指向数据库目录。样例除缓存外，还会建关系表、写入一条商品及上传一个对象；应使用可丢弃的本地数据目录。

远程模式只替换连接字符串：

```powershell
$env:SONNETDB_CONNECTION='Data Source=sonnetdb+http://127.0.0.1:5080/app;Token=<token>'
dotnet run --project samples/SonnetDB.EcosystemSample
```

这里假定 Server 已启动且 token 已配置。完整样例需要建库、SQL、KV 和对象桶权限；生产接入应预建资源并单独核验所需权限。不要把 `<token>` 原样运行，也不要把真实 token 写进代码。

样例中缓存键 `product:1` 的值设为 `ready`，绝对 TTL 为五分钟。因此，在写入成功且尚未过期的前提下，输出中的 `cache=ready` 是应检查的结果；`mode` 用来辨认本地或远程模式。本文发布任务没有实际执行此样例，以上是源码给出的预期，不是本次运行报告。

## 3. 注册 IDistributedCache 并检查读写

已有 .NET 应用可引用仓库的 `extensions/SonnetDB.Caching.Distributed` 项目；DI 依赖与完整项目配置参见生态样例。注册代码为：

```csharp
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;

var services = new ServiceCollection();
services.AddDistributedSonnetDBCache(options =>
{
    options.ConnectionString = "Data Source=./cache-walkthrough";
    options.Keyspace = "cache";
    options.Namespace = "product-summary-v1";
    options.ExpirationScanInterval = TimeSpan.FromMinutes(1);
    options.ExpirationScanBatchSize = 1024;
});

using var provider = services.BuildServiceProvider();
var cache = provider.GetRequiredService<IDistributedCache>();
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

await cache.SetStringAsync("product:1", "ready",
    new DistributedCacheEntryOptions
    {
        AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5)
    }, timeout.Token);

Console.WriteLine(await cache.GetStringAsync("product:1", timeout.Token));
await cache.RemoveAsync("product:1", timeout.Token);
Console.WriteLine(await cache.GetStringAsync("product:1", timeout.Token) is null);
```

合同预期是先得到 `ready`，删除后得到 `True`。空值表示缓存未命中，应由应用决定是否读取主数据并重建。字符串扩展只是字节值的便利入口，业务对象的序列化版本需要应用自行管理。

`ExpirationScanInterval` 控制注册的后台清理服务，但单独 `BuildServiceProvider()` 不会启动 `IHostedService`。需要后台清理时使用正常启动的 Generic Host/ASP.NET Core Host；读取过期值的惰性失效与后台物理清理应分别验收。

当前 Provider 构造时会尝试确保数据库，并写入一个五分钟过期的启动探针。仅解析服务也可能产生写入；它不是只读连通性检查。远程权限失败应核对初始化流程，不要把它解释成“没有业务键所以应允许只读”。

## 4. 滑动过期需要按当前实现显式刷新

当前代码的 `Get`/`GetAsync` 只读取 envelope，**没有自动刷新滑动 TTL**。采用滑动策略时，应在需要延长有效期的位置显式调用：

```csharp
await cache.SetStringAsync("session:preview", "display-state",
    new DistributedCacheEntryOptions
    {
        SlidingExpiration = TimeSpan.FromMinutes(2),
        AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10)
    }, timeout.Token);

await cache.RefreshAsync("session:preview", timeout.Token);
```

`Refresh` 对已有滑动条目按“当前时间加滑动窗口”续期，同时受绝对到期时间限制；已经失效的条目不会被它重新创建。需要“每次命中都续期”的应用，应明确安排刷新并测试并发更新场景。

这里的 Async 方法目前主要是同步实现的 Task 包装，取消令牌在入口检查；不能据方法名承诺端到端非阻塞或请求途中可立即取消。缓存续期中的读取后写入也不能当作 CAS 操作。

## 5. EasyCaching 使用独立命名 Provider

已引用 `SonnetDB.Caching.EasyCaching` 项目并具备 EasyCaching/DI 依赖的应用，可按现有注册入口接入：

```csharp
using EasyCaching.Core;
using Microsoft.Extensions.DependencyInjection;

var services = new ServiceCollection();
services.AddEasyCaching(options =>
{
    options.UseSonnetDB(config =>
    {
        config.ConnectionString = "Data Source=./easycache-walkthrough";
        config.Keyspace = "cache";
        config.Namespace = "product-summary-v1";
    }, "sonnetdb-products");
});

using var provider = services.BuildServiceProvider();
var factory = provider.GetRequiredService<IEasyCachingProviderFactory>();
var cache = factory.GetCachingProvider("sonnetdb-products");
cache.Set("product:1", "ready", TimeSpan.FromMinutes(5));
var value = cache.Get<string>("product:1");
Console.WriteLine(value.HasValue ? value.Value : "MISS");
cache.Remove("product:1");
Console.WriteLine(cache.Exists("product:1"));
```

预期是 `ready`、`False`。这是与上一节独立的代码片段，不应把两段顶层变量原样拼到同一文件。两种 Provider 的值编码不同，即使指向同一 keyspace，也应配置不同 Namespace，不能默认交叉解码对方写入的值。

EasyCaching 当前 `UseLock=false`；`TrySet` 的实现是先 `Exists` 再 `Set`，不能作为竞争下原子的 NX 或分布式锁。需要单 key 条件更新时，应检查独立的 KV NX/XX/CAS 合同，而不是从缓存接口名称推断原子性。

## 6. 按结果解释问题与支持范围

| 观察 | 先检查什么 | 可以得出的结论 |
|---|---|---|
| 写后立即读不到 | 同一数据库、Keyspace、Namespace、键和 TTL | 身份或过期条件需核对，不能直接归为数据丢失 |
| Get 后滑动键仍到期 | 是否显式 Refresh | 当前读取不自动续期 |
| 物理过期数据仍存在 | Host 是否启动 janitor、清理批量限制 | 物理回收与逻辑未命中是两件事 |
| 更新后读到旧缓存 | 应用失效/重建顺序与并发 | Provider 没有提供业务主数据事务 |
| Native AOT 发布受阻 | 扩展包及依赖的 AOT 声明 | 两个缓存扩展当前均未标记 AOT compatible |

源码中存在缓存 DI、过期和读写测试，但本文没有重新运行这些测试，也没有做吞吐量、跨节点一致性、进程故障或长期生产验收。持久 KV 能保存值，不能消除业务缓存失效、权限和重建策略的责任。

## 参考与复核入口

- [生态接入与边界](https://github.com/IoTSharp/SonnetDB/blob/5424b66672e37f49a2a5633fdde0b54a9b1f0290/docs/ecosystem-integration.md)
- [可运行生态样例](https://github.com/IoTSharp/SonnetDB/blob/5424b66672e37f49a2a5633fdde0b54a9b1f0290/samples/SonnetDB.EcosystemSample/README.md)
- [DistributedCache 接入说明](https://github.com/IoTSharp/SonnetDB/blob/5424b66672e37f49a2a5633fdde0b54a9b1f0290/extensions/SonnetDB.Caching.Distributed/PackageReadme.md)
- [EasyCaching 接入说明](https://github.com/IoTSharp/SonnetDB/blob/5424b66672e37f49a2a5633fdde0b54a9b1f0290/extensions/SonnetDB.Caching.EasyCaching/PackageReadme.md)
- [缓存实现：续期、同步与异步入口](https://github.com/IoTSharp/SonnetDB/blob/5424b66672e37f49a2a5633fdde0b54a9b1f0290/extensions/SonnetDB.Caching.Distributed/SonnetDbDistributedCache.cs)
- [EasyCaching 实现与锁边界](https://github.com/IoTSharp/SonnetDB/blob/5424b66672e37f49a2a5633fdde0b54a9b1f0290/extensions/SonnetDB.Caching.EasyCaching/SonnetDbEasyCachingProvider.cs)
