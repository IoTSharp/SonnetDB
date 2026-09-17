# 音视频 transcript、关键帧与时间片段（M35 #304）

`SonnetDB.Media` 是可选扩展，仅引用 `SonnetDB.Core`。外部工具完成转写、OCR、抽帧；扩展接受这些工具输出的 `SemanticContentManifest`，以现有 KV/WAL 原子保存一份内容的完整分段，并提供 transcript 子串、时间交集和关键帧过滤查询。Core 不下载模型、运行解码器或复制媒体字节。

原始媒体仍只存放在 Object Bucket。每个命中携带原对象的 bucket/key/versionId/ETag、业务 source、稳定 segment ID、startMs/endMs 和关键帧对象引用。这里只交付导入和确定性定位，不包含音视频向量生成、相似度检索或真实模型质量证据；RRF/rerank 归 #303，管理面治理与备份重建归 #305。

## 运行现有音频或视频的转写导入

准备 .NET 10、一个已有的音频/视频文件，以及由外部转写工具整理出的分段 JSON。例子使用仓库中的两条合同演示转写，**它们不是对输入媒体的真实识别结果**；实际使用时替换为工具输出：

```json
[
  { "id": "intro", "ordinal": 0, "startMs": 0, "endMs": 1000, "text": "Opening" },
  { "id": "alarm", "ordinal": 1, "startMs": 1000, "endMs": 2000, "text": "Alarm detected" }
]
```

在仓库根目录执行（替换实际文件路径和 MIME）：

```powershell
dotnet run --project samples/SonnetDB.MediaSegments -- ingest ./artifacts/media-demo C:/media/inspection.mp4 inspection-001 video/mp4 ./samples/SonnetDB.MediaSegments/transcript.json
dotnet run --project samples/SonnetDB.MediaSegments -- query ./artifacts/media-demo inspection-001 alarm 1000 2000
```

第一次命令上传原对象到 `media/inspection-001`，读取有界分段文件，并输出固定版本的完整清单；第二次命令在重开的数据库里查询。可以保存完整清单并使用 `import <db> <manifest.json>` 再导入；该入口不会读取远程 URL 或运行外部命令。示例支持 Ctrl+C 和两分钟总超时。示例中的媒体上传先于清单校验提交，导入失败可能保留新上传的原对象；已有清单会因源版本变化显示 stale，修正 JSON 后重新导入即可。

## 关键帧与公开 API

安装/引用 `extensions/SonnetDB.Media/SonnetDB.Media.csproj`。以下片段放入使用 .NET 10 的程序中；`source` 是既有视频对象的 `SndbObjectInfo`，`frame` 是外部抽帧后上传的 image/* 对象元数据：

```csharp
using SonnetDB.Media;
using SonnetDB.SemanticContent;

var manifest = new SemanticContentManifest(
    "inspection-001",
    new(source.Bucket, source.Key, source.VersionId, source.ETag),
    source.Sha256, source.ContentType, SemanticContentModality.Video,
    source.SizeBytes, source: "camera/line-7")
{
    Segments =
    [
        new("speech-25", 0, 1000, 2000, "红色指示灯亮起"),
        new("frame-25", 1, 1000, 1040, "红色指示灯")
        {
            FrameIndex = 25,
            KeyFrameRef = new(frame.Bucket, frame.Key, frame.VersionId, frame.ETag),
        },
    ],
};
var media = new MediaSegmentStore(database);
media.Import(manifest, cancellationToken);
var result = media.Query("inspection-001", new()
{
    FromMs = 1000,
    ToMs = 2000,
    KeyFramesOnly = true,
    Limit = 50,
}, cancellationToken);
```

用 `SndbObjectStore.PutObjectAsync` 上传外部工具产出的原始文件或关键帧。导入只验证其对象元数据与合同，不检验字节是否能被媒体解码器解码。JSON 的读写使用公开的 `MediaSegmentJsonContext.Default.SemanticContentManifest`、`SemanticContentSegmentArray` 和 `MediaSegmentQueryResult` 类型信息；不需要反射序列化。扩展继承仓库的 AOT/trim 分析设置。

## 边界与持久化语义

- `startMs` 包含起点，`endMs` 不包含终点；查询返回与 `[fromMs,toMs)` 相交的片段。允许多说话人和关键帧区间重叠。零长关键帧拒绝：调用方应提供实际帧显示区间，例如 25 fps 的 `[1000,1040)`，不自动猜测帧率或扩展时间戳。
- ID 在一份内容中必须唯一；按 startMs、ordinal、ID 的顺序返回。`Text` 使用不区分大小写的序数子串匹配，没有分词、排名或 embedding。最多返回 1000 条，`HasMore` 表示本次结果预算之外还有匹配；该版本没有跨内容检索或连续分页游标。
- 单清单 UTF-8 输入和持久化输出最多 4 MiB、最多 10000 段、总 transcript/OCR 最多 100 万 UTF-16 字符。所有标识、引用和状态元数据合计最多 65536 字符，单字段最多 4096 字符；非法 UTF-16 surrogate 拒绝。JSON 转义和元数据也计入最终 4 MiB 输出上限，由有界输出流在增长前拒绝；内存对象开销不等同于文件字节上限。
- 每个公开操作默认 30 秒协作超时，构造器可指定不超过五分钟的预算，遍历期间检查取消。KV 操作及 JSON 序列化的单次同步调用不能被硬中断；返回前检查取消不会回滚已经提交的原子写入。
- 必须提供原对象 versionId 或 ETag。导入校验当前对象的版本/ETag、SHA256、大小和 MIME，并补齐固定引用；关键帧必须是 image/* 对象，音频清单不能带关键帧。**仅当前版本可导入和查询**：即使旧版本字节仍存在，当前对象被覆盖或删除后，查询返回 `IsStale=true` 和空命中；命中的关键帧变化同样 fail-closed。对象检查与 KV 写入之间不是跨模型事务，查询重新检查引用。查询发生后的并发对象变化应由调用方使用返回的固定版本处理。
- 同一 ID 完整替换是一次 KV 写入；失败、取消或非法 JSON 在写入前拒绝，旧片段保留。空 segments 表示替换为空。并发更新采用最后成功写入的完整清单，不混合旧新分段。
- `Delete(contentId)` 一次删除该内容全部派生片段，保留原媒体和关键帧对象；对象保留策略由对象桶管理。清单不存在时 `Query` 返回 null，原来源失效时返回 stale，持久 JSON/合同损坏时抛出 `InvalidDataException`，三者不会混为无命中。

## 本地验证

```powershell
dotnet test tests/SonnetDB.Media.Tests/SonnetDB.Media.Tests.csproj --disable-build-servers /nodeReuse:false /p:UseSharedCompilation=false
dotnet build samples/SonnetDB.MediaSegments/SonnetDB.MediaSegments.csproj --disable-build-servers /nodeReuse:false /p:UseSharedCompilation=false
```

合同测试使用标记了 MIME 的可控字节，验证持久化、恢复、来源追踪及输入边界。它们不证明真实转写/OCR 准确度、媒体解码能力、模型成本或固定硬件性能。

2026-09-17 本地验证：29 项合同测试全部通过；示例及可选扩展构建通过，AOT/trim 分析启用，0 警告、0 错误。另用 32044 字节、两秒 PCM WAV 文件实际执行示例 ingest，再由独立进程重开 query，得到唯一 `alarm` 命中、`[1000,2000)` 区间和原对象固定 versionId/ETag，`IsStale=false`。该转写是已知合同输入，不代表识别模型结果。
