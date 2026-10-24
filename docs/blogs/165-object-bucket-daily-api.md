---
title: SonnetDB Object Bucket 日常 API：版本、Metadata、Range 与签名 URL
categories: SonnetDB
tags: 对象存储,Object Bucket,版本,Range,签名 URL
draft: false
---
# SonnetDB Object Bucket 日常 API：版本、Metadata、Range 与签名 URL

SonnetDB 的 Object Bucket API 把对象内容、版本、Metadata、标签、区间读取和短期签名 URL 放在同一套客户端合同里。本文用一个小型 `media` bucket 旅程说明这些入口怎样组合，以及嵌入式与远程模式有哪些不同。

![SonnetDB Object Bucket 版本、Metadata、Range 与签名 URL](https://img2024.cnblogs.com/blog/1725297/202610/1725297-20261019121731106-918676232.png)

图：AI 主题示意图，并非实测截图；图中的版本、范围和 URL 仅用于解释 API 关系。

## 先固定版本和支持边界

本文于 2026-10-19 对照 SonnetDB 源码提交 `0986f92e0d575c329a81b026e9431a16f4b8e2dd`、对象客户端合同和生态样例复核。公开源码链接固定到已可访问的提交 `5424b66672e37f49a2a5633fdde0b54a9b1f0290`，例如 [SndbObjectStorageClient](https://github.com/IoTSharp/SonnetDB/blob/5424b66672e37f49a2a5633fdde0b54a9b1f0290/src/SonnetDB.Data/ObjectStorage/SndbObjectStorageClient.cs)、[对象合同](https://github.com/IoTSharp/SonnetDB/blob/5424b66672e37f49a2a5633fdde0b54a9b1f0290/docs/object-client-contract.md) 和 [生态样例](https://github.com/IoTSharp/SonnetDB/blob/5424b66672e37f49a2a5633fdde0b54a9b1f0290/samples/SonnetDB.EcosystemSample/Program.cs)。当前核到的正式 GitHub Release 和 `SonnetDB.Core` NuGet 包为 4.0.0；源码中出现的新入口不能替代读者对实际包版本的核对。

下面的代码按客户端合同整理，本轮没有启动真实数据库、远程 Server 或对象服务执行示例。示例中的对象名、日期和哈希是可读的演示数据，不能当成吞吐、延迟、容量或生产验收结果。对象桶是 S3 风格子集，不承诺完整 AWS S3/SigV4 兼容；Graph 仍是 Beta，与本文无关。

## 第一步：创建 bucket 并写入带 Metadata 的对象

`SndbObjectStorageClient` 同时支持嵌入式连接字符串和 `sonnetdb+http://` 远程连接字符串。应用只使用公开客户端，不直接读数据库目录中的对象文件。

```csharp
using System.Text;
using SonnetDB.Data.ObjectStorage;
using SonnetDB.ObjectStorage;

const string connectionString = "Data Source=./data/object-demo";
using var objects = new SndbObjectStorageClient(connectionString);

var buckets = await objects.ListBucketsAsync();
if (!buckets.Any(static bucket => bucket.Name == "media"))
{
    await objects.CreateBucketAsync("media", SndbBucketPurpose.Artifact);
}

var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
{
    ["owner"] = "iotsharp",
    ["source"] = "demo",
};
var tags = new Dictionary<string, string>(StringComparer.Ordinal)
{
    ["kind"] = "photo",
    ["retention"] = "short",
};

await using var content = new MemoryStream(Encoding.UTF8.GetBytes("SonnetDB object body"));
SndbObjectInfo first = await objects.PutObjectAsync(
    "media",
    "images/device-001.txt",
    content,
    contentType: "text/plain",
    metadata: metadata,
    tags: tags);

Console.WriteLine($"version={first.VersionId}, sha256={first.Sha256}");
```

返回的 `SndbObjectInfo` 包含 `VersionId`、`ETag`、`Sha256`、内容类型、大小、Metadata 和标签。Metadata 作为对象属性保存，标签可以通过 `SetObjectTagsAsync` 单独更新。写入同一个 key 会产生新的对象版本；不要把 key 当成不可变文件名，也不要只保存最后一次 ETag。

## 第二步：列出当前版本和历史版本

日常页面一般只需要当前可见对象，版本审计才调用 `ListObjectVersionsAsync`：

```csharp
SndbObjectVersionListResult versions =
    await objects.ListObjectVersionsAsync("media", "images/device-001.txt");

foreach (SndbObjectInfo version in versions.Versions)
{
    Console.WriteLine(
        $"{version.VersionId} deleteMarker={version.IsDeleteMarker} " +
        $"updated={version.UpdatedUtc:o} sha256={version.Sha256}");
}
```

`key` 传入对象名时返回该对象的版本；传 `null` 则读取整个 bucket 的版本列表。删除对象会创建 delete marker，版本列表因此可能同时出现普通版本和删除标记。批量删除也会逐项返回版本和错误，响应不完整时客户端会抛出 `InvalidDataException`，调用方应先核对原目标再决定后续动作，不能根据异常就自动补发删除。

## 第三步：用 Range 读取大对象的一段

`OpenReadAsync` 返回带元数据的流结果。区间读取由 `SndbObjectRange` 表达，结果同时给出偏移、当前长度、是否为区间和完整对象长度：

```csharp
SndbObjectRange range = new(0, 8);
SndbObjectReadResult? read = await objects.OpenReadAsync(
    "media",
    "images/device-001.txt",
    range);

if (read is null)
{
    Console.WriteLine("object_not_found");
    return;
}

await using (read.Content)
{
    byte[] bytes = new byte[read.Length];
    int offset = 0;
    while (offset < bytes.Length)
    {
        int count = await read.Content.ReadAsync(bytes.AsMemory(offset));
        if (count == 0)
        {
            break;
        }

        offset += count;
    }

    Console.WriteLine($"offset={read.Offset}, length={offset}, total={read.TotalLength}");
}
```

这里读取第一步写入的短文本前 8 字节，预期为 `SonnetDB`；若要测试大对象的 1 KiB 区间，应先上传足够长的内容，再调整 offset/length。

从对象尾部读取时使用 `SndbObjectRange.FromSuffix(length)`。远程客户端在 `range` 不为空时固定走 REST Range 请求；即使连接配置选择 `frame-http2` 或 `auto`，这也不表示新增了对象 Frame Range opcode。返回流以后，取消令牌必须继续传给 `ReadAsync` 或 `CopyToAsync`，并由调用方释放流。

## 第四步：为远程读取创建短期签名 URL

预签名 URL 是远程 HTTP 能力，用于把一次受限的 `GET` 或 `PUT` 交给另一个调用方。此步骤改用已初始化、已授权且含有目标对象的远程连接，不能沿用第一步的嵌入式客户端：

```csharp
using var remoteObjects = new SndbObjectStorageClient(
    "Data Source=sonnetdb+http://127.0.0.1:5080/app;Token=<token>");

SndbPresignedObjectUrl link = await remoteObjects.CreatePresignedUrlAsync(
    "media",
    "images/device-001.txt",
    method: "GET",
    expiresMinutes: 10);

Console.WriteLine($"{link.Method} {link.Url}");
Console.WriteLine($"expires={link.ExpiresUtc:o}");
```

签名对象会回显 bucket、key、method 和过期时间。过期时间短只能缩小泄露窗口，不能替代应用自己的授权、审计和撤销设计。嵌入式对象存储没有 HTTP 出口，调用 `CreatePresignedUrlAsync` 会抛出 `NotSupportedException`；不要在本地模式下把自拼 URL 当成等价功能。签名 URL 也不改变对象版本语义，写入后仍应回读 `VersionId`、`ETag` 和 `Sha256`。

## 传输和失败处理

小对象可以直接 `PutObjectAsync`。需要受控并发、SHA-256 和可恢复清单时，使用 `SndbObjectTransferManager`；它会把输入先落到受控临时文件，再按阈值选择普通 PUT 或 multipart。安全重试只限幂等的 `UploadPart`；普通 PUT 和 `CompleteMultipartUploadAsync` 一旦发送，连接中断或响应损坏都不自动重放，调用方应先用对象和恢复清单核对目标。

这条边界很容易被“有了版本”误读成“可以随便重试”。版本能帮助定位结果，却不能证明一次未知提交没有发生。对网络超时、取消和坏响应保留未知状态，读取对象/版本确认后再决定补偿动作。

## 可以核查什么，不能推出什么

- 可以核查：对象当前版本的元数据、标签、ETag、SHA-256、版本列表、Range 返回偏移和签名 URL 过期时间。
- 可以核查：嵌入式与远程两种入口、远程签名 URL 的明确拒绝边界，以及客户端对错误响应和响应流的释放行为。
- 不能推出：完整 S3/SigV4 兼容、固定硬件容量、真实公网下载速度、跨进程断电恢复或长期生产稳定性。
- 不能推出：签名 URL 自动完成租户授权、生命周期删除与业务回滚；这些属于上层 Profile 和运维门禁。

先用一个独立 bucket 完成版本和哈希核对，再把 Range 与签名 URL 接入真实应用。对象写入与 multipart 完成请求的结果未知时，保留原始请求证据，不要把“没有拿到响应”改写成“肯定没写入”。

## 参考资料

- [对象客户端取消与目标合同](https://github.com/IoTSharp/SonnetDB/blob/5424b66672e37f49a2a5633fdde0b54a9b1f0290/docs/object-client-contract.md)
- [ObjectStorageContracts](https://github.com/IoTSharp/SonnetDB/blob/5424b66672e37f49a2a5633fdde0b54a9b1f0290/src/SonnetDB/Contracts/ObjectStorageContracts.cs)
- [SndbObjectModels](https://github.com/IoTSharp/SonnetDB/blob/5424b66672e37f49a2a5633fdde0b54a9b1f0290/src/SonnetDB.Core/ObjectStorage/SndbObjectModels.cs)
- [生态接入与 Profile 边界](https://github.com/IoTSharp/SonnetDB/blob/5424b66672e37f49a2a5633fdde0b54a9b1f0290/docs/ecosystem-integration.md)
