# WB74 共享 HANDOFF 的 owned 本地集成

日期：2026-10-08。新会话第一个独立有界任务，闭合计1/5。继承核验不另计。WB73的PS micro/Node micro各1/1、PS14/14和Node41/41继续沿用，六源最终SHA不变；本片不重复实现或重跑源测试，0actual、0产品build。

## 问题和合同

WB73已暂存14个owned路径、完整restore/原format退出0，但其他渠道在门禁期间只追加HANDOFF尾部6633B。完整冻结前缀不变，旧commit wrapper仍因整份工作文件哈希变化拒绝，实际git commit未启动。这是原失败，不能改为成功。

新task-local guard将固定的private候选、逐owned工作段及完整冻结工作前缀关联。候选只能由WB73已证317036B、WB73 ending3841B和本次明确owned段拼接；不暂存当前完整HANDOFF。原foreign352979B、2072B、6633B及未来未归属尾部字节均保护。

验收必须同时满足：冻结前缀字节完全一致；private SHA一致；各owned段在work和candidate固定offset/length/SHA一致；HANDOFF staged blob和整个index tree一致；其它提交路径精确集合/hash一致。允许的差异只有冻结点之后最多256KiB的未暂存追加，分类unowned不代表已经确认作者。任何owned或prefix修改、中间插入、段移动、其它路径/index变化都拒绝。每输入上限2MiB，最多8个段/15个提交路径，guard10秒并支持取消。根负责磁盘/Git新鲜绑定、最终stage和commit；子代理只实施纯guard/测试并独审。

## 验证和集成证据

证据目录为artifacts/wb74-owned-handoff-integration-20261008。task-contract、继承接收、工具独审、guard micro/full结果、精确HANDOFF归属、最终tree/index、完整restore和CI原format、observed退出审计、commit/post及closed分别保存。此文在最终门禁前冻结，不预填尚未执行的PASS；最终状态以closed.json与git提交绑定为准。15个候选路径包含继承14路径和本报告，只更新owned共享段。完整restore/format均须通过才允许本地commit，代码再改后重跑，无push。

## 保留历史与下一步

WB72原singleton语义失败/过期、WB73空结果fixture首次full失败、首次prepare CAS、工具ancestry初审拒绝、ownership约144秒超120秒和更正、最后caller错误、整工作hash拒绝全部保留。WB71 numeric PID复用与WB70 actual/normalExit/cleanup false、WB69～64失败不升级。observed tuple/retained handle证据不证明whole-session orphan-freedom或processIntegrity；短shell/未观察瞬时后代/observer自身退出和ReadToEndAsync预cap内存仍是限界。

本片集成闭合后才可冻结新的Native资源/真实旅程合同，不盲重跑WB70。M47导航/九模型/MQ database identity、instance persistence和.system/mq不变，Graph Beta。真实Server、三宿主、OS文件/ManagedLocal、安装、Extension Host、AOT、固定硬件、长期和发布分别验收。


## WB74 实际关闭及 WB75 接续集成

WB74 closed.json确认closed-failed-local-integration、managed14/14耗尽、actual commit0。两轮完整restore/原CI format exit0，但第一次日期offset读取拒绝、修复后第二次无Force读取Hidden .git拒绝；index锁/attempt/commit-tree均未开始，两个attempt/object文件缺席。初审、原锁微试exit1及DateKind修复历史保留，最后observed47/retained14/owned0/reused3/unknown0与更早审计分列。

WB75新有界窗口只修task-local工具并本地集成现有六源；IO属性验证真实Directory且非Reparse，允许Hidden，hooks枚举包括hidden，active/nondefault hooks及gpgsign拒绝。Preflight与commit共用真实环境/锁前index树与字节/自有index.lock/15工作锁/锁内只读CheckOwned和HEAD/parity/whitespace；预演禁止attempt、commit-tree或ref更新。提交仍要求本片完整最终树restore/原CI format、fresh observed退出审计和独立复核，固定tree/parent再expected-old CAS。

候选只用已证323201B+WB74 ending4563B+WB75 owned段，最多七段/15路径，全部foreign前缀/尾部保持；不增加第16路径。38/14/41与micro原样继承，不重跑source或Native旅程，0actual/产品build。证据在artifacts/wb75-local-integration-20261008；本节冻结于门禁前，不预填PASS，closed.json及Git提交绑定记真实结果。用户要求提交后暂停，heartbeat PAUSED，无push或接续创建。whole-session orphan-freedom/processIntegrity/overall及WB70实际恢复/normalExit/cleanup false均保持，宿主/OS/安装/AOT/硬件/长期/发布分层。