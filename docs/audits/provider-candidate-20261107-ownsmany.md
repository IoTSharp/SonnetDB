# #394 共同 Provider 候选：实际包应用的集合生命周期

2026-11-07（Asia/Shanghai）。本专题属于 #392/#395/#394 的同一共同候选，由主监督冻结最终已提交 source、四包、匹配 Server/RID 与指纹。当前编写消费者时的整合基线为 `537dc9bb9ea5bc701d722c4663593d10d26e0467`；本文件不把旧候选或源码单测结果外推为新包消费 PASS。

本轮 Worker C 只拥有 `eng/probes/issue394-ownsmany-app/` 与本专题。应用采用真实 PackageReference，执行运单装箱批次/零件的 EF SaveChanges 跟踪生命周期，不用 ExecuteUpdate 替代。构建/restore/测试/format/pack、候选来源清单、Server 生命周期及最终验收均由主监督串行排队。消费者须复制出仓库并使用独立源配置、packages/http-cache，不能导入仓库 ProjectReference 或既有缓存。

| 业务合同 | 应用检查 | 本阶段执行状态 |
| --- | --- | --- |
| 插入、读取、重复业务值、可空属性、Guid/converter | seed 插入，独立 verify 重开并核存储/模型表示。 | NOT_RUN：等待共同候选构建与消费。 |
| 根与嵌套 OwnsMany | 单元素及嵌套属性 SaveChanges、父数组删除/追加、清空/从空追加；根数组元素修改/追加。 | NOT_RUN。 |
| null、空、缺失与 SQL NULL | 单独四行，验证 JSON 表示及 EF 物化，不折叠为同一种值。 | NOT_RUN。 |
| 父 ordinal 保存后的下一次修改 | 先核父数组顺序已提交；同跟踪器清空须 identifying-key 拒绝、零下一条 SQL、原文不变。 | NOT_RUN：保留 EF10 现有边界。 |
| 显式 Clear/重新查询后继续变更 | 重载持久身份后再次改嵌套元素、清空/追加及独立进程重开。 | NOT_RUN。 |
| 事务与并发 | 外部连接看不到事务内嵌套修改，回滚恢复原文；显式 Stamp 胜出后 stale SaveChanges 并发异常/零行。 | NOT_RUN。 |
| 目标外 JSON | 大整数及 `1.2300e+04` 原文在元素/集合操作及重开后保留。 | NOT_RUN。 |
| embedded / REST / 原生 HTTP/2 Frame | 每协议独立 seed/mutate/verify 进程；外层核真实请求与同源 Server。 | NOT_RUN。 |
| 公开 NuGet-only / 实机 / NativeAOT / 物理掉电 | 与隔离候选和受控宿主分别记账。 | NOT_RUN；本应用不声明 EF NativeAOT。 |

源码消费者已准备，不等于构建通过。主监督完成执行后在本表或共同 manifest 中填写准确版本/source、每协议结果、原始回执位置与失败边界。若暴露产品缺陷，Worker C 只提供复现和最小归属，公共产品修复由主监督集成并重新冻结候选。

命令是 `dotnet Issue394.OwnsManyApp.dll seed|mutate|verify <absolute-database-directory>`；远程用进程级 `SONNETDB_APP_CONNECTION`。每 phase 120秒取消预算，外层须提供墙钟期限、唯一服务归属及进程树回收。SQL与断言记录有固定数量上限，失败/超时不自动重试。本 worker 没有运行任何 build/test/restore/format/pack，也没有创建服务或常驻进程。

父 ordinal 变化后 Clear/重载是受限支持的应用步骤，不能据此声称任意父重排后无刷新连续跟踪已完成。公开新 Provider、匹配 Server、正式消费及适用实机条件未完，#394 保持 OPEN。历史合同与原始失败保留在 [#394 源码切片审计](issue-394-20261105.md)，正式发行仍按主监督记录的延期决定。
