# 在线关系表索引

SonnetDB 支持 `CREATE INDEX ... ONLINE`。当前源码的能力不代表已经部署的服务器版本具备该语法；旧服务器遇到末尾 `ONLINE` 会返回解析错误。

```sql
CREATE INDEX IF NOT EXISTS ix_capture_time
ON captures (capture_time, id) ONLINE;
```

此入口分批回填普通非唯一关系表索引。每次调用最多处理 64 页、每页 16 行，并受 30 秒总预算约束；每页锁等待与工作共用 250 毫秒协作预算，页间释放锁并退避。构建中的插入、更新、删除会同时维护候选索引。只有完整索引持久发布到目录后，查询规划器和 `SHOW INDEXES` 才能看到它。

| 结果 | 含义 | 调用方动作 |
| --- | --- | --- |
| `operation=create_index_online_pending`，`rowsAffected=0` | 本批完成但索引尚未发布，或等待资源/冷开 | 有界退避后重提相同 SQL |
| `operation=create_index_online_complete`，`rowsAffected=1` | 索引已发布，含同定义幂等确认 | 停止重试，可登记迁移完成 |

不能把 HTTP 200、执行未抛异常或 `rowsAffected=0` 当作在线索引已完成。只得到影响行数的调用方，应等到 `1`，也可通过 `SHOW INDEXES ON captures` 确认索引存在及定义。普通 `CREATE INDEX` 的影响行数合同不能直接套到 `ONLINE`。

索引进度与每批新增索引键在同一 WAL 批次中持久提交。取消和重启不会发布半成品；正常读写完成表的冷恢复后，重提相同声明可续建。在线入口不会主动承担可能较慢的冷开操作：未打开且索引尚未完成的表返回 pending；目录中已发布的同定义索引即使表尚未打开也立即返回 complete。

当前仅支持普通非唯一关系表索引，不支持 UNIQUE、文档集合、JSON path、SPARSE、TTL 或 partial 索引。在线构建期间会拒绝破坏其结构前提的 DDL 和 TRUNCATE；同表一次只推进一个待建索引，同名不同定义不会被 `IF NOT EXISTS` 静默接纳。

其他数据库也有降低建索引期间业务阻塞的能力，但语法和锁语义不同：PostgreSQL 使用 `CREATE INDEX CONCURRENTLY`，SQL Server 使用 `WITH (ONLINE = ON)`，Oracle 使用末尾 `ONLINE`，MySQL/InnoDB 使用 `ALGORITHM` 和 `LOCK` 选项；SQLite 没有对应的并发建索引模式。在线不是零锁或零成本，仍会占用 CPU、I/O，并在定义提交时取得必要的短锁。

本地回归覆盖 pending→complete、半成品不可见、构建期间并发读/插入/更新/删除、持久断点重开续建、完成后的冷表确认，以及不支持索引类型的显式拒绝。这些测试不代替 ARM64 现场负载和尾延迟验证。
