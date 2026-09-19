# Object Transfer Manager

M36 #322 提供 `SndbObjectTransferManager`，把对象客户端已有的 multipart 原语组合成可恢复的文件传输边界。

```csharp
var manager = new SndbObjectTransferManager(client, new SndbObjectTransferOptions(
    MultipartThresholdBytes: 64 * 1024 * 1024,
    PartSizeBytes: 8 * 1024 * 1024,
    MaxConcurrency: 4,
    MaxRetries: 2,
    ResumeManifestPath: "./state/photo.upload.json"));

await using var source = File.OpenRead("photo.bin");
SndbObjectTransferResult result = await manager.UploadAsync("media", "photo.bin", source, cancellationToken: token);
```

上传正文先以固定 128 KiB 缓冲落入临时文件，内存占用不随文件大小增长。达到阈值后，分片上传使用有界 semaphore；`UploadPart` 可以安全覆盖同一 part，因此只对网络、超时、IO 和 5xx/408/429 响应重试。`CompleteMultipartUpload` 和普通 PUT 在发送后不自动重放，连接断开时由调用方根据对象和恢复清单核对结果。

配置 `ResumeManifestPath` 后，管理器以原子替换保存 bucket、key、uploadId、源文件长度与 SHA-256、分片大小和已完成分片。再次使用相同源文件和配置会复用该 uploadId，跳过已完成分片并重新完成；清单损坏或源文件摘要不匹配时会建立新会话。没有恢复路径时，失败或取消会尽力终止 multipart 会话并清理服务端分片。

下载通过 `OpenReadAsync` 流式复制到调用方提供的流，在结束时校验对象 SHA-256。取消令牌覆盖临时文件、分片 worker、HTTP 请求和目标写入；返回结果后的流生命周期仍由调用方管理。

本切片已覆盖嵌入式/REST 客户端共用的 manager、multipart 分片有界并发、恢复清单、逐分片安全重试、整对象校验和下载流式复制。固定硬件、大文件内存曲线、断电后的服务端未知结果和跨进程恢复仍需现场证据。
