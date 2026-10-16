# #253：无主键关系表的真实 Chloe 消费验收

日期：2026-10-16（Asia/Shanghai）。#243 已交付的无主键引擎路径通过本轮实际 Chloe 候选验证；两个固定公开版本仍拒绝写入，Chloe 上游也未交付候选。因此本项为**源码已具备能力、等待发布及上游交付，Issue 保持 OPEN**。

本轮只新增工具、候选补丁与报告，没有修改引擎生产代码或原测试源码。工作树 `D:/source/SonnetDB-worktrees/issue253-keyless-chloe`，分支 `codex/issue253-keyless-chloe`，基线 `f8bce9839e7344bd8a8095d14979b0ee24bc574b`。没有重复实现 #243，也没有修改 #256 独占的分页/共享执行器文件；#255 的 XCode 消费验收独立进行。

## 在线合同与固定来源

本轮实时回读 #253：OPEN、无评论；原要求是无显式主键的 INSERT、WHERE UPDATE/DELETE、重复相同行、NULL、参数/批量、RETURNING 边界、事务/回滚/重开及嵌入式/远程一致，不允许 provider 添加用户模型不存在的业务主键。Chloe PR435 回读仍 OPEN，head `bb7ec8735bd4511c35453eb009f70cc606795703`。

公开包都直接从 NuGet.org 固定还原，资产文件没有 SonnetDB ProjectReference：

- `4.0.0`：nuspec / DLL revision `d49babae0e049ca4554d26772b92493220d42822`。
- `4.5.0-preview.ef266.5`：revision `1af23d8f299febfdf66b040ab3419980396acf46`。
- Chloe 原始归档 SHA256 `FDD72DD90965C6271F49C15428AECE05F2EC383235C43D4CCE0975D1D8FD9E87`；本轮 1210 个归档文件逐个比较，候选只有两个目标文件变化。

Chloe 候选删除两个无主键保护条件，共四行：`SonnetDBContextProvider.EnsureInsertMappingSupported` 和 `SonnetDBTableGenerator.GenCreateTableScript`。没有修改公共 Chloe 核心、SQL生成器、参数规范化、迁移器或其它 Issue 的保护。补丁在独立 Git 目录实际 `apply --check` / `apply`，应用后源码 SHA 与编译候选一致。

## 本轮执行结果

| 验证层 | embedded | REST | h2c |
|---|---:|---:|---:|
| 原生正式包 4.0.0 | 3 PASS / 4 FAIL | 未测发布 Server | 未测发布 Server |
| 原生公开预览 .ef266.5 | 3 PASS / 4 FAIL | 未测发布 Server | 未测发布 Server |
| 同原生消费者，当前源码 Core/Data | 7/7 | 由下列 Chloe 场景覆盖 | 由下列 Chloe 场景覆盖 |
| 原 Chloe provider，当前源码引擎 | 9 PASS / 11 guard FAIL | 9 PASS / 11 guard FAIL | 9 PASS / 11 guard FAIL |
| Chloe 最小候选，当前源码引擎 | 20/20 | 20/20 | 20/20 |
| 候选独立进程重开后继续操作 | 5/5 | 5/5 | 5/5 |

候选主流程 60 项与重开 15 项合计 75 项场景通过。原 provider 的 33 次 guard 拒绝保留为负向证据，不写为功能通过；三个公共控制项和四个写入失败也分开记录。源码定向 xUnit 为 **Core 25/25、真实传输 6/6**，零失败/跳过，与消费者及历史 #243 覆盖重叠，不累加为唯一测试总数。

公开包的建表、空表查询和透明元数据通过，单条 INSERT、参数批量重复/NULL INSERT、INSERT RETURNING、事务 INSERT 均抛 `TableConstraintException`，要求先添加 PRIMARY KEY。这精确重现原问题：可建表不等于可写表。同原生程序换入本轮源码 Core/Data 后 7/7。

原/候选 Chloe 使用同一消费者 DLL、同一 Chloe 核心 DLL、同一源码 Core/Data；只替换 provider DLL。源码运行目录从公开包编译输出复制后，明确替换为工作树源码 DLL，日志逐份打印真实版本、SHA 与路径；这些正向结果**不能标成公开包通过**。真实远程使用源码 Server，未核验任何发布版 Server。

候选具体覆盖：

- 无主键建表生成器只生成声明列；实体和表达式 INSERT、多个批次及异步路径保留相同重复行和 NULL，中文/引号参数原样物化。表达式 INSERT 返回 null identity，没有伪造主键。
- WHERE UPDATE/DELETE 根据谓词作用于全部匹配重复行及 NULL；已有只读视图的无主键查询在原 provider 与候选均可用。实体对象 UPDATE/DELETE 仍明确要求主键。
- 一个 InsertRange 跨多个批次，后批 CHECK 失败时前批也回滚；事务内插入/更新/删除的提交、回滚结果正确。无主键 ON CONFLICT 仍明确拒绝。
- Chloe Session 的原生 SQL 验证 INSERT / UPDATE / DELETE `RETURNING *` 的数量和值；只返回声明列。这是 SQL 边界验证，没有新增 ORM RETURNING API。
- `SELECT *`、空 reader 元数据、`GetSchema("Columns")` 和 information_schema 只呈现 Message / Value / Category，主键标记为 false；内部稳定行身份没有出现在 SQL 模型中。

每次 reopen 都是新的消费者进程。候选远程先完成所有请求，再结束原 Server，使用同一数据目录和新 PID 启动，之后验证重复行/NULL 与回滚结果、再次 WHERE UPDATE/DELETE 及元数据。实际 HTTP/1.1 SQL 与 HTTP/2 `/v1/frame` 日志在写入和重开两个阶段都存在。这个补验不是 journal 各阶段的故障注入，也不替代 #243 原来的 crash 恢复测试。

## 构建、原始失败及交付边界

最终原生/Chloe 消费者、源码 Server、源码远程测试项目 Release 构建均 0 警告 / 0 错误；源码保留默认 AOT/trim 分析。外部 Chloe 原版与候选各有 38 个现存警告，未抑制，不能写作完整构建零警告。

准备阶段的三类失败均未覆盖：归档没有外层根目录，首次布局假设造成项目不存在；首次源码 Server 命令包装器在进程观测期间报一般错误，未取得构建 exit code，受控重试才成功；初版消费者三个 InsertRange 调用遗漏 table 参数造成编译失败，修正后才通过。它们均与功能负向基线分开记录。

原始产物位于 `D:/source/SonnetDB/artifacts/issue-supervision-20261016/issue253-work`；TEMP/数据目录为独占 `D:/sndb-t/i253-*`，未删除旧树或证据。机器报告 `issue-253-keyless-chloe-20261016.json` 验证 12 个消费者进程、14 个实际运行程序集、167 份证据文件、3 个已结束的自有 Server 会话、包资产和两份 TRX。工具在 `eng/probes/issue253-keyless-chloe/`。

JSON/Markdown 使用 LF、UTF-8 无 BOM；补丁按上游原字节保留 CRLF，局部 `.gitattributes` 禁止转换。临时 Git index 纳入全部新文件后检查，避免未跟踪文件漏检。完整 solution restore/Format Check、共享 HANDOFF/CHANGELOG、独立提交、批次推送及 Issue 回复由监督主智能体在最终集成树执行，本子任务未提交或外发。

下一步：根复核冻结清单并完成提交门禁 → 批次推送和 CI 回读 → 回复 #253 说明源码及候选证据、两个公开版本仍失败 → 在正式包/正式 Server 和 Chloe 上游交付后重跑真实消费者，再判断关闭。不得把本地候选当作已经合入或发布。
