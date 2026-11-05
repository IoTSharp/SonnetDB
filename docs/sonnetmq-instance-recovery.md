# Server 实例 MQ 的离线快照与恢复

本入口处理 Server `DataRoot/.system/mq` 的整实例队列数据，复用已有 `SonnetMqStore` 快照 manifest v1。它独立于单数据库 `sndb backup`，不备份数据库目录、对象外部卷、`.system/users.json`、Token/grant、Server 配置或其它控制面文件。恢复后的数据库与身份须由各自备份或管理员配置重建；不能仅凭 MQ 文件恢复宣称九模型或整个 Server 已恢复。

## 前提与来源

以本机具有源目录读写和目标目录创建权限的运维身份执行。先停止匹配版本 Server 及其它 MQ owner，再显式添加 `--offline`；命令不替用户停止、配置或重启服务，不接受远程 URL 或数据库 Token。数据库 Write grant 不包含这些本机运维动作。

目录模式 MQ 从 `Open` 前的恢复阶段到 `Dispose` 完成持有 `sonnetmq.lock` 生命周期租约，阻止同进程或其它进程同时打开该实例，包括尚未创建消息段的空实例。锁文件不属于快照数据。**旧版本 Server 不持有这一租约，必须由操作者实际停止旧进程；新 CLI 的租约检查不能证明旧版本进程已经停止。** 单文件模式沿用其既有原生 API，本 CLI 首片只维护 Server 的目录模式。

`--server-root` 必须指向真正的 Server DataRoot，不是某个数据库目录或 MQ 叶目录。源 `.system/mq` 必须存在；命令不会建立空队列冒充备份源。所有参数要求绝对路径，拒绝目录联接/符号链接及源、目标、快照目录的相互嵌套。snapshot 输出与 restore 的新 Server DataRoot 必须不存在，不提供 `--overwrite`。

## 命令

在确认 Server 已离线后创建快照：

```powershell
sndb mq snapshot --server-root D:\SonnetDB\data --output D:\SonnetDB\mq-backups\snapshot-20261105 --offline
sndb mq inspect --snapshot D:\SonnetDB\mq-backups\snapshot-20261105
sndb mq restore --snapshot D:\SonnetDB\mq-backups\snapshot-20261105 --server-root D:\SonnetDB\restored-instance-20261105 --offline
```

恢复结果位于新 DataRoot 的 `.system/mq`，保留快照内的全部 Topic：包括多个数据库限定名称、对应死信 Topic 以及无数据库前缀的 legacy Topic。恢复不重命名或重新分配 Topic，也不通过点号猜测其归属。

数据库限定 Topic 仍由 Server/MCP 的当前数据库上下文和 grant 控制。原生实例存储可看到所有 Topic；面向数据库的 MCP/REST 只显示该数据库限定的 Topic，legacy Topic 不自动公开给任意数据库。MQ 文件的恢复不附赠读取其它数据库或写入、确认、运维权限。

每次成功调用向 stdout 输出一个 JSON 回执，包含动作、`instance-mq-only` 作用域、CLI 版本、Server/MQ 路径、manifest SHA-256、manifest 版本、创建时间、文件数量及总字节数。可以将回执保存到独立日志目录，另行记录实际 Server/CLI 源码或发行物身份及停止、启动与验收时间。回执不是 Token/用户配置备份；`inspect` 的 CLI 版本是检查工具的版本，不应冒认为原始 Server 的发行版本。

## 一致性、校验和失败

原生快照在现有实例级写锁内冻结发布、ACK/NACK、offset reset、retention 和 Topic 创建，并锁住已存在的 Topic，完成文件刷盘后复制全部队列数据。消息、headers、consumer offset、retention tombstone、NACK 与死信记录都来自同一冻结点。没有另立一个快照引擎。

快照和恢复先写本次独占的随机 staging 目录，长度及 SHA-256 验证通过后才发布。非覆盖模式只可非递归删除仍为空的兼容目标目录；若并发出现文件，或原子移动遇到新目标，操作失败并保留那些文件。失败清理只针对此次生成的 staging，不能递归删除传入的外部目标。

`inspect` 与 `restore` 拒绝缺少/截断 manifest、不支持版本、不规范/越界路径、缺少文件及长度/hash 不匹配。没有成功回执时保留失败或未知状态，先核对目标和 staging；不要将整个命令盲目重跑到已有目录。进程中断可能留下未发布的 staging 与新 DataRoot 父目录，须按准确路径和任务记录人工确认其归属；命令不会自动删除它们或覆盖目标来恢复。

文件清单预检限制为 100000 个目录项、16 GiB、32 层及 30 秒，路径祖先检查限制 128 段及 5 秒。既有原生同步 `File.Copy`/SHA-256 API 没有取消令牌；此首片没有宣称可硬性中断单次慢 I/O，也没有伪造一个已生效的复制超时选项。需要由调用方给整个进程设置适合数据规模的外部期限并保留 PID/结果，取消后先对账。生产规模、慢盘、满盘、真正复制中断、物理掉电和跨平台矩阵继续单独验收。

## 验收

本次源码与本机证据见 [GH-Issue #400 核查](audits/issue-400-20261105.md)。源码测试、实际本机 CLI、跨进程 owner、真实 Kestrel/MCP、正式发行物、WorkBuddy HTTPS 宿主及生产演练分别记录，不能相互代替。
