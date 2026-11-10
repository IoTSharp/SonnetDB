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
- 未完成的旧多表事务日志先按原目录恢复并持久完成，之后才允许自动迁移。共享导入保留表的 generation；空表也记录实际提交的导入 sequence，可在无新写入且仍有保留文件时回滚。
- 只想保持 legacy 时设置 `LegacyMigration = Disabled`（Server：`SonnetDBServer:Kv:LegacyMigration`）。

## 保留与回滚

- 迁移前的文件移入目录下的 `legacy-<sequence>/`，默认保留 7 天（`KvOptions.LegacyMigrationRetention`），期满后在下一次打开时删除。
- 保留期内且迁移后**没有任何新写入**时可以回滚。已有新写入时拒绝回滚，需要先导出数据。共享表打开时初始化计数或消费维护令牌不会被误判为业务写入。

关闭使用该数据库的进程后，指定数据库根目录与具体 KV／表目录：

```powershell
sndb kv rollback-migration --database D:\data\example --path D:\data\example\kv\keyspaces\cache --offline
sndb kv rollback-migration --database D:\data\example --path D:\data\example\tables\rowstore\6974656D73 --offline
```

表目录名取实际目录，不能直接把表名代入。命令自动识别独立 KV 或共享表，取得数据库和 keyspace 的排他租约；运行中的数据库、越界／嵌套数据库目录、缺失的保留文件或迁移后新写入均会明确失败。`--offline` 是调用意图，实际租约检查仍然执行。

回滚先写 `generation.meta` v4 临时屏障，再持久写入 `lsm-rollback.pending`，随后隔离 LSM 文件、恢复 legacy、结束回滚；有意图的中断在再次运行或下一次打开时补完。恢复完成前，v4 屏障让旧 LSM 版本也明确拒绝半恢复目录。屏障已写而意图尚未落盘时，新版普通打开会报错，重跑回滚命令即可接续。`lsm-rolledback-*` 保留隔离文件，不自动删除。完成后若要保持 legacy，下一次打开设置 `LegacyMigration=Disabled`（或选择 `StorageEngine=Legacy`），否则默认自动迁移仍会再次生效。

## 降级

- 迁移后的目录在 `generation.meta` 中写入 v3 标记。旧版本二进制读到它会明确报错，不会把数据当作空库打开。
- 降级到旧版本前，必须在保留期内回滚，或先导出数据再导入旧版本。
- 备份：迁移不会额外做全量备份。重要数据建议在升级前手动备份。
- 共享表后续写出的 SST 使用 v3，旧 LSM 读取器也会明确拒绝；升级前的 v1/v2 SST 由新版本继续读取。维护令牌与行数属于可重建元数据，旧写者变更 sequence 后，新版本会在打开阶段重算行数。

## 验证范围

LSM 默认切换时依据本机功能回归。后续已增加独立 KV／共享表共 13 个迁移阶段的真实进程强杀测试，并运行 10 万／100 万条有界本机基线，具体源码身份、命令、结果和限制见 [KV-LSM 本机容量与恢复证据](../benchmarks/kv-lsm-capacity-20261110.md)。这些结果不等于完整容量、随机强杀至少 200 轮、真实旧版本二进制、固定硬件或长稳门禁通过。
