---
layout: default
title: "备份与恢复"
description: "使用 sndb backup 创建、校验、查看和离线恢复 SonnetDB 多模型数据库备份。"
permalink: /backup-restore/
---

## 适用范围

`sndb backup` 提供 SonnetDB 多模型数据库的第一批离线管理能力：

- 对 measurement、关系表、KV keyspace、document collection 创建一致目录备份。
- 生成 `sonnetdb.backup.json` manifest，记录一致性点、模型摘要、索引生命周期和逐文件 SHA-256。
- 支持备份校验、manifest 查看，以及恢复到新的数据库目录。

`sndb backup` 保持本地离线管理边界。运行时定时任务、在线恢复和管理界面由 Server 的备份任务 API 提供，增量备份、远端对象存储和审计审批仍不属于当前合同。

## 创建备份

```bash
sndb backup create --path ./data/metrics --output ./backups/metrics-20260602
```

可选参数：

| 参数 | 说明 |
| --- | --- |
| `--overwrite` | 允许目标目录已存在但必须为空 |
| `--no-fulltext-indexes` | 不复制 `documents/fulltext/` 派生全文索引目录，恢复后可由主数据重建 |

备份目标目录不能位于源数据库目录内部，避免复制过程中把备份产物再次纳入源文件枚举。

## 校验备份

```bash
sndb backup verify --path ./backups/metrics-20260602
```

校验会读取 manifest，逐个检查文件是否存在、大小是否一致、SHA-256 是否匹配。成功时返回 0；失败时返回非 0 并输出错误列表。

## 查看 manifest 摘要

```bash
sndb backup inspect --path ./backups/metrics-20260602
```

输出包括：

- manifest 格式版本和创建时间。
- checkpoint LSN、下一段 ID、segment 数量。
- measurement、table、keyspace、document collection 数量。
- table 二级索引、document JSON path / fulltext 索引、measurement vector 索引的 included / rebuildable 状态。

## 恢复备份

```bash
sndb backup restore --path ./backups/metrics-20260602 --target ./data/metrics-restored
```

恢复总是写入一个新的数据库目录。目标目录不存在时自动创建；如果目录已存在，必须为空且显式指定 `--overwrite`。

恢复前可以先做 dry-run。该命令会校验 manifest、文件 SHA-256 和目标目录策略，但不会复制文件：

```bash
sndb backup dry-run --path ./backups/metrics-20260602 --target ./data/metrics-restored
```

默认恢复前会先执行 `verify`。如需跳过校验：

```bash
sndb backup restore --path ./backups/metrics-20260602 --target ./data/metrics-restored --no-verify
```

如果备份时排除了全文索引，或希望恢复后立即同步补建派生索引，可以使用：

```bash
sndb backup restore --path ./backups/metrics-20260602 --target ./data/metrics-restored --rebuild-indexes
```

也可以对已恢复目录单独执行：

```bash
sndb backup rebuild-indexes --path ./data/metrics-restored
```

恢复后可以直接用本地连接打开：

```bash
sndb local --path ./data/metrics-restored --command "SHOW MEASUREMENTS"
```

## 索引与可重建数据

SonnetDB 备份 manifest 区分主数据和派生数据：

- 必需数据：catalog、schema、WAL、segment、tombstone、KV/table/document 主数据。
- 同步可重建索引：table secondary / table JSON path index、document JSON path index。
- 派生索引：document fulltext index、segment vector index、aggregate sketch 等会记录为可重建；全文索引可由 `backup rebuild-indexes` 同步补建，measurement vector index 仍按 Segment 生命周期返回 planned 状态。

`sndb backup restore` 负责还原文件。服务端管理面还提供同一数据库下的 HTTP 维护入口：

| Endpoint | 操作 | 说明 |
| --- | --- | --- |
| `POST /v1/db/{db}/maintenance` | `health_check` | 读取数据库健康摘要、segment / WAL / catalog / index 计数 |
| `POST /v1/db/{db}/maintenance` | `rebuild_index` | 重建 table secondary / table JSON path / document JSON path 索引，全文索引触发同步补建，向量索引返回 Segment 生命周期 planned 状态 |
| `POST /v1/db/{db}/maintenance` | `backup_verify` | 复用 manifest SHA-256 校验备份目录，仅 server admin 可调用 |
| `POST /v1/db/{db}/maintenance` | `restore_dry_run` | 校验备份和目标目录策略但不复制文件，仅 server admin 可调用 |

后台重建队列、增量备份、远端对象存储和企业审计审批仍留给后续批次。

## 运行时备份任务

Server 现在可以在进程内执行每日备份任务。任务定义持久化在
`<DataRoot>/.system/backup-tasks.json`，调度器按任务的 `localTime` 和
`timeZoneId` 每天只运行一次。默认配置使用北京时间 `02:00`、15 天和 15 批滚动保留，
任务可以通过管理 API 修改数据库范围、模型范围、目标目录、校验开关和保留策略。

全局配置位于 `SonnetDBServer:Backup`。

```json
{
  "SonnetDBServer": {
    "Backup": {
      "Enabled": true,
      "DestinationRoot": "/backup-volume/sonnetdb",
      "SchedulerTickSeconds": 60,
      "MaxTransferBytes": 21474836480,
      "TransferTimeoutSeconds": 1800
    }
  }
}
```

任务输出按 `<DestinationRoot>/<task-id>/<yyyyMMdd-HHmmss>/<database>` 分层，目标目录必须位于全局备份根目录内。任务默认不选择 KV 和对象桶正文，manifest 会记录实际模型范围。外置对象卷、实例级 `.system/mq`、身份和授权仍属于独立恢复边界。

资源名称选择会写入任务和 manifest。关系表、文档集合、KV keyspace 和对象 bucket 已按目录及目录清单执行筛选。measurement 共用 segment/WAL 文件，原生图共用 catalog，名称选择会保留在范围记录中并按模型整体复制，以保证恢复目录可重开。

管理入口包括 `/v1/system/backup-tasks` 的任务列表、创建、更新、删除和立即执行，以及 `/v1/system/backup-restore` 的目录或 HTTP(S) 恢复。所有这些入口都要求 server admin。

## 在线恢复

在线恢复支持服务器本机目录、zip/tar 上传和 HTTP(S) 地址。服务先把压缩包放入受控临时目录，限制传输大小和 30 分钟处理时间，校验 manifest 与逐文件 SHA-256 后才恢复。恢复总是生成不冲突的新数据库名称，例如 `restored-20261121153000-1`，不会覆盖现有数据库。恢复失败时会删除本次临时目录和未完成的新目录。

CLI 仍支持本地目录备份、校验和恢复。`backup create` 增加了 `--no-kv`、`--no-objects` 和 `--models measurements,tables,documents,graphs,kv,objects`，默认保持完整备份兼容行为。

## 应用内通用迁移原语

`SonnetDB.Core` 的 `MigrationService` 在 `BackupService` 之上提供稳定的组合门面：

| 方法 | 行为 |
| --- | --- |
| `Export` | 建立一致性 checkpoint，导出目录迁移包并立即校验 |
| `Scan` | 只读返回模型、文件、索引、一致性点和容量摘要 |
| `Checksum` | 校验逐文件 SHA-256，并计算稳定的包级 SHA-256 |
| `ImportDryRun` | 校验迁移包与目标目录策略，不创建目标目录 |
| `Import` | 预检通过后恢复到新的数据库目录 |

```csharp
using SonnetDB.Backup;

var migration = new MigrationService();
var exported = migration.Export(database, new MigrationExportOptions
{
    PackageDirectory = "./exports/metrics-v1",
});

var dryRun = migration.ImportDryRun(new MigrationImportOptions
{
    PackageDirectory = "./exports/metrics-v1",
    TargetDirectory = "./data/metrics-restored",
});

if (dryRun.IsValid)
{
    migration.Import(new MigrationImportOptions
    {
        PackageDirectory = "./exports/metrics-v1",
        TargetDirectory = "./data/metrics-restored",
    });
}
```

迁移包与 manifest 的 `DatabaseFormat` 绑定，适合同一兼容格式间的离线迁移和回滚准备，不是跨数据库产品的逻辑交换格式。调用方不得把 `VerifyBeforeImport=false` 视为已校验；此时 `MigrationChecksumResult.Verified` 为 `false`。
