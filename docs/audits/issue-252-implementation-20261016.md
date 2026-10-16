# Issue #252：原生 TimeSpan / DURATION 实施记录

本项补齐 Chloe 原生时长合同。`TIME` 继续表示一天内的 `TimeOnly`；新增
`DURATION` 保存完整有符号 Int64 ticks，支持 100ns、负数、超过 24 小时、边界和 NULL。
正式 SonnetDB 包、对应 Server 及 Chloe 上游交付尚未完成，Issue 保持 OPEN。

## 交付与复审

- 原 Issue 描述和评论已通过 GitHub API 实际读取；没有把已有 TIME 当作时长支持。
- 代理在 `issue252-timespan` 工作树交付；基础提交为 `f8bce983`，另接收 #259/#256
  冻结生产依赖。交付清单 SHA256 为
  `521BC1F4DEE62C151CCABB2F89D709E4FC482015561FD3124505B5310D297F70`。
- 根在 `codex/issue-supervision-batch05` 的 `390af19d` 上核对全部 70 份源码、341 份
  证据和 31 个程序集哈希，以及四份 TRX。只集成清单指定的 54 份文件，16 份未变依赖
  不覆盖；6 份叠加依赖保留 #259/#256 实现。没有整树覆盖或删除新主线文件。
- 原清单的 `integrationRoot` 指向尚停留在 `f9ee14b7` 的主目录。根实际在独立监督树
  逐文件核对所有前置哈希，全部匹配；实际目录与基线另录于根 integration.json。
  主目录其他会话的文档、旧树和失败证据均未修改。
- 根审查了序列/过程副作用前的旧协议拒绝、显式 `DbType.Time`、迁移持久顺序、
  schema 格式下限、帧和 REST 协商、source-generated JSON 以及 Chloe 五文件候选。

## 合同与兼容边界

`CAST(integer AS DURATION)` 按 ticks 解释；文本接受 invariant `c` 格式，浮点拒绝。
时长加减和取负越界报错。`DateTime - DateTime` 按已求值 CLR 钟面 ticks 返回时长；
既有 DATETIME 列和默认日期参数仍为 Unix 毫秒，本项没有解决 #236 的存储精度需求。

默认 `DbType.Object` + TimeSpan 使用完整时长；显式 `DbType.Time` 只接受一天内的值，
转换为 TimeOnly 后可访问旧 Server。reader 值和声明元数据均为 TimeSpan，包括空结果
和全 NULL。Chloe 普通参数和 InsertRange 两个入口必须一起设置 DbType.Object。

关系目录升级为 v14、类型码 10。涉及 Duration 的 ALTER 使用已有有界恢复日志，
顺序是永久 v14 屏障、pending 日志、raw/WAL、目录、完成决定、内存发布。失败、
恢复、移除列和 DROP 后不降级；旧引擎拒绝。包含时长声明/值的物化快照写 v3，
其他继续 v2，保留 v1/v2 读取。未改变 FileHeader 布局。

SQL 结果协议 v5 包含 v4 日期合同；Frame 声明版本 4、Duration 值 tag 11；REST
使用带类型标记的 Int64 ticks。客户端仅收到明确执行前 `unsupported_result_version`
才降级，不自动重发普通 SQL 错误或不确定传输。旧客户端对 Duration RETURNING
在写入前拒绝；带序列的查询/过程依赖树在首个序列副作用前检查输出，不能事先确定
输出类型时保守拒绝。其他过程结果在事务提交前检查。

## 验证证据

证据根为 `artifacts/issue-supervision-20261016/issue252-work/`；原始失败和修订结果
分别保存。摘要计数曾误写为 39 项定向及每路径 18 项重开，最终以 TRX/日志纠正如下，
不同筛选器重叠的计数不累加。

| 代理验证 | 实际结果 |
| --- | --- |
| 最终 Core SQL/协议/迁移扩大回归 | 3084/3084，零失败/跳过 |
| Duration/TIME/Frame/REST 解码定向 | 44/44，包含在扩大集合中 |
| 真实远程 ADO 整类 | 126/126，零失败/跳过 |
| 迁移真实进程强杀及恢复再次强杀 | 7/7，零失败/跳过 |
| 原始 Chloe 类型保护负基线 | 3 个预期拒绝断言 |
| Chloe 候选 embedded/REST/h2c | 每路径 26 个主流程 + 17 个独立重开断言 |
| 新客户端与真实旧 v4 Server 的 TIME 兼容 | 每协议 5+2，共 14 个断言 |
| 实际旧 v13 引擎对新目录的拒绝/新恢复/再次拒绝 | 6 阶段 × 3 次探针，共 18 次 |
| Core/Server AOT 分析、Data 显式 AOT/Trim 分析 | 实际构建 exit 0，零警告/错误 |

最终 Core 构建及回归使用冻结源码；旧 Server/强杀和代理 AOT 构建使用格式化前、
语义相同的源码。根在实际集成树另行构建并运行验证，结果见以下记录。
NativeAOT 二进制发布尚未执行，分析通过不等于原生发布通过。

实际旧 v13 Core DLL SHA256 为
`B838405885FC15F0874AAEAA2499CD4616D2B879251BC88E69684E24E505ADB6`。
pending/恢复再终止目录中旧引擎未改表文件字节或 mtime；completed 日志目录中旧引擎
先重复写相同完成标记再拒绝 v14，仅该日志 mtime 改变，raw/WAL/catalog 字节不变。
因此不能声称旧进程零文件写入；真实强杀也不能代替断电验收。

根在 `390af19d` 加本项交付的最终生产源码上取得以下独立结果：

- Core Release 构建零警告/错误；SQL/协议/迁移扩大回归 **3139/3139**，零失败/跳过。
- Server Release 构建零警告/错误；真实远程 ADO 整类 **141/141**，零失败/跳过。
- Data 显式启用 IsAotCompatible、EnableAotAnalyzer、EnableTrimAnalyzer 的构建实际
  exit 0，零警告/错误。该构建来自根最终源码，与代理格式化前分析证据分开记录。
- 交付 PowerShell 脚本语法和暂存差异检查通过。首次暂存检查发现 CHANGELOG 新增
  行为 CRLF，保留原件后只归一 LF；没有修改生产语义。外部 patch 使用局部
  `.gitattributes` 保持原字节。
- 根未重复真实 Chloe、旧引擎和强杀矩阵；逐文件核对其原日志、运行程序集和 TRX。
  上述根结果与代理集合重叠，不合计成不同用例数量。

根回执目录为监督树 `artifacts/issue-supervision-20261016/issue252/`；完整 solution
restore 和规定 Format Check 在 `root-gate/` 执行，提交者必须确认两条实际 exit 0
后才执行独立提交。此报告不把本地验证提升为远端 CI 或正式发行证据。

## 后续

源码候选冻结后把共享基线交给 #251；#250/#249 只做隔离准备。Chloe 补丁基于
`bb7ec8735bd4511c35453eb009f70cc606795703`，仅本地候选，不向外部仓库发送。
正式匹配 Core/Data/Server、Chloe 上游合入及正式发行仍需独立验收，不能据本地 PASS
关闭 #252。
