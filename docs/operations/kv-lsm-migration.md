# KV 存储引擎与 LSM 迁移

本文说明 SonnetDB KV 存储引擎（`KvOptions.StorageEngine`）的默认值、已有数据的打开时迁移、保留与回滚，以及降级时的注意事项。设计与合同差异见 [KV-LSM 设计](../design/kv-lsm-storage-engine.md) 第 7.5、7.6 节。

## 默认引擎

- 新建数据库默认使用 LSM 引擎（memtable + 多层 SST + manifest）。关系表共用每个数据库的 `tables/lsm/` 实例；文档集合、图、KV 命名空间等其余 keyspace 各自是独立的 LSM 目录。
- 已经是 LSM 布局的目录总是以 LSM 打开，与配置无关。
- 嵌入式使用通过 `KvOptions.StorageEngine` 选择引擎；Server 通过配置 `SonnetDBServer:Kv:StorageEngine`（`Lsm` 或 `Legacy`）选择。设为 `Legacy` 时只影响新建目录。

## 打开时迁移

`StorageEngine = Lsm` 且 `LegacyMigration = Auto`（默认）时，含 legacy 数据的目录在首次打开过程中迁移：

| 对象 | 迁移结果 |
|---|---|
| 独立 keyspace（文档、图、KV 等） | 原地转为独立 LSM 布局；保留每条记录的版本、TTL、`LastSequence` 与 generation |
| 关系表 | 数据导入本数据库的共享实例；行版本按新的提交 sequence 重新编号 |

- 迁移在开放读写之前完成，耗时与数据量线性相关。
- 可用空间不足（legacy 大小 × 1.2 + 64 MiB）时跳过迁移，继续以 legacy 打开。
- 迁移中断（进程退出、断电）后，下一次打开会补完或从头重做，旧数据在切换完成前不会被修改。
- 只想保持 legacy 时设置 `LegacyMigration = Disabled`（Server：`SonnetDBServer:Kv:LegacyMigration`）。

## 保留与回滚

- 迁移前的文件移入目录下的 `legacy-<sequence>/`，默认保留 7 天（`KvOptions.LegacyMigrationRetention`），期满后在下一次打开时删除。
- 保留期内且迁移后**没有任何新写入**时可以回滚：独立 keyspace 使用 `KvLegacyMigration.RollbackStandalone`，关系表使用 `KvLegacyMigration.RollbackShared`（目前只有内部 API，没有命令行入口；回滚时目录不能处于打开状态）。已有新写入时回滚会被拒绝，需要先导出数据。

## 降级

- 迁移后的目录在 `generation.meta` 中写入 v3 标记。旧版本二进制读到它会明确报错，不会把数据当作空库打开。
- 降级到旧版本前，必须在保留期内回滚，或先导出数据再导入旧版本。
- 备份：迁移不会额外做全量备份。重要数据建议在升级前手动备份。

## 验证范围

LSM 默认切换时依据本机功能回归。后续已增加独立 KV／共享表共 13 个迁移阶段的真实进程强杀测试，并运行 10 万／100 万条有界本机基线，具体源码身份、命令、结果和限制见 [KV-LSM 本机容量与恢复证据](../benchmarks/kv-lsm-capacity-20261110.md)。这些结果不等于完整容量、随机强杀至少 200 轮、真实旧版本二进制、固定硬件或长稳门禁通过。
