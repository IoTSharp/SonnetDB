# WB78 连接库安全字段类别观察

2026-10-09，从 WB77 收尾提交 `25d274ef` 接续；本会话已闭合1/5，本片实现、测试、修复与本地集成合计一个任务，闭合后2/5。证据目录为 `artifacts/wb78-library-field-observation-20261009`，执行时限和六命令上限见 contract.json；0actual、0产品build。WB77旧预算与失败收据不变。

## 源码合同

`StudioConnectionLibrary.SaveAsync` 通过 `StudioBridgeJsonContext.Default.StudioConnectionLibrarySnapshot` 写入规范化快照。该DTO声明 Profiles、ActiveProfileId、ActiveDatabase 与派生 ActiveIdentity；profile声明八个构造字段与派生 Identity。context使用camelCase及Metadata source generation，未声明忽略派生只读属性。既有Studio bridge测试明确检查JSON中的activeIdentity/identity，但不作为本片重新执行的磁盘或实际宿主证据。

现有 `projectDatabaseSnapshot(..., { disk: true })` 仅允许前三个顶层字段及八个profile字段，遇到派生字段也拒绝。WB77实际被拒字段没有保留且目录已清理，所以只能指出源码与诊断合同的差异，不能声称查明旧运行的具体字段或来源。本片保持该拒绝逻辑及成功链，先补可核对的安全观察。

## 冻结设计

只消费已有受管读取中、512KiB字节上限内解析的JSON对象，不额外读文件、发送请求或启动进程。顶层及唯一profile各最多32个自有字段，最多两个对象；最多8份观察随现有database-recovery终态保存。每份观察只包含固定schema/state、固定允许列表内的字段名、未知字段数量及有界profile数量。不保存未知字段名、任何字段值、嵌套identity内容、原文或异常消息。

已知存储字段与派生字段分别分类；分类不授权字段、数据或进程操作。getter不执行；profiles数组和唯一profile必须通过自有data property取得，相关访问器、非普通对象、非法集合、超过数量、取消、100ms单调时限或观察异常均保留固定unknown/partial状态。其它字段只观察名字，不读取值。时限检查是同步JSON处理中的协作检查，不承诺抢占任意外部Proxy代码。原快照投影仍以原输入执行，观察器或记录回调失败不能取代原拒绝异常。原fresh ack、DOM、数据库身份、两桌面、B查询、正常关闭和严格回收门禁均保持。

## 验证与后续

微小输入先执行1例通过；首轮数据库场景32项与新12项共44/44通过。复核补中途时钟回退守卫及既有终态writer集成测试后，最终新14项、原scenario32/selection-precondition11/preparation17，共74/74、0fail/skip/cancel通过。末项合成保存验证诊断可经原encoder/terminal writer写入且passed=false/restored=null不提升。三个源码语法检查通过；source-review-hashes.json绑定源码，五个原生产序列化/快照/进程/证据实现与HEAD完全相同。

测试进程通过task-local import在执行测试前固定等待10秒，供包装器捕获身份；未更改测试断言或测试时钟。micro、首轮、最终轮分别观察3/4/6完整身份且finally全部消失，短瞬时子进程不声称完整生命周期。WB77最后format命令exit0但包装器缺tuple的FAIL收据保留；其独立fresh100身份无存活/unknown后已完成提交，不能把包装器标成PASS。

最终候选为九路径、HANDOFF仅HEAD blob加本片自有段；文档收口后执行完整restore与原CI Format Check，再核冻结内容、索引及parity后本地提交。结果见本片`restore-final.result.json`、`format-final.result.json`、`commit-checkpoint.json`，不存在的收据不预填PASS。全部属于本机源码/合成合同，不是新Native实跑、C#磁盘序列化实测或source-to-binary等价证明。

合成合同须覆盖已知派生字段、未知敏感名字/值、访问器、错类型、数量/时间/取消边界、回调异常与原拒绝保持，并回归既有数据库场景合同；实际执行数量以Node收据为准。本地结果、完整restore/原Format Check和提交绑定分别写独立收据，不预填实际宿主PASS。下一独立窗口须重新冻结源码、依赖、runtime、资源与预算，先评审磁盘字段接受合同；本片不直接开放派生字段，不重跑WB77。

真实恢复、正常退出、OS对话框、安装、Extension Host、NativeAOT、固定硬件、长期与发布继续分别待验。Graph Beta、MQ database identity及instance `.system/mq`不变；并发发布文件和HANDOFF外来字节保留，无push/部署/安装/发布/外部发送。
