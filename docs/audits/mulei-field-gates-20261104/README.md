# 木垒 ARM64：SonnetDB 真机功能验收收口

日期：2026-11-04（Asia/Shanghai）。现场只读检查分为11:28～11:38及14:25～14:35两个窗口，SDK原生ARM64验证在14:31完成。

**结果：6组功能子项通过，1组对象列表分页达到既定8秒门限而未通过；共32条核验断言PASS、1条FAIL。** 原始JSON、SQL指纹、NDJSON终帧、实际执行路径、客户端程序集身份及资源回收见 [机器结果](verification.json)。这些是本轮核验断言，不是重新执行完整单元测试的数量。

按用户本轮确认，ARM64真实结果作为以下托管功能正确性的真机依据，**不再以同功能尚未重复执行x64为阻塞条件**。本报告不把未执行的x64专项容量、NativeAOT、原生依赖或硬件性能数字写成实测通过。

## 已完成的功能子项

| SonnetDB原任务 | 本次通过范围 | 关键证据 |
| --- | --- | --- |
| M42 / SQL-002 显式结果预览 | 6条固定真实回执默认完整返回；显式3行时truncated=true；SQL LIMIT 3与预览3行同时使用时truncated=false | [REST结果](preview-paging.json)中的前三项检查全部通过，均有合法meta/rows/end |
| M42 覆盖索引读取 | 实际走secondary_index_only；加入未覆盖FlowId后按合同回表，两条查询14行投影结果一致 | [真实执行计划](coverage-minimal.json)：实际检查/返回均14，索引IX_DeliveryTasks_BusinessId，完整45行EXPLAIN终帧 |
| M41 #381 真实DESC/OFFSET读取 | 固定12个记录身份，前后CreatedAt一致；7组相同时间的主键降序正确；OFFSET 6的6行等于完整集合后半页 | [固定身份分页](frozen-paging-verified.json)，没有依赖同步中Status分区保持不变 |
| M36 #314/#315 时序读取 | 真实device_status的18项schema、已结束分钟读取及22点计数；同一22条通过实际ADO在REST/Frame之间对拍 | [REST时序](timeseries.json)、[ARM64 SDK](sdk-run-verified.json) |
| M36/M42 远程ADO完整结果与预取消 | 生产兼容Data程序集在ARM64上分别强制REST/HTTP2 Frame完整读取6条关系记录和22条时序记录，结果一致；两个协议的调用方预取消和后续查询均通过 | [ARM64 SDK的11项检查](sdk-run-verified.json)，Frame使用5081的HTTP2端点，未走auto协议 |
| M36 #311/#323 对象读取 | 真实13,250,932字节PNG的SDK/HTTP完整读取和内容寻址SHA一致；首64字节、中间128字节、末尾32字节的206及Content-Range/ETag对拍；越界416、If-None-Match 304和错误If-Match 412均通过 | [对象HTTP检查](objects-verified.json)的前8项、SDK对象完整SHA检查 |

只投影索引覆盖列的查询为：

```sql
SELECT BusinessId
FROM DeliveryTasks
WHERE BusinessId = '6500261E142026110405000019'
LIMIT 16
```

实际执行路径是`secondary_index_only`。再投影`FlowId`时为`secondary_index`，两次均返回14条，actual_examined_rows/actual_rows=14/14。单次耗时受当时后台负载影响，本报告不计算稳定加速倍数或P95。

## 未通过及需要继续验证的内容

**对象列表分页为FAIL_TIMEOUT。** 对现存`vehicle-recognition`桶、前缀`flows/2026/11/04/`请求`max-keys=3`，两次首请求达到8秒预算；第二次探针已保存之前8个对象读取检查的通过结果。没有取得有效第一页，不能确认游标、第二页、重复或漏项；也不能把直接对象GET的通过扩展为分页通过。下一步应定位现存大元数据下列表前缀定位、候选扫描和首次响应耗时，不能仅放宽超时。

以下条件继续按原合同验收：

- M36九模型的完整真实写入、权限、删除、生命周期、重开和故障恢复旅程。当前关系、时序及对象读取的通过不覆盖未测试模型。
- M41 #381完整五类冻结语料和旧现网查询指纹。固定12个ID的结果集用于排序对账，不是整个数据库快照。
- M42执行器整体内存、首行、复杂阻塞查询、查询执行中取消、断连压力及资源释放。预取消只验证调用前取消和连接继续可用。
- 对象大容量、multipart、上传、校验恢复、断点续传、并发高变更分页、CLI及跨进程恢复。13.25MB对象读取不构成全容量或进程内存上界证明。
- 实例MQ snapshot/restore、整站独立对象正文备份及逐模型数据对拍。当前MQ只核准真实offset持续推进。
- 新候选完整168小时。14:35数据库实例运行约10.44小时；原采样计数9,337跨越旧版本和故障恢复，不能合并为当前候选七天。
- 真正NativeAOT目标启动和平台原生依赖。本次服务和探针以`dotnet *.dll`运行，没有以RID或架构名代替AOT执行证据。
- 新M49独立Protocol/Client包和发布消费者合同。本次Data是现场兼容程序集，不是新独立Client包。

因此，**以上6组功能子项可以收口；M36、M41、M42整个里程碑仍未完成，剩余原因是上述实际功能、容量或长期证据缺口。**

## 版本、资源与失败记录

服务端为现场基线`0a5acf0d`加本次内存/生命周期补丁，镜像为`mulei-arm64-20261104-memory-notification-r1@sha256:6167357de671da4fe44a00b6f7ac1fd3e857a694da0b436d4706aff5ffc0afee`。主线优化工作树仍基于`78c2c746`，没有直接部署该整份主线。

SDK验证引用当前业务实际部署的兼容Data程序集，运行与部署SHA均为`b92bc02541405b58aff4d1859c25170e75f1bf7fd6ce5ed8009b7aaa4c14a86b`。其源码基线为父仓固定`2fb18c78`加兼容补丁。REST与Frame是实际远程服务调用，没有用替身返回值代替。

探针仅在独占ARM64容器中执行，使用2CPU/1GiB隔离额度；生产128GiB/GC8未改变。探针只发送SELECT和对象读取，没有执行生产DDL/DML、抓拍触发、MQ写入或告警发送。隔离额度用于控制本次验证资源，不是生产内存优化措施。

14:35最终检查：原数据库、业务、发送器容器ID保持，均healthy、restart=0、OOMKilled=false。原图/门架/心跳消费者offset相对11:28继续推进，当时lag=93/3/0；这一快照不是积压清零或长期SLA结论。验证容器`115d100c6dc5...`已按任务label、ID和挂载核准后删除，临时凭据与远端探针目录已删除；最终再次核对同label容器为空，见 [现场与资源最终回读](final-status.json)。

本轮保留了以下失败与边界，没有改写为成功：

1. 首次索引目录探针用了`SHOW INDEXES FROM`，当前语法要求`ON`；属于探针语法修正。中文经控制台代码页的首轮输出也保留为无效证据，后续统一ASCII JSON转义，严格解析后才归档。
2. 未冻结Status分区的OFFSET首轮对账不一致；改成固定身份并前后核对后通过。原比较失败仍保留，不据此断言引擎有分页错误。
3. 最初覆盖查询投影了未被显式索引覆盖的`Id`，执行器按当前合同回表。核对源码后使用显式覆盖列形状，取得真实index-only结果。隐含主键后缀不自动视为可投影的覆盖列。
4. 第一次对象探针在列表超时后未导出前序检查；补齐异常收束后取得前8项PASS和分页FAIL_TIMEOUT，未修改服务端实现或扩大超时。
5. SDK初始前置检查错误地要求两个容器只共享一张网络，现场实际共享AI和业务两张网络；当时尚未创建验证资源。读取实际拓扑后绑定`tolnsd_tolnsd-network`，最终运行通过。

归档目录中的8份原始JSON均在 [verification.json](verification.json)记录SHA-256。探针源码、可重跑命令、程序集包及执行回执保留在父项目`.runs/sonnetdb-field-gates-20261104/`。源码修复仍未提交/推送；本轮只新增验收文档、证据并更新路线图、交接，没有改变业务实现。
