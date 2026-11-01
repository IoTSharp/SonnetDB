# #230：5.0 发行及 FreeSql 消费者核验

核验日期：2026-11-02（北京时间）。结论：**候选实现和验收通过，正式交付仍受阻，#230 保持开放**。机器收据见[同名 JSON](issue-230-release-20261102.json)。本报告不重新定义产品范围，也不把源码测试当作公开包交付。

## 已完成的工作

发行版本固定为 **5.0.0**，tag `v5.0.0` 指向 `455bf3b53b1e56df0064d8f574b0595cbb0b609e`。该源码已随 #385 合入 main；#386 修复 Linux 测试宿主 inotify 耗尽，Ubuntu 完整套件 10243/10243 通过。#380 EXISTS/NULL 索引访问路径修复已包含在同一源码内。

现行十二类发行门禁已对同一提交及精确 5.0.0 通过，包含完整 CI、三平台 AOT、Format、CodeQL、light/full Parity 原始证据及实际包/安装合同。七天 scheduled Parity 为 6/7，按现行政策是非阻断观察项；不能写成七天长稳通过，也不能自行再追加七天等待条件。完整工作流 ID 见 JSON。

十个候选 NuGet 包来自 [Publish 预检 41346120155](https://github.com/IoTSharp/SonnetDB/actions/runs/41346120155)，已核对版本、nuspec 源码和包 SHA。公开 4.0 数据的独立工作副本通过 5.0 读取、事务 DDL/双表写入、两次重开；旧版拒绝新 floor 后，从未改变的停写离线备份恢复，4.0 再次两次重开通过。

## FreeSql 的实际适配与消费者结果

[FreeSql #2234](https://github.com/dotnetcore/FreeSql/pull/2234) 已合并，提交 `05997ef16f0d6a12f051f9a50794a2c21dcd4dc5`。现有后续 [#2291](https://github.com/dotnetcore/FreeSql/pull/2291) 更新到 `a2268c1ff`，包含 5.0.0 依赖、按列绑定时间参数、无参数原生时间 CAST 和结构化视图去重，FreeSql 核心零修改。真实 UPSERT 消费者暴露的时间字面量遗漏已修复，新增四项回归。

| 实际执行 | 结果 |
| --- | --- |
| 完整 Provider 套件，精确 5.0.0 候选依赖 | 237/237 PASS，0 FAIL，0 SKIP |
| 候选 Core/Provider NuGet，新缓存 restore/build | PASS，无源码 ProjectReference |
| embedded、REST、frame-http2 业务旅程 | 各25项 PASS |
| embedded、REST、frame-http2 独立重开 | 各8项 PASS，Server 进程实际终止/重启 |
| NativeAOT Server 实际协议收据 | REST SQL68、HTTP/2 Frame32、HTTP/2事务21 |
| Provider Release pack / FreeSql Core 五目标 pack | PASS，无原预发布依赖 NU5104 |

旅程覆盖关键 CRUD、RETURNING、UPSERT、DECIMAL、100ns 时间、双事务竞争锁、DDL 提交/回滚、受支持的 STRING→INT 主键迁移、视图去重、列元数据、存储过程输出/多结果、空首结果和 measurement 分组/HAVING/排序/分页同步及异步。完整套件含嵌入式执行和 SQL 生成断言，不将全部237项称为三协议测试。

程序、生命周期脚本和运行说明已提交到 [FreeSql 验收目录](https://github.com/IoTSharp/FreeSql/tree/a2268c1ff/FreeSql.Tests/FreeSql.Tests.Provider.SonnetDB.ReleaseAcceptance)。阶段报告绑定实际加载程序集版本/SHA，根报告绑定 Server/消费者 SHA 和协议收据。DATETIMEOFFSET 按已有合同返回完整 UTC ticks 与零 offset；初次夹具中错误的保留输入 offset 预期已修正，没有改变数据库语义。主键迁移夹具改用已支持的 STRING→INT，没有扩大任意类型转换合同。

## 正式发布失败的准确位置

[正式 Publish 41348714535](https://github.com/IoTSharp/SonnetDB/actions/runs/41348714535) 的 NuGet 构建、Linux/Windows bundles、安装器及真实 Server 合同均成功。2026-11-02 05:08:05（北京时间），首个 SonnetDB.5.0.0.nupkg 推送被 NuGet 拒绝：**HTTP 403，API Key 已过期**。因此本轮没有成功推送十个公开 NuGet 包，GitHub Release job 跳过，正式公开包/Server 消费者仍为 NOT_RUN。

[Connectors 41348714371](https://github.com/IoTSharp/SonnetDB/actions/runs/41348714371) 的 win-x64/linux-x64/win-x86 构建均成功，资产上传因前置 Publish 失败而失败。[Docker 41348714479](https://github.com/IoTSharp/SonnetDB/actions/runs/41348714479) 已成功发布，属于已经发生的部分分发；它不能证明整个版本交付完成。

需要维护者更新 SonnetDB 仓库的 `NUGET_API_KEY`。不移动或重建现有 tag，不绕过合同；更新后重跑原 Publish 的失败 jobs，再恢复 Connectors 上传。正式成功后使用已准备的5.0发行说明替换泛用回退说明，下载并核对公开十包及实际 Server，使用全新缓存重跑 Provider237项与三协议旅程。公开 NuGet 可能附加仓库签名，应分别记录包SHA，核对源码和 DLL 内容，不能要求签名后包与候选整包字节相同。

## 公开 FreeSql 组合仍不可安装

本轮公开 FreeSql Core 最新稳定 `3.5.311` 未包含 `DataType.SonnetDB`；`FreeSql.Provider.SonnetDB` 的公开 NuGet 索引返回404。已保留独立失败探针：候选 Provider 包加公开 Core 的 restore 成功，build 明确报 **CS0117**。这不是候选 Core/Provider 本地包三协议 PASS 能消除的交付问题。

当前账号对 dotnetcore/FreeSql 仅 READ，无权合并或发布。需要上游合入 #2291，并发布包含已合入 #2234 的新 Core 及对应 Provider。不得把本地同版本候选包冒充 NuGet.org 包。

## #230 关闭条件与证据边界

1. 更新 NuGet 密钥，从原 tag 恢复正式发布，核对十包、发行说明及完整资产。
2. 用全新缓存、公开 SonnetDB5.0与匹配正式Server，完成完整Provider和三协议消费者复验。
3. FreeSql 上游合入并发布新 Core/Provider。
4. 用公开 FreeSql/SonnetDB 组合重复消费者验收，通过后关闭 #230。

5.0 的 Protocol 拆分和分页 long API 属于主版本兼容变化，应用和 Provider 必须统一升级并重新编译；数据库回退需恢复升级前备份，详见[迁移合同](../releases/5.0-migration.md)。本报告不证明物理掉电、固定硬件容量、干净机器安装、全部模型/Server MQ 恢复或生产负载，也不替其它 ORM 的适配与上游发包结项。

原始 TRX、日志和探针位于 ignored `artifacts/release-followup-20261102/`；不提交编译产物或明文凭据。#230正文与#2291描述已更新为上述实际状态。
