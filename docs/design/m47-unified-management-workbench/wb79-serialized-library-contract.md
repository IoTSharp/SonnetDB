# WB79 真实落盘夹具与派生字段合同

2026-10-09，从本地 `11beed0e` 接续。本片的实现、测试、包装器修复、独立复核及本地集成合计本会话第3个独立任务，收尾后3/5。证据位于 `artifacts/wb79-serialized-library-contract-20261009`；合同限制8个受管命令、3次Node测试命令、最多2次定向C#测试构建，截止06:43Z，0 Native actual。两位专属实现代理分别拥有C#与JS路径，第三位只读复核；根串行维护共享文档、运行验证和提交。

## 接受合同

`StudioConnectionLibrary.SaveAsync` 使用原有 source-generated context 保存规范化快照。新增测试真正调用保存方法，读取实际文件，以完整JSON结构和值与 `web/e2e/fixtures/studio-managed-local-library.json` 比较。Node直接消费同一文件，C#项目把它链接到测试输出，避免两份独立样本。确定性样本含root四字段、profile九字段及两个各四字段的派生identity，保留混合大小写数据库名和固定正数时间戳。测试限定文件4KiB、解析深度8和10秒取消；沿用原有归属检查及临时目录释放。

磁盘投影接受两种形态：两个派生字段均缺省的旧格式，或 `activeIdentity` 与唯一profile的 `identity` 同时完整存在。后一形态必须只有host/profileId/baseUrl/database四字段，host严格为studio-desktop，profile身份逐值匹配其id/baseUrl/defaultDatabase，active身份匹配选中profile及activeDatabase，大小写精确。半对、null、错类型、缺字段、多字段或值不一致全部拒绝。未知root/profile字段继续拒绝。

投影输出仍不含派生身份。磁盘内容不能充当native bridge acknowledgement；原最终快照校验仍要求选中库与profile默认库一致。原bridge、fresh请求/响应barrier、A/B顺序、两次desktop启动、第二次被动恢复、B查询和正常关闭/进程合同未放宽。观察器仍只记录固定字段类别与未知数量，不保存身份值或未知名字。生产C#序列化、runner、进程/evidence实现及观察器共六个源文件与HEAD一致，见 `protected-source-invariants.json`。

## 已取得的验证

- 共享fixture微试1/1通过。真实C#连接库定向20/20通过，包含1项新增落盘比较；命令exit0，TRX保留于 `csharp-tests/wb79-studio-library.trx`。这一次dotnet test实际构建了Studio、Server及引用项目，不能称0产品build，也不是启动Server或Native桌面。
- 最终Node为场景37、观察15、precondition11及preparation17，共80/80，0fail/skip/cancel。新增6项测试覆盖共享夹具、28个拒绝变体、身份绑定差异和disk不能替代native ack；旧旅程断言保留，旧缺省格式另有投影等价检查。
- 专属只读复核见 `independent-review.md`，精确源码SHA见 `source-review-hashes.json`。复核是静态证据，不能替代根的动态测试。

## 原失败与包装器边界

C#包装器因 `Complete process identity required` 留下passed=false与cleanup-error，虽然测试命令exit0。原收据没有指明缺失tuple的PID，不能断言是已退出的瞬态进程。独立fresh核验只证明12个已记录完整身份当时均不存在，不能证明未记录后代或全会话进程树清零。

首次完整Node同样被包装器中止，exit-1；其3个完整身份经核实终止、2个已不存在，原失败日志和收据保留，不能计为完整测试成功。随后fresh核验这5个已记录身份均不存在。原 `run-command.ps1` 字节保留；新v2仅在不完整缓存行上作一次fresh按PID查询，确认旧身份不存在或创建时间不同才跳过，同创建仍不完整继续失败。每个Observe快照以PID+创建时间缓存结果，包括null，最多128条观察事件，清理不复用该缓存作为终止授权。原失败原因不被后来成功追认。

独审还修正了task-local包装器的启动后根tuple登记失败记账及清理链跳数边界。v2微输入通过，预算内唯一完整Node重跑通过，其6个已记录身份全部自然退出；该轮没有触发fresh缺字段分支，因此不把这次成功当作异常分支动态覆盖。所有旧失败收据保持原值。

## 本地集成与下一步

根仅集成12个自有路径。HANDOFF提交内容采用HEAD blob加本片明确自有段，工作文件的全部外来字节及后续追加保持；博客、CSDN、OSChina和微博工作不纳入提交。最终树必须运行完整 `dotnet restore SonnetDB.slnx` 及原CI命令 `dotnet format SonnetDB.slnx --verify-no-changes --no-restore --severity warn --exclude extensions/`。实际结果分别保存于 `restore-final.result.json`、`format-final.result.json`，精确提交及本会话3/5记账见 `commit-checkpoint.json`；不存在收据时不得预填通过。最终新鲜已记录身份审计与原失败逐一关联，继续保留wholeSessionOrphanFreedomProved=false。

下一独立任务可按此已验证的落盘接受合同重新冻结Native源码、依赖、实际runtime、资源和预算，取得普通A/B、双desktop、B查询、正常退出及恢复证据。WB77的实际被拒字段未保留，原唯一actual已耗尽且失败，不能盲重跑或升级结果。本片不证明原失败原因、真实桌面恢复或source-to-binary等价。真实Server、三宿主、文件对话框、安装、Extension Host、AOT、固定硬件、长期和发布验收继续分别记录。Graph Beta、MQ database身份及instance `.system/mq`边界不变；无push、部署、安装、发布或外部发送。
