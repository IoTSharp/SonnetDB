---
title: SonnetDB Bucket 治理：Quota、Retention、Legal Hold 与生命周期
categories: SonnetDB
tags: 对象存储,Bucket,Quota,Retention,Legal Hold,生命周期,审计
draft: false
---

# SonnetDB Bucket 治理：Quota、Retention、Legal Hold 与生命周期

SonnetDB 的 Object Bucket 治理把容量配额、对象保留窗口、版本级 Legal Hold、生命周期执行和审计记录放在同一条对象存储合同中。本文以 media bucket 为例，说明如何先读状态、再配置规则、最后用结果和审计记录确认删除边界。

![SonnetDB Bucket 治理：Quota、Retention、Legal Hold 与生命周期](https://img2024.cnblogs.com/blog/1725297/202610/1725297-20261021180636499-93944022.png)

图：AI 主题示意图，并非实测截图；配额、保留、版本锁定、到期清理和审计关系以当前对象存储合同为准，不表示合规认证或九模型原子删除。

## 先固定版本和支持边界

本文于 2026-10-21 对照当前源码提交 faf4c9dda2892be69b906ff10d133fc417e571cd、对象客户端合同、管理工具矩阵和对象存储测试复核。公开源码固定到已可访问提交 5424b66672e37f49a2a5633fdde0b54a9b1f0290，可查看 [对象存储合同](https://github.com/IoTSharp/SonnetDB/blob/5424b66672e37f49a2a5633fdde0b54a9b1f0290/src/SonnetDB/Contracts/ObjectStorageContracts.cs)、[对象存储实现](https://github.com/IoTSharp/SonnetDB/blob/5424b66672e37f49a2a5633fdde0b54a9b1f0290/src/SonnetDB.Core/ObjectStorage/SndbObjectStore.cs)、[管理工具矩阵](https://github.com/IoTSharp/SonnetDB/blob/5424b66672e37f49a2a5633fdde0b54a9b1f0290/docs/management-tools.md) 和 [对象客户端合同](https://github.com/IoTSharp/SonnetDB/blob/5424b66672e37f49a2a5633fdde0b54a9b1f0290/docs/object-client-contract.md)。当前正式 GitHub Release 和 SonnetDB.Core NuGet 包核到 4.0.0；源码接口仍需与实际包版本核对。

本文没有启动真实 Web Admin、远程 Server、固定硬件或生产 bucket。代码片段展示的是治理顺序与返回字段，不是合规证明、容量承诺或九模型原子删除证据。对象数据与派生语义索引的清理仍是不同任务，Graph 为 Beta，与本文无关。

## 第一步：读取 bucket 当前状态

治理操作先确认 bucket 存在，再读取 quota、retention、lifecycle、统计和审计。嵌入式 API 使用 SndbObjectStore；远程应用应通过对应的对象客户端/管理端点完成同样的读取和审批流程。

~~~csharp
using SonnetDB;
using SonnetDB.ObjectStorage;

using var db = Tsdb.Open(new TsdbOptions { RootDirectory = "./data/governance" });
var store = new SndbObjectStore(db);

const string bucket = "media";
store.CreateBucket(bucket);

SndbBucketQuotaInfo quota = store.GetQuota(bucket);
SndbBucketRetentionInfo retention = store.GetRetention(bucket);
SndbBucketLifecycleInfo lifecycle = store.GetLifecycle(bucket);
SndbBucketStatsInfo stats = store.GetStats(bucket);

Console.WriteLine($"quotaBytes={quota.MaxSizeBytes} quotaVersions={quota.MaxObjectVersions}");
Console.WriteLine($"retentionCurrent={retention.RetainCurrentForDays} retentionNoncurrent={retention.RetainNoncurrentForDays}");
Console.WriteLine($"expireCurrent={lifecycle.ExpireCurrentAfterDays}");
Console.WriteLine($"usedBytes={stats.ObjectVersionSizeBytes} activeMultipart={stats.MultipartUploadCount}");
~~~

GetStats 同时报告当前可见对象、对象版本、删除标记、活动 Multipart 和分片字节数，并在配置 quota 时计算剩余空间与剩余版本数。配额检查会把已有对象版本和活动 Multipart 分片纳入预计使用量，因此不能只看当前 latest 对象的大小。

## 第二步：设置容量配额

SetQuota 支持最大对象版本数和最大字节数；传 null 表示该项不限制。写入普通对象、上传分片和完成 Multipart 前都会重新计算使用量，超过限制时抛出 quota_exceeded：

~~~csharp
SndbBucketQuotaInfo configuredQuota = store.SetQuota(
    bucket,
    maxSizeBytes: 10 * 1024 * 1024 * 1024L,
    maxObjectVersions: 20_000);

Console.WriteLine(
    $"maxBytes={configuredQuota.MaxSizeBytes} " +
    $"maxVersions={configuredQuota.MaxObjectVersions}");
~~~

配额是写入准入边界，不是后台自动删除策略。即使生命周期将来会清理旧版本，当前写入仍须先通过配额检查；应用应在错误响应中保留 bucket、对象 key 和客户端请求 ID，避免把一次配额拒绝误判成网络未知结果。

## 第三步：用 Retention 保护对象版本

Retention 按 bucket 配置当前版本和非当前版本的最短保留天数。生命周期扫描或显式删除遇到仍在窗口内的版本时，会返回 object_retained，不会因为它已经不是 latest 就绕过保留规则。

~~~csharp
SndbBucketRetentionInfo configuredRetention = store.SetRetention(
    bucket,
    retainCurrentForDays: 30,
    retainNoncurrentForDays: 7);

Console.WriteLine(
    $"current={configuredRetention.RetainCurrentForDays}d " +
    $"noncurrent={configuredRetention.RetainNoncurrentForDays}d");
~~~

保留窗口使用对象版本的创建时间；零天窗口表示不额外保护。删除标记不使用对象保留字段，但它仍受 Legal Hold 和生命周期删除标记规则影响。Retention 只保护对象版本，不代表所有九种数据模型共享一套删除事务。

## 第四步：对单个版本启用 Legal Hold

Legal Hold 是版本级状态，必须先解析到确定的 VersionId。启用后，删除对象版本和生命周期清理都会返回 object_legal_hold，直到显式关闭 hold：

~~~csharp
SndbObjectInfo version = store.HeadObject(bucket, "photos/device-001.bin")
    ?? throw new InvalidOperationException("对象不存在");

SndbObjectLegalHoldInfo held = store.SetLegalHold(
    bucket,
    version.Key,
    enabled: true,
    reason: "case-2026-10-21",
    versionId: version.VersionId);

Console.WriteLine($"hold={held.Enabled} version={held.VersionId}");
~~~

应用关闭 hold 前应保留审批和审计依据。SetLegalHold 会写入版本、启用状态、原因和更新时间，并追加 object.legal_hold.enable 或 object.legal_hold.disable 审计记录。没有版本号时不要把当前指针变化猜成原版本仍受保护。

## 第五步：配置生命周期并显式执行

生命周期规则分别控制当前版本、非当前版本和删除标记的到期天数。设置规则不会立即删除对象，ApplyLifecycle 才会按扫描开始时的 latest 指针分类并执行一次：

~~~csharp
SndbBucketLifecycleInfo configuredLifecycle = store.SetLifecycle(
    bucket,
    expireCurrentAfterDays: 90,
    expireNoncurrentAfterDays: 30,
    expireDeleteMarkerAfterDays: 14);

SndbBucketLifecycleApplyResult applied = store.ApplyLifecycle(bucket);
Console.WriteLine(
    $"expiredCurrent={applied.ExpiredCurrentObjects} " +
    $"removedNoncurrent={applied.RemovedNoncurrentVersions} " +
    $"removedDeleteMarkers={applied.RemovedDeleteMarkers}");
~~~

执行结果会列出过期的当前对象身份，便于调用方安排语义索引或缩略图清理。对象元数据先提交删除，再尽力删除正文文件；正文回收失败不能反向制造“有元数据但没有正文”的可见对象。Legal Hold 或 Retention 保护的版本会从删除计划中排除。

生命周期的当前/非当前分类以扫描开始时的 latest 指针为准，而不是简单按 CreatedUtc 排序。这样在恢复或指针被重新建立的场景中，清理仍服从当前可见版本合同。生命周期执行是一次有界维护调用，不等同于永远运行的后台任务。

## 第六步：用审计记录解释结果

治理页应把配置变更、统计读取、版本列表、hold、删除和生命周期执行放到同一条审计时间线。嵌入式 API 可以直接读取：

~~~csharp
foreach (SndbObjectAuditEntry entry in store.ListAudit(bucket, maxEntries: 100))
{
    Console.WriteLine(
        $"{entry.TimestampUtc:o} {entry.Action} " +
        $"{entry.Key}@{entry.VersionId}");
}
~~~

审计记录包含动作、bucket、可选 key/version、UTC 时间和结构化 details。它解释“为什么拒绝删除”或“本次生命周期删了哪些类别”，但不替代外部合规系统、身份认证或跨系统审批。管理工具中的 Write Approval、dry-run 和明确确认仍是危险写入的前置步骤。

## 删除 bucket 与重建边界

删除 bucket 会同时清理 policy、lifecycle、retention、quota、语义选项和对象索引；同名重建不会继承旧配置。旧的预签名令牌也会因为 bucket 版本变化而失效。删除期间如果遇到 WAL 同步异常，重启恢复会重新应用整批清理，避免配置和旧令牌半残留。

这个行为适合做“撤销整个命名空间”的明确操作，不适合当作跨 bucket 或跨模型事务。应用仍须保存删除请求、审批和恢复检查点，并把未知响应单列为待核对。

## 检查清单与支持边界

- 先读 GetStats、quota、retention 和 lifecycle，再提交写入或删除审批。
- Quota 约束对象版本和活动 Multipart 分片；Retention 保护当前/非当前版本；Legal Hold 保护确定的版本号。
- ApplyLifecycle 是显式一次执行，结果和审计可回读；派生语义索引清理是独立异步任务。
- 删除 bucket 后同名重建不继承旧治理配置，旧预签名令牌失效。
- 本文没有取得现场合规、固定硬件、容量曲线、跨系统审批或九模型原子删除证据；这些边界仍按对象桶和恢复路线单独验收。
