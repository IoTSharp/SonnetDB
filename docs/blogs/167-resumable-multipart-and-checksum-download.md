---
title: SonnetDB 可恢复 Multipart 上传与校验下载
categories: SonnetDB
tags: 对象存储,Multipart,断点续传,SHA-256,恢复清单
draft: false
---

# SonnetDB 可恢复 Multipart 上传与校验下载

SonnetDB 的对象传输管理器把普通 PUT、Multipart 分片、恢复清单和下载校验组合成一条可观察的文件传输流程。本文用一个 media/photo.bin 示例说明何时切换 Multipart、哪些分片可以安全重试，以及为什么 CompleteMultipartUpload 发送后的未知结果必须交给调用方核对。

![SonnetDB 可恢复 Multipart 上传与校验下载](https://img2024.cnblogs.com/blog/1725297/202610/1725297-20261021174951697-1408860337.png)

图：AI 主题示意图，并非实测截图；分片、恢复清单、SHA-256 与原子替换用于说明传输合同，不代表固定容量或断电恢复验收。

## 先固定版本和支持边界

本文于 2026-10-21 对照当前源码提交 faf4c9dda2892be69b906ff10d133fc417e571cd、对象传输专题、对象客户端合同和测试复核。公开源码链接固定到已可访问提交 5424b66672e37f49a2a5633fdde0b54a9b1f0290，可查看 [SndbObjectTransferManager](https://github.com/IoTSharp/SonnetDB/blob/5424b66672e37f49a2a5633fdde0b54a9b1f0290/src/SonnetDB.Data/ObjectStorage/SndbObjectTransferManager.cs)、[对象传输说明](https://github.com/IoTSharp/SonnetDB/blob/5424b66672e37f49a2a5633fdde0b54a9b1f0290/docs/object-transfer-manager.md)、[对象客户端合同](https://github.com/IoTSharp/SonnetDB/blob/5424b66672e37f49a2a5633fdde0b54a9b1f0290/docs/object-client-contract.md) 和 [传输审计](https://github.com/IoTSharp/SonnetDB/blob/5424b66672e37f49a2a5633fdde0b54a9b1f0290/docs/audits/object-transfer-validation-20260920.md)。当前核到的正式 GitHub Release 和 SonnetDB.Core NuGet 包为 4.0.0；当前源码新增入口仍需与读者实际包版本核对。

本文没有启动真实远程 Server、固定硬件或大文件容量试验。代码中的阈值、文件名和哈希是可复核的演示参数，不是吞吐、延迟、内存曲线或断电恢复证据。对象桶是受支持的 S3 风格子集，Graph 仍为 Beta，与本文无关。

## 第一步：设置有界传输参数

SndbObjectTransferOptions 约束 Multipart 触发阈值、分片大小、并发数、分片重试次数和可选的恢复清单路径。下面的 64 MiB、8 MiB 和并发 4 是示例配置，应用应按自己的对象大小、磁盘和服务端合同调整。

~~~csharp
using SonnetDB.Data.ObjectStorage;

using var client = new SndbObjectStorageClient(
    "Data Source=sonnetdb+http://127.0.0.1:5080/app;Protocol=rest;Timeout=120;Token=<token>");

var options = new SndbObjectTransferOptions(
    MultipartThresholdBytes: 64 * 1024 * 1024,
    PartSizeBytes: 8 * 1024 * 1024,
    MaxConcurrency: 4,
    MaxRetries: 2,
    ResumeManifestPath: "./state/photo.upload.json");

var transfer = new SndbObjectTransferManager(client, options);
~~~

管理器会先把输入流以固定 128 KiB 缓冲写入任务临时文件，并在同一次遍历计算源文件长度和 SHA-256。这样分片读取不需要把整个文件放入内存。分片数量不能超过 10,000；MaxConcurrency 的有效范围是 1 到 64，MaxRetries 的有效范围是 0 到 8。

## 第二步：上传并观察结果

上传入口会根据源文件长度选择普通 PUT 或 Multipart。UploadAsync 返回对象信息、传输字节数、源 SHA-256、是否复用了清单以及分片数：

~~~csharp
await using var source = File.OpenRead("photo.bin");
var result = await transfer.UploadAsync(
    bucket: "media",
    key: "photos/2026-10-21/photo.bin",
    source: source,
    contentType: "application/octet-stream");

Console.WriteLine(
    $"version={result.Object.VersionId} " +
    $"sha256={result.Sha256} resumed={result.Resumed} parts={result.Parts}");
~~~

小于阈值的文件走普通 PUT；达到阈值后，管理器先创建 Multipart 会话，再按分片大小切分临时文件。每个分片响应必须返回匹配的 part number、大小和非空 SHA-256，否则本次上传失败。服务端最终对象如果带 SHA-256，管理器还会把它与源摘要比较；没有服务端摘要时，只能说明传输完成，不能宣称端到端校验已经完成。

## 第三步：理解“可重试”的范围

只有单个 UploadPart 可以在确定安全时重试。代码把 IO、超时、连接错误和服务端 408、429、5xx 视为可重试分片错误，并按 100 毫秒递增的短暂等待再发同一 part。因为同一 Multipart 会话中同一个 part 可以覆盖，重试不会重复发布一个新的对象版本。

CompleteMultipartUpload 和普通 PUT 的请求一旦发送，就不自动重放。连接在响应前断开时，调用方无法只凭异常判断服务端是否已提交；此时应读取对象、版本或恢复清单，再决定是否继续。不能把“超时”直接当成“服务端没有对象”，也不能自动再次 Complete。

## 第四步：使用恢复清单续传

指定 ResumeManifestPath 后，管理器会保存 bucket、key、uploadId、源长度、源 SHA-256、分片大小、Metadata、标签和已完成分片。清单写入临时文件、刷新到磁盘后再原子替换：

~~~csharp
var resumable = new SndbObjectTransferManager(
    client,
    options with { ResumeManifestPath = "./state/photo.upload.json" });

await using (var firstAttempt = File.OpenRead("photo.bin"))
{
    try
    {
        await resumable.UploadAsync("media", "photos/2026-10-21/photo.bin", firstAttempt);
    }
    catch (IOException error)
    {
        Console.Error.WriteLine($"保留清单，等待下一次人工续跑: {error.Message}");
    }
}

await using var retrySource = File.OpenRead("photo.bin");
var resumed = await resumable.UploadAsync(
    "media", "photos/2026-10-21/photo.bin", retrySource);
Console.WriteLine($"resumed={resumed.Resumed} sha256={resumed.Sha256}");
~~~

再次调用时，只有 bucket、key、源长度、源 SHA-256、分片大小和分片记录都匹配，管理器才复用 uploadId 并跳过已完成分片。清单损坏、源文件改变或分片记录越界时会新建会话。批量上传会按 bucket/key 派生不同清单路径，避免多个对象覆盖同一个恢复状态。

如果没有配置恢复路径，或清单还没有成功持久化，失败/取消会尽力调用 Abort 清理服务端 Multipart 会话。配置恢复路径后，失败时会保留清单，让下一次调用重新核对并继续；这不是跨进程自动接管，也不替代服务端对未知 Complete 结果的核查。

## 第五步：下载时先校验，再替换目标文件

流式下载会在读取完成后计算 SHA-256，并与服务端对象信息比较。需要文件发布语义时使用 DownloadToFileAsync：它先写同目录临时文件，刷新到磁盘，校验成功后再原子替换目标文件。

~~~csharp
var downloaded = await transfer.DownloadToFileAsync(
    bucket: "media",
    key: "photos/2026-10-21/photo.bin",
    destinationPath: "./downloads/photo.bin");

Console.WriteLine($"downloaded={downloaded.BytesTransferred} sha256={downloaded.Sha256}");
~~~

取消或校验失败会删除本次临时文件，已有目标文件不会被部分内容覆盖。OpenReadAsync 的取消令牌只覆盖打开操作；返回流之后，调用方仍须把令牌传给 ReadAsync 或 CopyToAsync，并释放返回流。

## 批量上传和 CLI 边界

UploadManyAsync 使用有界 semaphore 限制同时处理的对象数，返回结果顺序与输入顺序一致。单个对象失败会保留在对应结果中，不会吞掉同批其它对象的结果；整批取消则停止等待并抛出取消异常。

CLI 的 sndb cp 复用同一个 Transfer Manager，可在本地文件和 s3://bucket/key 之间上传或下载；sndb sync 当前只生成目录计划并要求 --dry-run，实际传输仍由 Transfer Manager 负责。不要把 sync 的计划输出当成已写入对象的回执。

## 最后检查支持边界

- 分片并发和分片重试有界，普通 PUT、Complete 以及响应未知的请求不自动重放。
- 恢复清单只在源文件身份和配置匹配时续传；清单是调用方管理的本地状态，不包含在数据库备份合同中。
- 服务端没有返回 SHA-256 时，结果只能表示传输完成；固定硬件和现场合同必须单独要求摘要。
- 本轮没有取得固定硬件、大文件内存曲线、断电后服务端未知结果或跨进程恢复证据；这些属于 M36 对象大文件/恢复待办。
