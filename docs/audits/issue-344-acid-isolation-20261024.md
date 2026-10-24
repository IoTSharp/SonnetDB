# Issue #344 ACID/I 复验记录（2026-10-24）

## 范围与基线

- 工作分支：`codex/issue344-acid-isolation`。
- 基线：`origin/main` `91ffc6733a1feb3396660f0a07d4c68958fd2732`（包含 #343 约束/索引一致性验证）。
- 本切片只补隔离合同的确定性验证；没有重新实现已由 #225 交付的表级乐观 `Serializable` 校验，也没有修改生产代码或存储格式。

## 既有证据映射

- `tests/SonnetDB.Core.Tests/Sql/SqlSerializableTransactionTests.cs` 已覆盖同表两次读取、谓词插入/删除、写偏斜、schema/store 身份变化、锁顺序/释放、级联/延迟触发器、自增预约、ReadCommitted 对照、提交取消及不受保护模型拒绝。
- `tests/SonnetDB.Core.Tests/Ado/SndbAdoApiTests.cs` 已覆盖嵌入式 ADO 的基本提交、显式 `Serializable` 单表提交、读己之写和回滚。
- `tests/SonnetDB.Tests/SqlSerializableTransportTests.cs` 已覆盖真实 REST HTTP/1.1 与 Frame HTTP/2 异步 Serializable 会话、变化读取/幻读/写偏斜、冲突错误码、服务端回执及旧服务端/缺失回执拒绝降级。
- `docs/sql-reference.md` 和 #225/#340 证据明确：默认/`Unspecified` 为 `ReadCommitted`；SQL 文本 `BEGIN`/`/sql/batch` 不能选择 `Serializable`；冲突后必须从 `BEGIN` 重试整笔事务。

## 本切片新增

1. Core：新增跨表读写依赖、只读跨表依赖和保存点回滚后首次读令牌的 `Serializable` 回归。外部写入按固定先后顺序执行，不使用 sleep 或概率调度；冲突后断言稳定 `table_serialization_conflict`、旧事务终态及持久最终值。
2. 嵌入式 ADO：新增默认/显式 `ReadCommitted` 双连接测试，确认未提交 INSERT/UPDATE 不可见、事务内读己之写、提交后可见；新增同步和异步 `Serializable` 冲突后连接复用与整笔重试。
3. 远程 ADO：新增 REST 与实际 Frame HTTP/2 的同步 `ReadCommitted` 可见性合同及同步 `Serializable` 冲突/整笔重试，断言请求协议和 `X-SonnetDB-Isolation-Level` 协商值；已有异步测试继续保留。

## 验证状态

- `git diff --check`：通过。
- `dotnet restore SonnetDB.slnx`：**NOT_RUN**（当前执行环境没有 `dotnet` 命令）。
- `dotnet format SonnetDB.slnx --verify-no-changes --no-restore --severity warn --exclude extensions/`：**NOT_RUN**（同上；提交前必须在具备 .NET 10 SDK 的环境执行）。
- Core/Server/Crash/真实 REST/h2c 测试：**NOT_RUN**（同上）。本报告不把源码静态检查或既有历史日志当作本次通过证据。

## 边界

本次没有验证已发布 NuGet/Server 包、FreeSql/XCode 上游消费者、跨进程并发或物理故障；也没有把 REST 结果外推为 Native Frame 以外的能力。合并前需在具备 SDK 的环境运行新增测试及相关回归，并把精确命令、SHA、通过/失败/跳过结果补入交接记录。
