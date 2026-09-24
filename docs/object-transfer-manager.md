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

配置 `ResumeManifestPath` 后，管理器以原子替换和 `Flush(true)` 保存 bucket、key、uploadId、源文件长度与 SHA-256、分片大小和已完成分片。再次使用相同源文件和配置会复用该 uploadId，跳过已完成分片并重新完成；清单损坏、源文件摘要不匹配或分片记录不一致时会建立新会话。批量上传会按 bucket/key 派生独立清单，避免多个对象覆盖同一恢复状态。没有恢复路径，或恢复清单无法持久化时，失败或取消会尽力终止 multipart 会话并清理服务端分片。

下载通过 `OpenReadAsync` 流式复制到调用方提供的流，在结束时校验服务端提供的对象 SHA-256。需要文件发布语义时使用 `DownloadToFileAsync`：它先写同目录临时文件、刷新到磁盘，校验成功后再原子替换目标，取消或校验失败不会破坏已有文件。服务端未返回 SHA-256 时无法执行端到端校验，结果只能表示传输完成；固定硬件和现场合同必须要求服务端提供摘要。

批量上传使用 `UploadManyAsync`，以 `MaxConcurrency` 限制同时处理的对象数，按输入顺序返回成功结果或逐项异常；取消会停止整批，单个普通失败不会隐藏其它对象的结果。

本切片已覆盖嵌入式/REST 客户端共用的 manager、multipart 分片有界并发、恢复清单、逐分片安全重试、整对象校验和下载流式复制。固定硬件、大文件内存曲线、断电后的服务端未知结果和跨进程恢复仍需现场证据。
