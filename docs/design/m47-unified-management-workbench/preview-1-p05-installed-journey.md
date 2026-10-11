# M47-P05：实际 NativeAOT 包的首次使用与重开

2026-10-11（Asia/Shanghai）。**候选10在本机 Windows 11 x64 上的 R1 实物验收完成；产品仍 NOT_READY，P06～P08未执行。** 用户明确回复“继续完成 P05”后接续，本轮结果不改变此前的范围纠正记录。七模型专用读取及其它宿主继续延期；30秒/1000行/4 MiB/64 KiB预算保持。

| 身份 / 资产 | 实际回读 |
|---|---|
| 候选 | [不可变 candidate10](preview-1-candidate-10.json)，`4.5.0-preview.1.10` |
| 包来源 | `54eefc816f133a805b0d38f0cbe930d2c17650b6`；tree `0083fd496dbdac25019aee058651c821dac0c336`，两个 gitlink 固定在 manifest |
| CI | `38104431383` attempt1，整体 success；11项命令收据全部 PASS / cleanupComplete=true |
| NativeAOT | 实际 publish 283.47秒、28个观察身份、0 IL/AOT告警；PE无CLR目录，ProductVersion与完整SHA一致 |
| ZIP | 46,634,305 bytes；SHA256 `d01729b97a8db906ffb78bf88138cfa58d91b708efaf03fa408c0b59aaab51ed` |
| 独立库存 | ZIP / manifest / SHA256SUMS三资产，279项entry路径、大小与SHA逐项相符；编译Web与解压 `Start-Preview -VerifyOnly` 通过 |
| 实际环境 | Windows 11 家庭中文版 x64，build26300；Chrome `150.0.7871.115`、Edge `155.0.4283.45`，使用专属空白profile |
| 部署模式 | 解压NativeAOT Server＋同源内嵌Web，根路径 `/admin`，只监听 `127.0.0.1:5080` HTTP与`:5081` Frame，Production配置；先执行原包预检，再在独占控制台启动包内原生exe |

原始证据保存在 `artifacts/m47-p05-20261011`：`delivery-10`、`ci-evidence-10`、`delivery-validation-4.5.0-preview.1.10.json`、`suite-10-b/suite.json`及各phase目录；冻结记录绑定收据与工具SHA。包来自干净CI，未把共享dirty工作树、旧dist或本机build当发行物。

| 实物场景 | 结果与证据边界 |
|---|---|
| 首次设置、密码登录、About、导航及深链接 | Chrome首次setup；两浏览器均真实用户名/密码登录，About核完整版本/SHA/profile/R1，授权数据库可见、隐藏数据库不出现在选择器；七个全局导航中四个禁用，直接进入延期路由拒绝 |
| SQL / 关系表 / measurement | 两浏览器读取实际包Server的合成数据，表/列原始拼写保留；1001行查询截断为1000行，未加载的第1001行不进入导出；measurement读到真实准备的1点 |
| 当前窗口导出 | 两份实际浏览器下载各22,835 bytes，独立JSON解析各1000行，同SHA `2d0e59456294ca31db07780eb9d6eef59cb55a3337a5189ce3abc7a54f6e8af9`，不称全表导出 |
| 单行写审批 | 取消后独立核无行；确认一次，等待真实HTTP200与NDJSON `recordsAffected=1`，再以独立API核行；只读数据库grant的UI写入口禁用且实际写请求403 |
| 撤权、身份失效、断连 | 实际撤销grant/token后清除结果、草稿、审批与可导出载荷；浏览器offline/online不恢复旧写入；已撤token触发的导出产生0次下载 |
| 未知写入及取消读取 | 注入在真实Server提交1行之后丢失响应，两浏览器各仅1次写请求，独立核已提交，旧审批不重放；延迟真实读响应再取消，只证明客户端丢弃迟到载荷，不证明Server扫描停止 |
| Server/Web错配 | 真实旧Preview请求头返回428；身份响应版本错配注入在登录视图挂载前拒绝并清认证，0次认证API；候选09原生Server＋候选10Web使用单独新数据，两浏览器实际拒绝登录，旧Server未打开候选10数据 |
| 停止重开 | first、reopen、rollback、legacy四次原生Server均实际Ctrl+C退出0；正常停止完成后Job为空、强制终止身份数0。重开两浏览器密码登录并读回已确认行 |
| 隔离快照回退 | 正常停止后复制61个文件并逐项比SHA；重开添加独立“快照之后”行，再停机恢复原快照并核完整库存。两浏览器读到原审批行、未读到快照之后行；仅同候选副本回退 |
| 启动拒绝 | 修改Web、替换旧Server、错candidate编号均被原包启动器拒绝；真实端口占用与本任务数据目录ACL拒写均实际启动失败。五份原始命令收据仍为FAIL / exit1，套件另记PASS_EXPECTED_REFUSAL；目录/端口失败保留NativeAOT异常日志，未宣称友好恢复 |

`suite-10-b`耗时95.93秒：first26项、reopen5项、rollback4项、legacy2项均PASS，另有五类启动拒绝与同候选回退记录。操作员API建库、建用户/grant、准备1001关系行和1个时序点，以及快照之后的单独写入，明确标作合成夹具准备/变更；真实UI密码登录和UI写审批独立取证。三类浏览器注入在机器记录分别标记，不升级为真实网络故障、崩溃恢复或模型质量验收。

独立 `installed-independent-audit.json`核20份Job收据、268条记录身份，完成任务身份0匹配、监听0残留、0信号；重复PID观察保留并跳过，未按名称或旧PID杀进程。数据ACL由suite finally恢复原捕获对象，独立检查无Deny并通过一次CreateNew写探针；未持久记录原SDDL，因此不宣称有原/后SDDL逐字比对证据。专属临时包、合成数据/快照与浏览器profile按任务marker和绝对路径回收，原始日志、截图、JSON下载、失败收据及交付三资产保留。

候选09原三资产与P04冻结记录保持不可变。P05发现其profile实际为candidate-08，启动器未校验编号；旧请求头实际返回200而期望428，`c09-explore`仍FAIL。继任包加入编号一致性、AOT source-generated身份读取、匿名no-store身份端点、Web挂载前5秒/4096字节核验与Preview请求版本/SHA约束，清认证/载荷/审批且不重放。

本轮 `suite-10-a`在审批后过早回读而FAIL：组件在异步提交之前消费并关闭审批，脚本原先把弹窗消失当提交完成。验收工具改为等待实际写入完整响应后回读；仅工具源码发生此修正，candidate10包字节未变。原suite-a强制Job清理如实保留，不写成正常停止。更早的Fetch `status()`误用、隐藏控制台AttachConsole失败/cleanup=false、P02安全事故及其它FAIL/UNKNOWN记录不覆盖。

此结果覆盖本机实际解压包的R1；未在干净Windows机器执行安装，也未验证服务安装、代理子路径、跨域/TLS、容器、崩溃恢复、任意升级/降级、七模型专用预算或生产业务。SSE事件等延期入口仅验证拒绝，未冒称可用。P06接说明/限制/升级回退和四附件分发合同，P07核同候选原发布门禁与243依赖中的原文/元数据/Microsoft再分发条款，P08仍须单独授权。Workbench/WB98自动化保持PAUSED；未tag、公开Release、生产部署、重启编辑器或跨聊天外发。

P05主交付提交 `6b90fcfe54e2ced1ea141bdb297e50f13c80a18c`已正常推送并独立核remote main，保护parity-results仍`0e48b753da7eab8ebd9651d405ddb58e3bef01c8`；完整restore、原CI同级format及staged whitespace均PASS。提交后首份final audit错误筛出0份收据，**该空窗结果无效**：本机JSON自动日期转换后再转字符串解析丢失UTC偏移。单条实际收据micro重现后，工具保留字符串时间并增加至少15收据/100身份的下限；[审计补记](preview-1-candidate-10-acceptance-audit.json)绑定新实证20收据/337身份、0live/0监听/0signal与24临时目录回收。原错误空窗JSON、micro与修正脚本/结果分存，不覆写为PASS。

原安装独立audit20收据/268身份未使用时间窗口筛选，且PID查询全部不存在（reused=0），未进入有日期解析问题的匹配分支；原安装/停止证据继续有效。candidate10冻结文件与包字节保持不可变，补记只补交付后审计，不改变R1/P06～P08边界或创建新包版本。
