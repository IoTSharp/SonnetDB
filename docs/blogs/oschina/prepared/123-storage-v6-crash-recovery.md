# Segment v6 与崩溃恢复：把可靠性做到文件尾部

存储格式需要同时回答两个问题：正常数据怎样读取，损坏数据怎样拒绝。SonnetDB 当前 writer 使用 Segment v6，将部分扩展索引放回主段文件，并在 header 中保存 mini-footer 摘要，减少依赖尾部单一定位信息的情况。

本文依据当前 writer、reader 与格式结构整理。Segment 格式版本 6 与软件正式版本 4.0.0 是两套编号，不能互相替代。部署与迁移应以对应 tag 的格式支持和实际备份为准。

## 从 sidecar 到嵌入扩展区

v5 时期部分能力可以保存在 `.SDBVIDX` 向量索引和 `.SDBAIDX` 聚合 sketch sidecar 中。当前 v6 writer 在 BlockIndex 后、Footer 前组织嵌入 extension section，减少主段与旁路文件的配对工作。

```text
SegmentHeader
    -> BlockHeader + timestamp/value payload
    -> BlockIndexEntry[]
    -> embedded extension sections
    -> SegmentFooter
```

这是一份布局示意，不是让应用自己计算偏移的 ABI。固定结构仍按 little-endian 合同读写；应用应使用引擎读取，而不是假定某个版本的保留区可以随意复用。

## mini-footer 的四项事实

header 保留区的摘要包含 `IndexCount`、`IndexOffset`、`FileLength`、`IndexCrc32`。它不是一份完整数据副本，也不是用来恢复已经损坏的 block payload。

reader 先尝试主 footer；失败时按 v6 mini-copy 的结构和长度约束尝试定位。之后仍要校验索引范围、索引 CRC、block header 与索引的一致性，以及具体 payload 的完整性。默认校验不应为了“能打开”而关闭。

例如一个段的 footer 被截断，header 中摘要仍完整，且索引与数据满足校验时，可能走受控 fallback。若索引也被覆盖，摘要里的 CRC 并不能重新生成索引内容，读取器应返回明确损坏错误。

## 兼容读取与回滚是两件事

当前 reader 保留旧 v4/v5 读取路径和 legacy sidecar 兼容，当前新段由 v6 writer 生成。flush/compaction 可以逐步生成新格式段，不要求每个旧文件立刻离线转换。

这是新 reader 对旧数据的兼容，并不证明旧软件 reader 能读取新 v6 段。升级前需要完整目录备份；回滚到旧二进制时，必要时恢复升级前目录，不能只替换程序文件。

## 文件读取不能代替实例恢复

mini-footer 主要解决段文件的定位与诊断。WAL replay、flush publication、catalog、tombstone、compaction swap 和实例级其他模型的持久化状态仍有各自恢复合同。单段读取成功不能冒称“数据库任意断电都能恢复”。

实践中可以按以下顺序验收：

1. 用固定数据建立可重复查询结果并记录软件/格式版本。
2. 在可丢弃副本上分别测试正常读取、尾部截断、索引 CRC 损坏、payload 损坏与旧版本文件读取。
3. 比较恢复后的完整结果与错误类型，确认坏数据没有被静默接受。
4. 另做进程崩溃、系统故障和备份恢复测试，保留实际环境与边界。

这些是验收建议。本次整理只读文档和源码，没有执行故障注入或重新证明任何目标硬件的断电恢复能力。

参考：[Segment writer](https://github.com/IoTSharp/SonnetDB/blob/main/src/SonnetDB.Core/Storage/Segments/SegmentWriter.cs)、[Segment reader](https://github.com/IoTSharp/SonnetDB/blob/main/src/SonnetDB.Core/Storage/Segments/SegmentReader.cs)、[mini-footer](https://github.com/IoTSharp/SonnetDB/blob/main/src/SonnetDB.Core/Storage/Format/SegmentFooterMiniCopy.cs)、[正式 4.0.0](https://github.com/IoTSharp/SonnetDB/releases/tag/v4.0.0)。
