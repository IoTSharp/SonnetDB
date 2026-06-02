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

第一批定位为离线 / 维护窗口工具。定时策略、增量备份、远端对象存储、审计审批、在线恢复和 UI 编排留给企业版管理能力继续演进。

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

后台重建队列、定时策略、增量备份、远端对象存储、在线恢复和企业审计审批仍留给后续批次。
