# 共同 Provider 候选：JSON 补货应用验收

2026-11-07（Asia/Shanghai）。本专题属于根路线第一顺序#392/#395/#394的共同候选；历史7e9单片和0d687bd9候选的PASS不转移至本轮。准确最终source、候选版本、四包、Server/RID及制品/DLL身份由主监督的共同manifest冻结。本页的消费者位于`eng/probes/issue395-json-app/`，只使用PackageReference；实际运行须移出仓库，并使用独立NuGet.Config、packages和http-cache。

## 本轮消费者增量

| 阶段 | 可检查合同 | 本轮运行状态 |
| --- | --- | --- |
| seed | 新产品、显式RowVersion=0、第三方未知JSON及精确数字/转义原文。 | NOT_RUN，等待共同候选。 |
| update | 无预读的单条带参数UPDATE；策略子树、标签数组/固定primitive元素、固定OwnsMany对象/叶子、Guid/可空Guid/offset ticks/decimal及string converter。 | NOT_RUN，等待共同候选。 |
| update→verify | 同语句库存12→14，但Minimum右值从旧库存得到15；PriorVendor读取旧集合元素。策略回滚后保持，旧版本命令0行，目标外JSON原文不变；新进程读取。 | NOT_RUN，等待共同候选。 |
| reject | 动态/负下标及converter逐行右值零SQL拒绝；数组越界使先声明的库存setter一并原子拒绝；完整JSON与RowVersion保持。 | NOT_RUN，等待共同候选。 |
| compete | 两个独立DbContext/线程，以10秒Barrier起跑；相同RowVersion=1的补货命令影响行数恰好[0,1]，每项只发一条UPDATE。 | NOT_RUN，等待共同候选。 |
| verify-concurrency | 新进程读RowVersion=2、完整赢家策略及保留字段。 | NOT_RUN，等待共同候选。 |

每个协议单独建立新数据库，以seed→update→reject→verify→compete→verify-concurrency六个进程运行。embedded、REST、原生Frame分别记账；远程连接由`SONNETDB_APP_CONNECTION`传入匹配Server，不允许生产连接或协议回退。进程内部90秒取消，外部执行器还须有墙钟超时和所属进程树回收。命令回执包含逐项检查、SQL/字符串参数、竞争影响行数及实际程序目录的四DLL指纹。

## 证据与剩余门禁

- 原始本轮证据归属：`D:/source/SonnetDB/artifacts/provider-next-phase-20261107/json-update/`；重负载全部由主监督排队，持既有FileShare.None锁、CPU2/单MSBuild节点/禁持久服务。
- 候选本地feed、CI候选和NuGet.org-only公开消费是独立证据层；本专题不把源码审阅、既有单测或本地feed运行提升为公开包验收。
- 本轮未发布NuGet、正式Release或tag，未部署/重启/迁移生产。公开新包/匹配Server/适用实机门禁未完成时，#395继续OPEN。
- Provider现有运行时模型仍处于非NativeAOT边界；拒绝形状不添加客户端读改写或隐式重试。旧5.0.0负控的verify-rejected阶段保持可用。
