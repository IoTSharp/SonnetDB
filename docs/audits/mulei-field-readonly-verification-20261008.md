# 木垒 SonnetDB 真机验证任务与结果（2026-10-08）

本次直接连接 `mulei-direct-target`，于北京时间 **2026-10-08 02:33:47–02:38:50** 完成只读核验。**5 个明确拆分的现场子项通过，已标记完成；M41/M42、M36 等整体验收继续待验。**

任务来源为现场对应版本的 [SonnetDB 真机待办](https://github.com/IoTSharp/SonnetDB/blob/6a8d82d28561ff9be369603e2405f780cd9b0585/ROADMAP.md#真机验证待办)，共 16 类。新路线图已把部分条目归并为 13 类，并增加 M44/M45/M46；新规划和新工作台源码不能据现网旧版本直接完成。

原始请求、返回行、完整 NDJSON end、SQL SHA-256、实际执行诊断、对象读取与进程回收记录见 [原始证据 JSON](mulei-field-readonly-verification-20261008.json)。其 SHA-256 为 `652ab28470040c90ea8cbab03172752570fe055a4e00ef579effff3bbbcb239b`。证据绑定现场提交 `6a8d82d28561ff9be369603e2405f780cd9b0585`，并不表示最新主仓库全部能力已经部署。

## 已完成的现场子项

- [x] **ML-SDB-01：当前 ARM64 配置与持久挂载核验。** 主机 `aarch64`、48 个逻辑 CPU、约 250.45 GiB 内存。数据库镜像为 `tolnsd.eth0.click:5000/sonnetdb:mulei-arm64-20261005-desc-ordered`，镜像 ID 为 `sha256:e6a1e853471bf5f1afd6a19d506a9cb47de784aa9aea5f48fe0b265b27cad810`；容器 healthy、restartCount=0、OOMKilled=false，readiness 首尾均为 HTTP 200/Healthy，已预热 2 库 61 表。DataRoot=/data 对应主机 `/var/data/tolnsd-storage/sonnetdb`，对象目录独立挂载 `/var/data/tolnsd-storage/objects`。这项验收只证明当前运行配置及挂载，不包含跨重建恢复。
- [x] **ML-SDB-02：六个目标索引的名称与列序核验。** SHOW INDEXES 完整返回，DeliveryTasks 站点/目的地/状态/时间复合索引 1 个、FlowRevisions 索引 2 个、提醒时间索引 3 个均与预期一致。目录存在是发布状态的依据，响应没有原始 building/published 状态列。
- [x] **ML-SDB-03：真实倒序分页与实际读取放大核验。** 已完成终结历史媒体任务的 48 行 SELECT，顺序正确、Id 唯一、未截断；OFFSET 24 的 24 行等于首批后 24 行。EXPLAIN ANALYZE 真执行命中目标复合索引，actual_candidate_rows=48、actual_examined_rows=48、actual_rows=48，实际检查/返回放大比为 1。仅完成这一 SQL 样本，不替代 M41 的冻结同语料整项。
- [x] **ML-SDB-04：REST SELECT 预览边界。** 默认请求完整返回 6 行；previewMaxRows=3 返回相同前 3 行、truncated=true；SQL LIMIT 3 且 previewMaxRows=3 时完整返回 3 行、truncated=false。三次均收到合法 meta/rows/end，rowCount 与实收行数一致，没有尾部错误。
- [x] **ML-SDB-05：真实流水三图对象完整读取与内容摘要对账。** 流水 `6500261E142026100802000032` 的车头/侧身/车尾对象 HEAD 和完整 GET 均为 200，实收字节与数据库及 HEAD 一致，ETag 一致，JPEG 签名正确；三个完整下载的 SHA-256 均与数据库对象键的内容摘要一致。

六个索引的精确合同：

| 表 | 索引 | 列序 |
|---|---|---|
| DeliveryTasks | IX_DeliveryTasks_RoadTolnsdSiteCode_DestinationCode_Status_CreatedAt | RoadTolnsdSiteCode,DestinationCode,Status,CreatedAt |
| RoadTolnsdFlowRevisions | IX_RoadTolnsdFlowRevisions_CreatedAt_Id | CreatedAt,Id |
| RoadTolnsdFlowRevisions | IX_RoadTolnsdFlowRevisions_FlowId_Revision | FlowId,Revision；唯一 |
| T_OverlimitReminderPlateRecognitions | IX_T_OverlimitReminderPlateRecognitions_CaptureTime_Id | CaptureTime,Id |
| T_OverlimitReminderPlateRecognitions | IX_T_OverlimitReminderPlateRecognitions_AddDateTime_Id | AddDateTime,Id |
| T_OverlimitReminderPlateRecognitions | IX_T_OverlimitReminderPlateRecognitions_UploadTime_Id | UploadTime,Id |

三图完整读取：

| 图片 | 数据库/HEAD/GET 字节数 | 完整下载 SHA-256；与对象键一致 |
|---|---:|---|
| 车头 | 36,904 | b3317e03c58b72652c46ab0852d183e0aaa264fc1c068b671b128afb6261a1d7 |
| 侧身 | 55,004 | 1498a7fe88f895551a12a1f517abba89a4e4c18599e24070d363d7f6d6d81cd5 |
| 车尾 | 52,953 | 031bbc8ca712203174b989e7ffb340a0ab7f2dcf9175357a3d91889538840bc4 |

内容摘要依据为现场应用 `VehicleMediaStorage` 的内容寻址键合同：保存时以原字节 SHA-256 生成文件名。原始探针的 checksumOracleAvailable=false 表示没有额外 checksum header/oracle；归档的 derivedObjectChecks 单独记录了下载摘要与数据库对象键对账。ETag 不作为 SHA-256。此次没有做图片完整解码、视觉识别、源相机原件或中心副本对拍。

## 能继续使用木垒数据库验证的任务

| 来源 | 本次状态 | 剩余验收 |
|---|---|---|
| M41 #381 / mulei_same_corpus | 部分通过：单个真实 DESC 查询 | 冻结真实语料、五类查询形状、旧现网查询指纹及结果 oracle 对账、检查/返回放大比；当前历史时间上界不是冻结快照 |
| M42 覆盖索引 / SQL-002 | 部分通过：REST 预览边界 | secondary_index_only 与回表对照、Frame/ADO 完整结果兼容、取消/断连后的恢复、执行预算及 heap/首行；本次未验证这些子项 |
| M41/M42 固定 ARM64/x64 | ARM64 当前运行核验完成 | 固定同语料多次 P50/P95/P99、RSS/heap/分配/GC、锁与准入队列、逻辑/物理 I/O；还需要 x64 对照 |
| M36 #323 对象 | 三个小图片的现有对象读取完成 | SDK/CLI、分页无重复/遗漏、大文件流式内存、multipart、取消/续传/重试、高变更分页、容量及跨进程恢复 |
| M36 #325/#326 MQ | NOT_RUN | topic/message/consumer offset 可先只读盘点；nack/reset/dedup、实例 snapshot/restore 和九模型重开需要隔离验证环境 |
| M36 #314/#315 时序 | NOT_RUN | 已有 measurement 可做 REST/Frame 查询 parity 与取消；重开、吞吐和固定容量另验 |
| M36 #317 KV | NOT_RUN | 真实 keyspace 的 continuation/TTL/热点可先有界只读核验；pipeline 背压与容量需要专用负载 |
| M36 #318/#319 FullText | NOT_RUN | 需要真实全文语料做远程查询/设置 parity、相关性 oracle；rebuild 与容量另验 |
| M36 #320/#321 Vector；M35 | NOT_RUN | 需要真实向量语料、模型 profile、Recall@K/质量 oracle；生命周期和模型换代另验 |
| M39 / #339 | NOT_RUN | 现网事务/WAL可观察；混合 DML、触发器/回滚、TTL、批量 measurement、deferred/outbox、crash/replay 需隔离写入与故障窗口 |

## 需要其他环境或维护窗口的任务

- [ ] M41 #381 process_crash_replay、backup_restore_deployment：在隔离恢复副本核对 WAL/checkpoint、各模型不变量、恢复耗时和逐模型对拍。
- [ ] M41/M42 I/O、冷启动、Native AOT 目标 RID 与首查：需固定版本和维护/隔离窗口；当前进程为暖运行，不能算冷启动或 AOT。
- [ ] M41/M42 seven_day_mixed_workload：连续 168 小时及完整性能、WAL/checkpoint、异常重启记录。当前容器从 10 月 5 日 11:41:44 启动，到本次结束约 63 小时，也没有完整长稳指标序列。
- [ ] M19 #125：固定 Linux x64 的百万 series、万 segment、20 次 kill/reopen、万 measurement 与四档容量报告。木垒 ARM64 不满足其冻结平台合同。
- [ ] M25 #174：百万/千万文档写查、TTL、重建、备份恢复、内存曲线和硬件 attestation。
- [ ] M40 #352/#367 与 Couplet：真实 Graph 外部对拍、1m/10m、LDBC/Graphalytics、AOT、hard-kill、双客户端恢复和 7 天混合负载。
- [ ] M20 #136：GitHub light/full 和连续七次 scheduled 原始 artifact；不能靠木垒业务数据库证明。
- [ ] M27 #184/#185/#187/#340：真实 provider/模型质量成本、IdP/broker、双网/续流、Studio 完整旅程。
- [ ] M29 #258：干净 Windows/WebView2 安装、升级卸载、宿主生命周期和端口冲突；现场 Linux 数据库不能代替。

## 证据解释与执行记录

实际请求耗时不混用：48 行 SELECT 客户端为 **8,571.460 ms**，end 服务端为 **2,692.0612 ms**；OFFSET 分别为 **3,418.289 / 1,868.8371 ms**。后续热 EXPLAIN ANALYZE 的 actual_execution_ms=2.6138 是该次引擎执行指标，不能替代先前请求时延或推导整体性能收益。统计标记 statistics_stale；actual_* 为实测字段，不采用 estimated_scanned_rows 作实测。

SQL 固定 CreatedAt<1791129600000 和 Status=succeeded，但底层库仍运行，不能称为冻结语料。日志 fingerprint 为完整 SQL 文本 SHA-256，尚未与旧现网规范化指纹对账。48 行时间无并列，现场同时间组的 Id 反向顺序仍需另补样本。响应中的 actual_peak_memory_bytes 不是 CLR heap 或进程内存硬上限。

结束时数据库 RSS 约 79.79 GiB、容器上限 128 GiB、线程 80；这些是一次快照，I/O 字段是进程累计计数，不代表本次查询物理读取或稳定容量。

本次共 6 个 SSH 批次、11 条只读 SQL、三个对象各一次 HEAD/完整 GET，以及首尾 readiness；没有生产写入、DDL、容器重建或故障注入。每批最多 4 条 SQL/55 秒、每请求 12 秒、最多 64 行/64 KiB；对象每个最多 8 MiB/17 块/12 秒。先以 6 行样本验证边界，再扩大到 48 行。所有 Bash 脚本通过指定安全 wrapper 的 stdin 传输，凭据仅在现场 Python 内存读取。

原始日志为 `.runs/performance-20261005/field-audit-20261008-*.stdout.log`，执行脚本和清理记录在 `.runs/sonnetdb-field-20261008/`，保留作复核制品。6 个本地 wrapper 及其子进程均已退出，10 个先前登记的远程查询/inspect PID 均已不存在，本任务生成的宿主脚本与锁已回收。独立只读审核通过；核验记录及任务状态经用户授权提交推送，具体提交身份以 Git 记录为准。
