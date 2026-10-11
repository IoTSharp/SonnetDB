---
title: 深入探讨：SonnetDB 的文件格式与存储布局
categories: SonnetDB,存储,备份恢复
draft: false
---

排查容器挂载、数据库迁移和备份问题，第一步是分清 Server 数据根目录、单个数据库目录和控制面目录。SonnetDB 的持久化边界是目录，不是一个可随意复制的单文件。本篇按当前文件格式文档修订旧稿中的每个 measurement 单独 WAL/Segment 目录示意。

正式 [4.0.0 Release](https://github.com/IoTSharp/SonnetDB/releases/tag/v4.0.0) 具有独立标签。文件兼容应按自己的版本与格式核对，最新 `main` 的布局说明不能自动成为任意旧版本的读回保证。

## Server 根目录和数据库目录

典型的 Server 数据根目录是：

```text
<DataRoot>/
├─ .system/
├─ metrics/
├─ telemetry/
└─ ...
```

`.system` 承载安装、用户、Token 摘要和授权等控制面信息。`metrics`、`telemetry` 各自是数据库目录。Docker 的默认挂载点是 `/data`，直接从源码运行通常使用 `./sonnetdb-data`，实际位置仍应核对运行配置。

measurement 相关的数据库公共文件为：

```text
<database-root>/
├─ catalog.SDBCAT
├─ measurements.tslschema
├─ tombstones.tslmanifest
├─ wal/
│  └─ {startLsn:X16}.SDBWAL
└─ segments/
   └─ {id:X16}.SDBSEG
```

这张图只展示时序公共布局；其它模型还有自己的主数据与索引，不是数据库所有文件的穷尽列表。

## Catalog、schema 与 tombstone

`catalog.SDBCAT` 保存 series catalog，`measurements.tslschema` 保存 measurement schema 集合。它们不是旧稿中的“每个 measurement 一个独立 schema 目录”。

`tombstones.tslmanifest` 保存删除与保留策略相关的 tombstone。查询需要结合它过滤数据，维护流程再消化相关记录。排查时应核对主数据、schema、catalog 和删除状态，不应单独移动其中一个文件。

## 分段 WAL

WAL 位于数据库的 `wal/`，当前命名使用起始 LSN 的十六进制形式。恢复会校验日志记录并按兼容合同回放；日志末尾损坏的处理也有具体边界。仓库保留旧 `active.SDBWAL` 布局的迁移路径，不表示任意损坏都可修复。

写入成功时 WAL 是否仅在进程缓冲、OS page cache 或已经同步到介质，取决于持久性配置。不能只因为目录里存在 WAL 就断言掉电后没有损失。

## Segment v6 与内嵌扩展

4.0.0 标签中的 SegmentWriter 声明写入 Segment Format v6：

```text
[SegmentHeader]
[数据 Blocks]
[BlockIndexEntry]
[可选向量索引/聚合 sketch 扩展区]
[SegmentFooter]
```

v6 将 HNSW 向量索引和扩展聚合 sketch 整合到主 `.SDBSEG`，新段不再为这些内容生成独立 sidecar。读取层保留 v4/v5 与 legacy sidecar 的相应兼容路径。

Header 的 mini-footer 摘要帮助识别尾部损坏，并在规定条件下提供受控 fallback；这不是绕过校验或任意坏段恢复。Segment 格式版本也不同于关系 schema、备份 manifest 或产品版本号，升级时分别核对。

## 备份与恢复示例

在受支持的离线/维护窗口中执行：

```bash
sndb backup create --path ./data/metrics --output ./backups/metrics
sndb backup verify --path ./backups/metrics
sndb backup inspect --path ./backups/metrics
sndb backup dry-run --path ./backups/metrics --target ./data/metrics-restored
sndb backup restore --path ./backups/metrics --target ./data/metrics-restored
```

备份包含 manifest 和逐文件 SHA-256 校验，恢复写入新的目录。运行中的目录不能随意复制几个文件充当一致备份，导出查询结果也不是完整恢复包。

Server 的 SonnetMQ 物理状态位于实例级 `.system/mq`，单库备份不包含完整 MQ 日志、消费者与 DLQ 状态。控制面身份与数据库恢复也须组合规划，不能宣称这组命令提供九模型原子恢复。Graph 仍按 Beta 合同验收。

参考：[文件格式](https://github.com/IoTSharp/SonnetDB/blob/main/docs/file-format.md)、[4.0.0 SegmentWriter](https://github.com/IoTSharp/SonnetDB/blob/v4.0.0/src/SonnetDB.Core/Storage/Segments/SegmentWriter.cs)、[备份恢复](https://github.com/IoTSharp/SonnetDB/blob/main/docs/backup-restore.md)和[可靠性变更](https://github.com/IoTSharp/SonnetDB/blob/main/docs/performance-reliability-updates.md)。本文核对文档与源码，未在本次整理中执行备份或恢复。
