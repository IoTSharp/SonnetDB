# 视觉派生内容合同（M35 #306）

`SonnetDB.SemanticContent` 的 `Visual*` 类型描述外部检测器产生的目标、矩形区域和视频轨迹。原始图片或视频继续由 Object Bucket 唯一持有。此 PR 提供模型、确定性校验与公开 source-generated JSON；不加载检测器、不解码媒体，也不增加持久化表、识别服务或查询引擎。

## 最小图片结果

```csharp
using SonnetDB.SemanticContent;
using System.Text.Json;

var source = new VisualSourceReference
{
    ContentId = "inspection-001",
    ObjectRef = new("inspections", "pump-001.jpg", versionId: "object-version-1"),
    ContentHash = "sha256:<实际原对象哈希>",
    Modality = SemanticContentModality.Image,
    Width = 1920, Height = 1080,
};
var profile = new VisualDetectorProfile
{
    Id = "defects-model-v1", Provider = "external-import", Model = "defects",
    Revision = "weights-v1", PreprocessingRevision = "rgb-original-orientation-v1",
    LabelSetRevision = "defect-classes-v1", SupportedModalities = [SemanticContentModality.Image],
};
var target = new VisualDerivedTarget
{
    Id = "defect-1", Source = source, DetectorProfileId = profile.Id,
    Label = "surface-defect", Confidence = 0.9,
    Region = new() { X = 0.1, Y = 0.2, Width = 0.3, Height = 0.4 },
};
var manifest = new VisualDerivationManifest
{
    Id = "inspection-001/defects-model-v1", Source = source,
    DetectorProfiles = [profile], Targets = [target],
};
VisualContentValidator.ValidateOrThrow(manifest);
string json = JsonSerializer.Serialize(manifest, VisualContentJsonContext.Default.VisualDerivationManifest);
```

示例版本、hash 和置信度仅说明合同，不是实际模型证据。调用方必须从受权限保护的 Object API 获取真实身份并核对内容。校验器不会读取对象，因此无法证明引用仍然存在、哈希匹配字节或检测结论正确。

## 来源、区域与模型版本

- `VisualSourceReference` 固定 `ContentId`、bucket/key、versionId 或 ETag、内容 hash、画面尺寸和模态。一个快照中的所有目标及轨迹必须具有完全相同的来源合同；即使 key 不变，也不能混入覆盖后的对象版本。
- `VisualRegion` 使用应用原对象方向信息之后的画面坐标，左上为 `(0,0)`，右下为 `(1,1)`。宽高必须大于零，区域不得越界或包含 NaN/Infinity。导入方应先将 letterbox、裁剪与模型输入坐标转换回原画面；此处不自动猜测转换。
- `VisualDetectorProfile` 的 provider、模型、权重版本、预处理版本、标签集版本、输入模态与外发策略共同形成兼容合同。`IsCompatibleWith` 比较全部字段，模态顺序不影响比较；只应用于已通过校验的 profile。新模型或处理语义应使用新 ID，宿主持久化时必须拒绝已登记 ID 下的合同漂移。
- `CropObjectRef` 只允许表示可重建裁剪对象，必须独立固定身份。若已有共同版本或 ETag 能证明它指向原对象，校验直接拒绝；其他引用之间是否实际指向相同字节需由宿主验证。
- 默认 `DataEgressPolicy` 为 `LocalOnly` 且 `AuditRequired=true`；非本地声明必须指定目标。此处校验声明完整性，实际授权、外发控制、审计提交与访问权限由执行宿主落实。

## 视频 track

视频必须声明正的 `DurationMs`。目标的 `TimestampMs` 在 `[0, DurationMs)` 内；`FrameIndex` 可选且非负，不根据时间戳反推固定帧率。图片目标不能携带时间、帧序号或 track。

`VisualTrack` 只聚合同一视频版本、检测 profile 和标签的观察结果。`StartMs`/`EndMs` 为半开区间，所有成员时间位于其中；`TargetIds` 按时间严格递增，已提供的帧序号也必须严格递增。每个目标最多被一个 track 引用一次，目标上的 `TrackId` 和 track 列表必须双向完整匹配。track ID 不代表真实身份，不支持跨视频或跨摄像头关联。

完整快照通过 `Validate`/`ValidateOrThrow` 核对全部引用。后续独立能力可先调用 `ValidateTarget(target, profile)` 检查单目标结构、来源与 profile；此入口不验证 track 是否已经存储或被完整引用。

## 状态与预算

目标与快照复用 `SemanticIndexStateInfo` / `SemanticIndexStateMachine`。原对象发生覆盖、删除或 profile 更新时，宿主需将旧派生结果置为 `Stale` 并按现有清理策略删除或重建；仅序列化合同不会自动清理对象或索引。空 `Targets` 可表示检测完成但未检出目标，仍需保留完整 source 和至少一个 profile。

| 项目 | 硬上限 |
|---|---:|
| 单快照 detector profiles | 64 |
| 单快照目标 | 4096 |
| 单快照 tracks | 512 |
| 所有 tracks 合计目标引用 | 4096 |
| 常规标识、标签和 profile 文本 | 256 个 UTF-16 字符 |
| 内容 hash | 512 个字符 |
| 对象版本、ETag、失败说明 | 1024 个字符 |
| 外发目标 | 2048 个字符 |
| 对象 key | 4096 个字符 |
| 返回的失败明细 | 最多前 256 条 |
| 协作校验时间 | 5 秒 |

校验先检查集合数量，再读取成员；每个集合循环观察取消和时间预算，失败返回结构化 `Path/Rule/Message`。取消抛出 `OperationCanceledException`，时间超限抛出 `TimeoutException`。校验期间调用方不得并发修改集合；传入自定义集合的索引器也必须及时返回，协作超时不能强制打断阻塞的用户代码。JSON 接收方须在反序列化前限制请求字节数；校验上限不会阻止上游先创建超大 JSON 对象图。

## 验证与后续边界

`VisualContentContractTests` 覆盖图片/视频合同、对象版本隔离、坐标及置信度边界、轨迹引用与排序、profile 漂移、外发声明、空/null/重复/损坏 Unicode、输入预算、取消和 source-generated JSON round-trip。测试使用合成元数据，不能计作检测质量、FAR/FRR、mAP/CMC、真实模型或容量证据。

#307 的受治理人脸能力、#308 的 ReID/步态/姿态动作、#309 的车辆/车牌能力继续拥有独立权限、保留期、审计、删除与质量合同。它们可引用本 PR 的目标及固定来源，不应把裁剪结果变成第二份权威原对象，也不应把 track 或向量相似度等同于精确身份。
