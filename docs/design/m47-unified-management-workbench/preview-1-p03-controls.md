# M47-P03：Preview 入口、授权、结果预算与终态

2026-10-10（Asia/Shanghai）。本轮只处理 P03，采用[范围修订 R1](preview-1-p03-scope-revision.md)。**七个模型的专用读取延期，不能称九模型读取全部验收。产品仍 NOT_READY。** 候选01、02不可变；代码提交 `faa03a018c77afff4ffe3db5cac50a5a78e874cf` 已冻结为[候选03](preview-1-candidate-03.json) / `4.5.0-preview.1.3`；最终文档提交不是新的二进制候选。

## 实现

- `VITE_WORKBENCH_PROFILE=preview-1` 选择独立 Preview 外壳；默认完整模式保持，未知 profile 拒绝。七模块位置保留，开放概览、受限工作台和 About。直接路由、旧恢复参数、OAuth/auto-login、延期子页及发送边界默认拒绝；不恢复旧 tab、远程连接、查询历史或草稿。
- 正常用户名密码登录。Server `/v1/db/{db}/access` 分别回读 HTTP role 与数据库 grant；普通用户的 `readwrite` HTTP role 不能替代数据库 Write。数据请求和导出前重新核权，切库、身份变化、撤权、取消、断连时清空载荷、草稿与审批，旧响应不能覆盖新上下文。重连只能显式重读。
- 自由 SQL 只允许单条受支持只读 AST；Server 还有独立 AST 检查和执行预算。关系表写只允许一次确认的一行参数化 INSERT，发送前消费审批。取消不是回滚，丢响应、HTTP408/5xx或损坏终态为 unknown，不自动重试。
- 浏览器使用一次实际 fetch 的有界队列、30秒截止、64 KiB 输入、4 MiB 增量响应及解码/保留预算。SQL 行数、完整终态、当前窗口导出均校验。Server SQL 设置30秒截止、4 MiB估算物化预算、10000累计物化行数和最多1000输出行；不支持的预算路径拒绝。schema 不再走原目录/备份统计，而是最多1000资源/列/主键项的有界投影。
- 保留 SQL 原拼写、Graph Beta、MQ database + Topic 逻辑身份与实例 `.system/mq` 持久化/单库备份缺口。原五 Workbench job/artifact 和 release policy 未改。

## 本地证据

证据根目录：`artifacts/m47-p03-20261010`。所有命令的 `.spec.json`、双流、根/控制器身份、返回码、Job成员清空结果及原失败记录独立保存。尚未生成预览发行包；本地 Web 资源只是验证输入。

| 检查 | 实际结果与边界 |
|---|---|
| Preview TypeScript / Vite | web-typecheck-04、web-build-04 PASS；实际构建输出与index.html存在，非发行物 |
| Node合同 | node-contracts-04：465/465，串行执行，0失败；此前进程失败保留 |
| 真实Kestrel | server-tests-06：19/19；真实角色/grant、单行参数、拒绝范围外SQL、输出/物化/schema预算、正常Stop/Dispose/Start持久化回读。生产AOT分析无IL警告，未NativeAOT publish |
| 真实浏览器 | real-08：12/12；正式/admin资源、setup与普通/只读UI登录、撤权/撤销token、一次审批、真实写已提交后丢响应、显式故障注入及代际隔离 |
| 完整模式四分片 | 89/0/0、64/13/2、86/0/2、63/12/1（通过/skip/失败）；原332个ID/标题/文件一致 |
| 完整模式定向与浏览器对照 | 五项定向3通过/2失败；原HEAD源码读取覆盖对照中KV通过、measurement同样超时；当前系统Chrome两项均超时；配套Chromium measurement通过、KV仍超时。合并当前源码证据306通过/25原skip/1未决；不称四片零失败、稳定性恢复或两环境等价 |
| StudioHost额外夹具 | studio-host：12/12，StudioNative fixture；非桌面安装/实物验收 |
| 输入绑定 | 297个当前源码/配置SHA与最终验证前快照一致，候选冻结另核Git规范化blob |
| 执行清理 | smoke-current-diagnostic原cleanup=false/NTSTATUS C000010A保留；23个记录PID在随后只读快照均不存在，原完整清理仍未证。退出竞态改为在原句柄上有界等待，不扩大终止范围；其余本轮已结束Job各自回执为准 |
| 发布与完整验收 | NOT_READY；P03完整模式KV超时尚未关闭，七模型专用读取延期，远端门禁及P04～P08未执行 |

真实浏览器运行使用隔离 Kestrel、正式 `/admin` 同源静态资源和已存在 Chrome；初始化、管理员再登录及日常普通用户登录均通过 UI，管理员测试 token 只用于准备隔离数据，不注入替代登录。丢响应测试先让真实 INSERT 提交，再切断返回；超大响应和延迟/断连是显式故障注入，与真实 Server 授权/存储证据分列。

正常停止后重开由真实 Kestrel xUnit 的 `StopAsync`、Dispose、重新 Start 和持久化回读证明。浏览器批次结束时的 Job 清理不是正常停机或恢复证明。UserStore 没有 token 到期字段，本轮真实验证的是撤销后401；没有冒称定时过期验收。

## 原失败与执行边界

- 原 `web-build-01` 根进程 exit0 但没有构建输出，独立验证记为 `FAIL_NO_BUILD_OUTPUT`；后续直接 Vite 执行才取得真实构建产物，旧记录保留。
- 原 Node 首轮因 VM 测试缺少新增依赖而失败，补完整模式依赖绑定、保留断言后458/458；后续一次两项夹具进程失败，原因未证。两项及新增 Preview 合同独立复核24/24，最终全量结果以上表为准。
- 真实浏览器早期失败包括测试 SQL/集合准备、定位器重复匹配、首次页面重定向和 GRANT 不降低已有权限的准备错误。全部保留；没有把未执行用例计入通过。范围 R1 依据服务端预算缺口制定，不用这些准备错误作为模型能力失败证据。
- 完整模式第二分片一次在 `browserContext.newPage` 超时，主动取消并仅清理所属 Job；后继运行另有 worker 非正常退出。具体通过与未决边界以上表为准，不把后续通过反写为原运行成功。
- 新执行器先挂起任务根、加入不可 breakaway 的 Windows Job 再启动；保留句柄与 PID/创建时间/命令/父身份，成功清理批次核内核成员为空。`smoke-current-diagnostic` 遇到进程退出中的命令读取失败，原 ERROR/cleanup=false 保留；后来23个记录PID不存在不补足原完整清理证明。后续仅在原句柄上增加最多250毫秒、总10秒的退出等待，不扩大终止范围。没有使用旧 PID 父链执行器、按名称结束进程或操作编辑器。P02旧越界事件及恢复未知不改写。

## 提交与后续

P03代码提交和候选文档提交都必须在各自最终树取得完整 restore、与 CI 相同的 Format Check 和 cached whitespace PASS，精确暂存本任务路径。共享 HANDOFF/CHANGELOG/ROADMAP/queue/validation 只暂存 HEAD 加本片变更；其余历史、发布账本、博客、三个 VS Code pending 保留。最终来源和清单见后继候选03，不能继承候选02的 PASS。

P04～P08 未执行：无包内 profile/版本配对、NativeAOT发行包、实物安装、部署、完整恢复矩阵、远端 workflow、push、tag或发布。P04/P05/P07仍须逐步验证最终完整候选；七个延期模型重新开放需要新预算实现、新证据与新候选。旧 Workbench 自动化/WB98未恢复，也未向来源会话发消息。
