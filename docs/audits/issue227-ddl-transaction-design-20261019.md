# Issue #227 DDL 事务独立设计

本树基于 `9eb4028745f6f20b92b8e314faf84ff8303dbe71`，只实现并验证关系 DDL workspace、二阶段 journal、恢复协调器与前置治理底座；没有接入共享 `SqlExecutor`、`TableCatalog`、`TableManager` live publication，也没有改变已发布目录格式。

事务使用数据库排它关系写 lease，并创建完整关系目录副本。副本保留 `TableSchema`、rowstore、索引、主键与外键数据，复制有表数、行数、字节数、复制批次和总时限预算。事务首条 DDL 前会复验已有 DML mutation schema、Serializable 读基线，并把已缓冲关系 DML原子迁移到 workspace；deferred trigger 队列存在时稳定拒绝升级为持久 DDL workspace。其它模型不复制，且每条 staged DDL 在 workspace 产生副作用前回到原数据库执行跨模型名称、Modbus、property graph、view/materialized view、routine/trigger 依赖和 UDF 默认值/CHECK 治理。过程 body 仍不开放 TEMP/持久 DDL。

持久提交固定顺序为：准备 undo → durable `prepared` → publish → durable `committed` → cleanup → 删除 journal。publish 前失败可补偿并删 prepared；committed 决策已 durable 后，任何清理/响应异常都保留 committed journal，恢复只重做 cleanup，不向已发布状态写相反 rollback。每个 journal 带 SHA-256、owner、relative workspace/undo 路径、对象 pair 和目录 floor。#236 保留 floor 17，#232 namespace codec 使用 floor 18，#227 live publication 时最终接入永久 floor 19；本底座不修改 `TableSchemaCodec`。

真实子进程测试已经覆盖 prepared/committed 决策 durable 后强杀、recovery backend 后第二次强杀，以及第三次幂等恢复；完成前不宣称事务 DDL 已连接到 ADO/Server 或正式包。
