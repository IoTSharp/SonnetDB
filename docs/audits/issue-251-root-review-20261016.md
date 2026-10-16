# #251 主监督复审与集成

2026-10-16，监督树 `D:/source/SonnetDB-worktrees/issue-supervision-batch05` 在
`ea04b240` 上集成 #251；#252 的 `16b266c2`、已交付文档 merge `a3932c49` 和
独立递归取消测试修正均保留。未覆盖代理交付范围外的文档或生产文件。

## 输入与复审

冻结清单 SHA256 为 `613D27DDA235F29A8DCAEBB30C130B5FCE014C462421F601DD79E20D4A912A13`。
根逐项核对 71 个交付文件、302 份证据、23 个运行程序集、12 份 TRX 及 Chloe
最终树 1210 个跟踪文件，之后只复制清单中的 71 文件。清单 `baseline252Sha256`
仅针对 #252 修改过的路径；其它既有路径另核与 `16b266c2` 无差异，不把 null 误当新文件。

复审覆盖：名称绑定后的类型预检查、空表/短路/索引/连接拒绝、UTC ticks 的主键/索引/
排序/分组/MIN/MAX、v15 永久屏障与恢复、快照声明预读与正式 CRC 检查、结果 v6
协商及旧端副作用拒绝、显式旧 DbType 的客户端 UTC 归一。Core 未增加依赖或 unsafe，
REST envelope 使用手写 JSON，不添加反射序列化入口。

Chloe 最终候选基于 `bb7ec8735bd4511c35453eb009f70cc606795703`，保留 #262、#252、#251
三组补丁，未修改默认 DateTimeOffset converter。代理的真实消费、旧 Server/旧 Core
和 14 个强杀测试已通过哈希与原始记录核对，未由根重复计为新执行。

## 根实际执行

| 验证 | 结果 |
| --- | --- |
| Core SQL/表/协议/参数/ADO 扩大回归 | 3768/3768，零失败、零跳过 |
| 真实远程 ADO 整类 | 147/147，零失败、零跳过 |
| Core Release 构建 | 零警告、零错误 |
| Server/远程测试 Release 最终构建 | 零警告、零错误 |
| Data 显式 AOT/Trim 分析 | 零警告、零错误 |

运行使用独占短 TEMP 与 D 盘产物。原始命令、退出码、TRX、运行程序集与源码 SHA
见同名 JSON 及 `artifacts/issue-supervision-20261016/issue251/`。
Core 新计数包含独立取消测试两输入，不能用它改写代理原 3765/3766 或 174/175 失败。

首次远程 restore 因共用 NuGet HTTP 审计缓存文件锁失败，原日志完整保留；第二次
使用独立缓存后构建和测试通过，但两个扩展项目的 assets 保留旧 NU1900 警告。
再以独立新 HTTP 缓存执行 force-evaluate restore 并构建，最终零警告、零错误，
没有关闭漏洞审计或压制诊断。最初根脚本参数插值错误发生在启动产品命令前，已修正。

根格式整理仅改变四个文件的空白/行尾，逐文件 `git diff --no-index --ignore-all-space
--ignore-blank-lines` 确认无其它变化；测试对应冻结生产语义及已提交的取消修正。
原冻结文件和运行结果保持，格式差异与最终源码哈希另列，不伪称代理原 DLL 是根重建产物。
暂存检查另发现代理审计 JSON 的 CRLF 和探针脚本尾部空行，两个文件只归一行尾/EOF；
源快照保持，原 JSON 哈希与仓库规范化副本哈希分别记录。
首次完整门禁 restore 成功，Format 因新测试中两个既有 using 的顺序返回 2；
仅交换 Backup/Data 导入顺序，原失败留在 `root-gate/`，最终完整门禁另存 `root-gate02/`。

## 提交与交接边界

提交前必须在最终树完整通过 `dotnet restore SonnetDB.slnx` 和规定的 Format Check；
门禁、最终 tree 与实际提交回读分别写入 `root-gate02/` 和 `commit.json`。
本报告随独立 #251 提交，提交后再将最终 SHA 交给原 #250 代理。

本批 #252/#251/#250 齐后才普通推送、核远端 main/parity SHA 和精确 CI，再回复 Issue。
当前没有发布正式包、向 Chloe 外发补丁、执行 NativeAOT 发布运行、断电或跨平台部署。
#251 保持 OPEN，等待正式 SonnetDB 与 Chloe 交付；#226/#243 已推送的源码补缺不重做。
