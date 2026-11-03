# 5.0 核查后的 SonnetDB 后续任务

日期：2026-11-04（Asia/Shanghai）。按用户“未完成且需SonnetDB完成的内容直接创建Issue，后续继续”的要求执行。创建前读取现有开放Issue及相关已关闭工作范围；创建后逐项独立回读确认首批10项均为OPEN、正文有唯一标记及验收清单。跨机器恢复候选及WorkBuddy渠道审查后追加#402、#403并独立回读，合计12项。

| Issue | 后续范围 | 已完成范围及边界 |
| --- | --- | --- |
| [#392](https://github.com/IoTSharp/SonnetDB/issues/392) | EF迁移按操作能力保留DDL事务，验证多命令原子性 | Core/ADO关系DDL事务已有；EF的EndDdlStatement仍统一suppressTransaction。 |
| [#393](https://github.com/IoTSharp/SonnetDB/issues/393) | 自引用外键表的受限真实主键变更迁移 | 本轮主键不变的native日期/schema演进已修；真正主键定义变化仍明确拒绝。 |
| [#394](https://github.com/IoTSharp/SonnetDB/issues/394) | EF OwnsMany集合持久化与元素路径 | #266的基础JSON/OwnsOne/Passkey已交付，不冒称集合生命周期完成。 |
| [#395](https://github.com/IoTSharp/SonnetDB/issues/395) | EF ExecuteUpdate参数化JSON路径原子更新 | Core JSON函数与普通跟踪更新不等于EF翻译已实现。 |
| [#396](https://github.com/IoTSharp/SonnetDB/issues/396) | 最小受支持EF Provider NativeAOT路径 | Server/CLI/核心AOT已有；EF及完整ABP AOT保持独立边界。 |
| [#397](https://github.com/IoTSharp/SonnetDB/issues/397) | M49九模型typed Client及真实HTTP/2语言消费者 | 共享Protocol、轻量Client和已有codec/SQL首片不重做；其余冻结切片分批推进。 |
| [#398](https://github.com/IoTSharp/SonnetDB/issues/398) | 公开安装器的干净系统安装/升级/卸载/数据保留 | 资产构建、哈希及真实Server包合同不替代实机安装。 |
| [#399](https://github.com/IoTSharp/SonnetDB/issues/399) | 固定硬件容量/查询性能/恢复报告 | Hosted quick/ci证据保持其范围，不推断固定目标或生产。 |
| [#400](https://github.com/IoTSharp/SonnetDB/issues/400) | Server实例MQ一致备份/恢复及归属验收 | 已有消息/ACK/目录恢复不重做；单库备份不覆盖实例MQ。 |
| [#401](https://github.com/IoTSharp/SonnetDB/issues/401) | M50按库ms/us/ns及无损全链路迁移 | 当前measurement time仍ms；关系DATETIME2/DTO的100ns不等于时序ns。 |
| [#402](https://github.com/IoTSharp/SonnetDB/issues/402) | 固定tag发布恢复、外部结果对账及已验证资产白名单 | 8bf0e812备用分支保留；13策略单测不等于实际恢复及未知/部分发布对账已验。 |
| [#403](https://github.com/IoTSharp/SonnetDB/issues/403) | M51 WorkBuddy正式HTTPS、宿主、平台送审与生命周期闭环 | 43工具/授权/预算/AOT已完成；本地HTTP、静态包和旧镜像不是渠道批准或正式宿主验收。 |

已有 [#230](https://github.com/IoTSharp/SonnetDB/issues/230) 继续跟踪正式发行与公开消费者交付；压缩优化继续使用已有 [#367](https://github.com/IoTSharp/SonnetDB/issues/367)。上游FreeSql/XCode/Chloe/SqlSugar的合并和包发布关联现有PR，不重复创建为SonnetDB引擎缺陷。

各Issue包含当前依据、剩余工作和完成条件，不承诺未经验证的性能/平台或把已完成代码重做。本清单不替代当前5.0必需的精确SHA门禁、公开包与Docker回读。创建/回读收据位于ignored `artifacts/followups-20261104/issue-creation-receipt.json` 与 `issue-readback.json`，没有凭据或外部私有数据。
