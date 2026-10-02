# 单分区 CDC 到持久流恢复桥接

`CdcStreamingBridge` 将专用单分区 `CdcEventSpool` 接入真实 `FileStreamingSubscription`。绑定包括源、实体、schema/version、partition、源和目标绝对路径、目标订阅定义/容量及桥接选项；重开必须与保存身份一致。它使用独立版本化状态、source-generated JSON、SHA-256、原子替换和单执行者文件锁，保持现有 spool 和订阅格式。

一次推进先保存一批 outbox，包含稳定的源位点、目标序号及完整 CDC 编码载荷，再逐事件发布。目标目录保存独立接收凭证；凭证完成后才记录 outbox 发布进度，全部交付后才确认源 spool，最后保存桥接提交位点。事件仍按原始 CDC 载荷传入流，不把捕获后的副本固定行导出冒充持续 change feed。

目标接收凭证在消费者 ACK、回收事件后继续存在，证明最后一次已完成发布。重开时先协调待完成的发布/源 ACK，再接收下一批。源 ACK 越过桥接证明、绑定错配、损坏或丢失状态、非法 schema/分区和目标映射序号缺口均拒绝推进。源 offset 只要求按 spool 合同单调，原事件 Sequence 与目标连续序号分别保存。目标发布结果未知时，仍存在的 spool 事件须按稳定身份与摘要核对；若接收凭证尚未完成而目标事件已被外部消费者回收，桥接明确 fail closed，需要人工恢复交接，不能推测成功后确认源。

调用方应在开始消费前恢复桥接意图，并独占目标发布入口和源 spool 的 ACK。每批事件数、CDC 字节数、状态字节数和操作墙钟时间均有显式上限；目标容量不足立即拒绝并保留 outbox，释放句柄后排空目标，再重开桥接继续。目标必须采用 Deliver 迟到策略，不能将迟到丢弃视为已交付。

这是本地可恢复交付协议，跨目录提交有明确顺序和未知结果边界；不提供业务副作用原子事务、通用 exactly-once、多分区原子性、远程传输、schema 演进或生产耐久性证明。[组合样例](../samples/SonnetDB.CdcStreamingJourney/README.md)和[本轮报告](audits/roadmap-parallel-next-20261002.md)分别记录实际入口和证据等级。
