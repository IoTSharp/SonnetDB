# M35 #307：受治理的人脸模板与比较合同

`FaceRecognitionStore` 提供嵌入式人脸模板登记、1:1 阈值比较、1:N 候选、导出、删除和审计。输入是外部已经生成的向量与 [#306 视觉派生目标](visual-derived-content.md)。Core 不下载模型，不检测或解码人脸，不调用远程推理。候选相似度不是身份认定；真实模型的 FAR、FRR、TAR 和固定硬件性能仍须独立评测。

## 显式启用与信任边界

`FaceRecognitionOptions.Enabled` 默认 `false`，`AllowedPurposes` 默认为空。登记、验证、搜索和导出必须同时满足启用、允许用途和 `IBiometricAuthorizer.IsAuthorized(context, "face", operation)`。`Enroll`、`Verify`、`Search`、`Export`、`Delete`、`ReadAudit`、`ApplyRetention` 是互不隐含的独立权限；普通数据库 Read/Write/Admin 不自动授权。

宿主必须从可信认证会话建立 `BiometricAccessContext.Actor`，校验批准用途，再注入授权器。不得把远程请求提供的 Actor 当作已经认证的身份，也不得允许请求自行指定授权结果。嵌入式代码及其 `Tsdb` 实例属于信任边界，可直接访问 Core 存储的代码本身就是可信宿主。此切片没有人脸 REST/Frame 路由，也没有 Server 认证或模型提供方接线。

关闭功能后，显式授权的删除、到期清理和审计读取仍可执行；这些操作仍须匹配用途。每个图库内的模板按用途隔离，验证、候选、导出和删除均不能跨用途访问。

## 嵌入式入口

以下是已有对象与外部检测/embedding 完成后的入口片段。`profile` 是不可变 `FaceRecognitionProfile`，`target` 是与该检测 profile 匹配、状态为 `Ready` 的 `VisualDerivedTarget`；`hostAuthorizer` 必须由宿主实现真实权限判断。

```csharp
var faces = new FaceRecognitionStore(database, "visitor-consented", profile,
    hostAuthorizer, new FaceRecognitionOptions
    {
        Enabled = true,
        AllowedPurposes = ["visitor-consented-access"],
        MaxTemplates = 256,
        MaxRetention = TimeSpan.FromDays(7),
    });
var access = new BiometricAccessContext
{
    Actor = authenticatedSession.Subject,
    Purpose = "visitor-consented-access",
};
faces.Enroll(access, new FaceRecognitionTemplate
{
    Id = "template-42",
    SubjectId = "purpose-scoped-pseudonym-42",
    Purpose = access.Purpose,
    ProfileId = profile.Embedding.Id,
    Target = target,
    Vector = enrolledVector,
    ExpiresUtc = DateTimeOffset.UtcNow.AddDays(1),
});
var probe = new FaceRecognitionProbe
{
    ProfileId = profile.Embedding.Id,
    Vector = queryVector,
};
FaceVerificationResult comparison = faces.Verify(access, probe, "template-42", threshold: 0.8);
IReadOnlyList<FaceRecognitionCandidate> candidates = faces.Search(access, probe, limit: 10, threshold: 0.8);
int deleted = faces.DeleteSubject(access, "purpose-scoped-pseudonym-42");
```

示例阈值仅展示参数传递，不是推荐的生产阈值。服务只接受图片 embedding 的余弦空间，最多 4096 维，拒绝零向量、非有限数和 profile 不匹配；在双精度下归一化输入后复用已有向量距离计算。模型、revision、预处理、检测标签、外发策略及完整 profile 同图库持久绑定。同 ID 内容改变也会拒绝比较和写入；模型换代需新图库并重新生成模板。

## 来源、保留期和删除

登记前及每次验证、查询、导出时，服务读取 Object Store 当前对象元数据，对照固定 versionId/ETag 与内容 SHA-256。覆盖或删除的原对象、到期模板、非 `Ready` 目标不会继续返回。若提供 `CropObjectRef`，也核验裁剪对象的当前版本/ETag。来源检查发生于本次操作中，不声明与并发对象变更之间存在跨存储原子事务。

模板必须提供未来 `ExpiresUtc`，且不超过配置保留期；默认上限 30 天，配置最高 365 天。到期模板即使尚未清理也不能匹配、验证或导出。宿主定期调用 `ApplyRetention` 删除到期记录；原对象删除后可调用 `DeleteSource`，或按主体调用 `DeleteSubject`。两种删除均在当前用途内操作并可重复执行，返回实际删除条数。

删除与终态审计以一个 KV/WAL 原子批次提交，提交后同步 WAL。重开数据库后模板仍不可见。这里的删除是当前数据库逻辑删除，原对象、历史 WAL/快照、备份和宿主已导出的副本不属于物理擦除承诺；它们须按宿主独立的对象与备份保留策略处理。Core 不运行后台自动清理任务。

## 审计、预算与恢复

专用 `__face_recognition` keyspace 保存图库 profile、模板和审计，随数据库已有备份/恢复合同持久化。远程通用 KV、SQL 和管理列表把该名称及大小写/Windows 尾点空格别名作为保留资源处理，不能用普通数据入口绕过授权。

每次操作先写 `started` 并同步 WAL，审计失败便不调用授权器或访问模板。成功的模板变更和终态审计原子提交，成功审计同步失败时也不返回查询或导出结果。终态包括 `succeeded`、`denied`、`failed`、`cancelled`、`unknown`；进程中止可能留下 `started`，提交失败可能是 `unknown`，不能据此断言数据未变更，也不承诺 exactly-once。

审计不保存主体原文、用途原文、模板、媒体路径、向量、分数或异常消息，只保存认证主体/用途 SHA-256 摘要、操作、开始时间、状态与数量，保留 30 天。摘要仍属于需受控访问的可关联数据。`ReadAudit` 仅返回当前用途最近最多 200 条，页 payload 上限 256 KiB，包括本次读取自身的 `started`；不是完整审计导出 API。

每图库每用途默认最多 256 个模板，可配置为 1..10000；单模板序列化最多 256 KiB，全部模板最多 16 MiB。扫描使用稳定 KV cursor，每页最多 32 条/512 KiB，超出条目或字节预算显式失败，登记会预先检查替换后的总量。默认操作期限 5 秒、最高 1 分钟，含锁等待并支持取消；授权器与底层同步 I/O 属于协作边界。候选最多 100 条，按余弦相似度降序、模板 ID 升序稳定排序。该实现是有界精确比较，没有 ANN 索引和容量宣称。

`FaceRecognitionStoreTests` 验证默认关闭、独立权限/用途隔离、重开与删除、对象覆盖/删除失效、保留期、profile 漂移、数量预算、非法向量、取消、大数归一化、审计失败关闭与 source-generated JSON 脱敏。测试使用合成向量与对象字节，只验证合同；真实人脸质量、活体检测、公平性、FAR/FRR/TAR、生产容量、进程强杀和远程产品旅程继续后置。
