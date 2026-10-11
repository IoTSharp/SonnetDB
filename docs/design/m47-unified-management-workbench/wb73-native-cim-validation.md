# WB73 Native CIM 本地验收与集成

日期：2026-10-08。本片验收 WB72 已实现的观测，未新增产品功能。新冻结窗口为 02:35～03:25Z；会话已有 WB72 失败关闭 1/5，WB73 闭合后另计一次。0 Native actual、0 产品构建；本地夹具不能证明真实慢查询原因、数据库恢复或三宿主完成。

## 实现与源码绑定

仅对原 snapshot CIM 查询记录 self-handshake、parent-chain、seed-lookup、child-enumeration 四阶段，最多 160 槽、lookup 结果 0..1、children 0..64。保留原 Stopwatch 相对时间，超过 20s 不截断；累计观察记账达到 100ms 后停止，排除查询等待。Node 在同一 success/error envelope 上读取 own data descriptor 并安全投影；原查询/cache/primary error、预算、权限、运行时选择与 close/kill 行为保持。协议、产品 JSON/AOT 边界未修改。

最终六源 SHA256：

| 文件 | SHA256 |
| --- | --- |
| studio-native-process.ps1 | 46A4BA89B9C0D5B63B19364EDF36F8810526E4C19F617831EF6A296DE68BA705 |
| studio-native-evidence.mjs | FE363018A89F89F28CED7C67025F20F1DFFDEEC9C72E30264BD0E7CCDA0EAE70 |
| run-studio-native-real.mjs | 5358D2F61BD467EA6481CC16BFDF484F294D72F98081D35E0D34CFF5E215EBB2 |
| studio-native-helper-statistics.test.mjs | 649A0A9761DF555D3D4FD512B99D5137D8505CE556AA8C35357AE06A5FD74FB5 |
| studio-native-cim-observation.test.mjs | EED13A3BCE0C15349C053A0BB78E581765C0475D1097709F854C18E4E19606C3 |
| studio-native-cim-observation.fixture.ps1 | 2D6208104DB302209D87FEC3FDDBB9721233ACF7F3EA40BE3DAB7DEB20C89DBA |

## 本地验证与保留失败

新 PS micro 真正执行 self/query1，1/1；Node micro 1/1。第一次 PS full 退出 1，在前三项通过后被 child-cache 断言拒绝。fake missing CIM row 的 `return $null` 经数组包装生成一项 null；真实无匹配 CIM 返回零对象。专属 owner 仅将该 fake 分支改为 bare return，并说明语义；所有断言及其他五个源不变。独立逆向字节哈希与 AST0 复核通过，原失败和原 fixture 哈希保留，不倒写成功。

修复后 PS full-v2 **14/14**：self、null cache、parent boundary、child/BFS reuse、原异常、起止时钟、追加前缀失败、观察异常、记账停止、查询返回后的预算拒绝、160 查询边界、结果上限和 close/kill 排除。执行 case 名称、查询计数与 summary 逐项验收；live CIM0、childLaunches0。Node full **41/41**，fail/cancelled/skipped/todo 均 0，包含新观察 10 项、既有统计 15 项及 evidence 16 项。两项 full 的保留句柄分别退出 0，wrapper cleanupFailures0。源测试已用 5/5，不追加重试。

证据根为 `artifacts/wb73-native-cim-validation-20261008`：task-contract、acceptance-review、tools-review、source-repair-review、acceptance-results、原 ps-full 与修复 ps-full-v2、node-full stdout/retained-exit/wrapper-cleanup 分开保存。工具最初 ancestry race 的拒绝与修复记录亦保留。

## 集成与边界

根串行维护共享文档和最终树。HANDOFF 工作文件的 352979B foreign 前缀保持逐字节不变；并发追加的其他会话 2072B 微博交接也完整保留且不纳入暂存。首次 prepare 被长度 CAS 拒绝，0 工作文件写入；重新绑定准确的新前缀，追加时持有文件写锁并核全字节 CAS。只把已证明归属的 WB71 staged307508B、WB71 ending3963B、WB72 ending3150B 和本片 owned delta 拼成私有候选，再以 blob 精确 stage，禁止整份 add。其他十三路径仅纳入已证明归属的修改，新旧失败报告均保留。

最终树/hash、owned index、完整 `dotnet restore SonnetDB.slnx`、原 CI `dotnet format SonnetDB.slnx --verify-no-changes --no-restore --severity warn --exclude extensions/`、cached diff check、提交与 post binding 的实际结果以本证据根的收据为准；此处不预填尚未执行的 PASS。仅在两个完整门禁、退出审计、独立复核通过且冻结内容未变时本地提交，不 push。`origin/parity-results` 保持 0061d6d78591fb08493f473d3231ca42303faae1。

退出审计只覆盖已观察完整 tuple 与保留 child handle；复用 PID/未知/shared 身份保留且不授 stop 权限。启动/短命 shell、瞬时未观察后代、ReadToEndAsync 预 cap 内存和审计器自身退出仍有边界，不宣称 whole-session orphan-freedom 或 processIntegrity。WB72 过期、WB71 strict numeric 复用失败及 WB70 actual/normalExit/cleanup false 不变。

下一独立窗口才可冻结基于新观测的真实 Native 准入/单次旅程；先独立审核工具/源码再核 fresh 资源，不能盲重跑旧 WB70。真实 Server、三宿主、OS 文件/Managed Local、安装、Extension Host、AOT、固定硬件、长期与发布证据分别验收，Graph Beta。
