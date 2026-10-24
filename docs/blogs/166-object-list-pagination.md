---
title: SonnetDB 对象列表分页：Prefix、Continuation 与高变更率
categories: SonnetDB
tags: 对象存储,分页,Continuation Token,一致性
draft: false
---
# SonnetDB 对象列表分页：Prefix、Continuation 与高变更率

SonnetDB 的对象列表接口使用 Prefix、可选 Delimiter 和不透明 Continuation Token 做有界遍历。本文从第一页到下一页写出一条可复核的 C# 读取流程，再说明对象在翻页期间新增或删除时，调用方怎样检查重复与缺失。

![SonnetDB 对象列表分页：Prefix、Continuation 与高变更率](https://img2024.cnblogs.com/blog/1725297/202610/1725297-20261019122134685-893561954.png)

图：AI 主题示意图，并非实测截图；页码、token 和对象变化用于说明分页合同，不代表固定吞吐或一致性快照。

## 先固定版本和分页合同

本文于 2026-10-19 对照 SonnetDB 源码提交 `0986f92e0d575c329a81b026e9431a16f4b8e2dd`、对象客户端合同和对象分页审计复核。公开源码固定到提交 `5424b66672e37f49a2a5633fdde0b54a9b1f0290`，可查看 [分页实现](https://github.com/IoTSharp/SonnetDB/blob/5424b66672e37f49a2a5633fdde0b54a9b1f0290/src/SonnetDB.Core/ObjectStorage/SndbObjectStore.Listing.cs)、[客户端入口](https://github.com/IoTSharp/SonnetDB/blob/5424b66672e37f49a2a5633fdde0b54a9b1f0290/src/SonnetDB.Data/ObjectStorage/SndbObjectStorageClient.cs) 和 [对象分页审计](https://github.com/IoTSharp/SonnetDB/blob/5424b66672e37f49a2a5633fdde0b54a9b1f0290/docs/audits/object-pagination-20260906.md)。当前正式 Release/NuGet `SonnetDB.Core` 核到 4.0.0；源码和审计中的新分页入口仍需与读者实际包版本对照。

本轮没有启动真实 bucket 或远程 Server。下面的对象名、token 和页内容是为了解释合同而构造的示例，不能作为高变更率现场数据、稳定排序延迟或跨页快照证据。对象列表仍是 S3 风格子集；Graph Beta 不属于本篇范围。

## 第一步：先用 Prefix 缩小遍历范围

分页读取的 `maxKeys` 是单页上限，服务端已有上限为 10,000。`prefix` 会在请求侧去掉前导 `/`；仅由空格组成的 Prefix 也会保留并参与过滤，不等于没有 Prefix。最简单的第一页读取如下：

```csharp
using SonnetDB.Data.ObjectStorage;
using SonnetDB.ObjectStorage;

using var objects = new SndbObjectStorageClient(
    "Data Source=sonnetdb+http://127.0.0.1:5080/app;Token=<token>");

SndbObjectListResult first = await objects.ListObjectsAsync(
    bucket: "media",
    prefix: "images/",
    maxKeys: 3,
    continuationToken: null,
    delimiter: "/",
    cancellationToken: CancellationToken.None);

Console.WriteLine($"truncated={first.IsTruncated}");
foreach (SndbObjectInfo item in first.Objects)
{
    Console.WriteLine($"object {item.Key} v={item.VersionId}");
}

foreach (string commonPrefix in first.CommonPrefixes)
{
    Console.WriteLine($"prefix {commonPrefix}");
}
```

`Delimiter = "/"` 时，子目录会折叠到 `CommonPrefixes`。对象和公共前缀共享 `maxKeys` 名额，所以一页可能返回两个对象和一个公共前缀，而不是三个对象再加三个目录。页内对象和公共前缀按 Ordinal 顺序返回，调用方不应再用当前区域设置排序。

## 第二步：把 token 当成不透明游标

只有 `IsTruncated` 为 `true` 且 `NextContinuationToken` 非空时才继续读取。不要解码、拼接或按 key 自己制造 token：分组列表的 token 还绑定了 bucket、prefix 和 delimiter。

```csharp
string? token = null;
var seen = new HashSet<string>(StringComparer.Ordinal);
using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
const int maxPages = 100;
int pages = 0;

do
{
    deadline.Token.ThrowIfCancellationRequested();
    pages++;
    SndbObjectListResult page = await objects.ListObjectsAsync(
        "media",
        "images/",
        maxKeys: 3,
        continuationToken: token,
        delimiter: "/",
        cancellationToken: deadline.Token);

    foreach (SndbObjectInfo item in page.Objects)
    {
        // 版本 ID 一起参与去重，避免同一 key 的合法新版本被吞掉。
        string identity = item.Key + "\u001f" + item.VersionId;
        if (!seen.Add(identity))
        {
            Console.WriteLine($"duplicate={identity}");
        }
    }

    foreach (string commonPrefix in page.CommonPrefixes)
    {
        Console.WriteLine($"prefix={commonPrefix}");
    }

    token = page.IsTruncated ? page.NextContinuationToken : null;
}
while (token is not null && pages < maxPages);

if (token is not null)
{
    Console.WriteLine("page_budget_reached: traversal_incomplete");
}
```

客户端会验证响应 bucket、prefix、delimiter、请求 token、服务端有效 `maxKeys`、对象/公共前缀的归属和页限。截断页必须提供能推进的 token，末页不能保留后续 token；异常响应会被拒绝，而不是静默拼出半页结果。

## 第三步：在高变更率下记录重复和缺失

对象列表分页读取调用时状态，不提供跨页快照。第一页返回 token 后，另一个写入者可能新增 `images/new.jpg`，也可能删除 `images/old.jpg`。因此“每一页都合法”不等于“整个遍历是某一时刻的完整快照”。

建议把每次遍历保存为一个有界审计：

1. 记录请求的 bucket、prefix、delimiter、`maxKeys`、起始时间和每页 token 的摘要；不要把 token 当作业务主键。
2. 用 `(Key, VersionId)` 作为对象去重键，用 `CommonPrefixes` 单独记录目录折叠结果。
3. 发现重复时保留首次和再次出现的页号；发现业务上应存在但没有读到的 key，重新发起一次从头遍历并比较两次清单。
4. 如果两次结果仍不一致，把差异标成并发变化或未知，不要直接把缺失对象当成删除成功。

这套流程只处理客户端可观察到的重复和缺失。它没有把分页升级成分布式快照，也没有替调用方完成租户级锁、批量补偿或业务删除确认。

## 第四步：理解 delimiter 与物理预算

普通列表不分组时 `delimiter` 为空；分组列表把每个公共前缀当作一项，并在达到页限后跳到该前缀上界。底层维护对象列表派生索引，写入、删除标记和 multipart 完成会在同一 WAL 批次更新索引。重开时，旧库缺少 ready 标记会先按页清理和重建；完成标记最后提交。

为了避免大量删除标记把一次列表放大成全桶扫描，物理候选也受预算限制。超出预算时服务端返回 `object_list_scan_budget_exceeded`（HTTP 映射为 503），不返回半页，也不生成推进 token。合同建议等待对象元数据的检查点或压实完成后，再由调用方决定是否重读同一个请求；这不是客户端自动重试保证。

配置 `frame-http2` 或 `auto` 不会新增 Object Frame 的 list opcode，分页仍使用既有 HTTP 管理路由。取消令牌会贯穿 Core、REST 和 SDK；调用方仍需为自己的遍历设置总期限。

## 一个可检查的结果表

| 检查项 | 通过条件 | 发现问题时的动作 |
| --- | --- | --- |
| 页归属 | bucket、prefix、delimiter 与请求一致 | 丢弃响应并记录 `InvalidDataException` |
| 页大小 | 对象数 + 公共前缀数不超过 `maxKeys` | 不把响应当成完整页 |
| token 推进 | 截断页有新 token，末页无 token | 停止遍历并保留原始响应 |
| 排序与重复 | 页内 Ordinal；跨页按 `(Key,VersionId)` 检查 | 记录页号，重新从头审计 |
| 高变更率 | 两次清单差异可解释 | 标记并发变化/未知，不猜删除 |
| 预算错误 | `object_list_scan_budget_exceeded` | 等待维护窗口后由调用方重读 |

## 支持范围和证据边界

- 已实现：Prefix、Continuation Token、Delimiter/CommonPrefixes、最大页限、Ordinal 排序、取消传播和物理候选预算。
- 本地审计覆盖 Core 分页、REST/Kestrel 多页和重开/重建路径；这些替身和本机回归不等于固定硬件、远程现场或长稳验收。
- 未承诺：跨页快照、全量 S3 兼容、任意规模的无界遍历、云厂商目录语义或高变更率下的 exactly-once 清单。
- 对象写入、删除和 multipart 完成在请求发送后仍可能有未知结果；分页读到的状态只能作为后续核查证据，不能替代写请求回执。

## 参考资料

- [对象分页审计（OBJECT-001）](https://github.com/IoTSharp/SonnetDB/blob/5424b66672e37f49a2a5633fdde0b54a9b1f0290/docs/audits/object-pagination-20260906.md)
- [对象客户端取消与目标合同](https://github.com/IoTSharp/SonnetDB/blob/5424b66672e37f49a2a5633fdde0b54a9b1f0290/docs/object-client-contract.md)
- [SndbObjectStore.Listing](https://github.com/IoTSharp/SonnetDB/blob/5424b66672e37f49a2a5633fdde0b54a9b1f0290/src/SonnetDB.Core/ObjectStorage/SndbObjectStore.Listing.cs)
- [SndbObjectStorageClient](https://github.com/IoTSharp/SonnetDB/blob/5424b66672e37f49a2a5633fdde0b54a9b1f0290/src/SonnetDB.Data/ObjectStorage/SndbObjectStorageClient.cs)
