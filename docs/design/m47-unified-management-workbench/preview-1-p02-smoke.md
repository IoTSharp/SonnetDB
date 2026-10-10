# M47-P02：Web smoke 修复与候选验证

2026-10-10（Asia/Shanghai）。本轮只处理 P02；发布状态保持 **NOT_READY**，P03～P08 未执行。候选 01、P01 范围/预算/资产白名单、旧失败和三个 pending VS Code 脚本均保留。

## 接续本地验收（2026-10-10）

**Web 修复与本地验收通过；后继候选待修复提交后独立冻结。产品仍 NOT_READY。** 用户已在停止披露后明确要求继续；未重启编辑器或确认其恢复，不改写下方旧停止状态。新证据目录为 artifacts/m47-p02-resume-20261010。

| 检查 | 接续结果与边界 |
|---|---|
| 四片 fixture smoke | 332 项逐 ID/标题/文件匹配原清单；307 passed、25 skipped、0 failed/flaky，单 worker、retries=0。四片实际数量分别为 89 passed/0 skipped、66 passed/13 skipped、88 passed/0 skipped、64 passed/12 skipped |
| Studio 宿主补充回归 | 用 StudioNative runtime 执行原 studio-host-client.spec.ts，12 passed、0 skipped；覆盖身份/生命周期负向守卫。默认 skip 中另13项 StudioNative suite 未在本次单独执行 |
| Measurement 501 行 | 隔离一次通过，第三分片再次通过；旧5秒点击超时原因仍 unknown，不改产品或增加超时来获得PASS |
| Web build / Node 合同 | 复用旧窗口已完成的 build、448/448；236个Web/workflow/policy输入SHA在接续前后相等，提交后另核Git blob绑定。build仅任务验证物，未生成预览发行包 |
| 提交门禁 | 最终待提交树完整restore与原CI Format串行执行；实际结果见本地gate收据，不以旧Format代替本次检查 |
| 真实服务 / GitHub | 30条独立真实suite及所有后继候选远端门禁NOT_RUN；原缺URL拒绝、历史两次CI失败和旧Object flaky保留 |

新任务执行器先挂起新根进程、加入不可脱离的独立 Windows Job，再启动；内核继承限定子进程范围，保留句柄与PID/创建/命令/父身份记录，不从旧PID收集外部后代。正常退出、超时、已退出父进程的detached子进程及另一个独立sentinel隔离检查通过。初版conhost命令变化检查未通过；改为无控制台启动。第二分片第一次因短命命令读取缺项中断，仍记ERROR/cleanupComplete=false；后续延后观察，存活成员清理仍核完整身份和组归属。上述错误及原安全事件均不覆盖。

本地仅用任务级fixture启动适配器保留仓库runner的Vite环境、就绪检查、Playwright参数和15分钟上限，把Windows身份/结束处理交给Job边界；没有使用旧PID执行器或仓库Windows taskkill路径。通过的四片与额外宿主任务均自然exit0，最终Job成员为空。这证明这些接续任务的隔离回收，不能升级原执行器FAIL或证明全机/编辑器恢复。正式CI仍使用仓库npm入口、Node22/Linux，尚未在后继候选运行。

P02只修测试/收集与CI合同入口；生产代码、独立真实suite、原五job/artifact和发布policy保持。P03～P08、C01～C05/D01入口与发行物控制仍未验收；无push、dispatch、tag或发布。

## 历史停止与进程越界事件（原记录保留）

停止时：**P02 未完成、未提交；后继候选 02 未创建。** 任务级执行器错误地沿用了已退出进程的 PID 作为子进程发现父节点。PID29260 被 VS Code C# Dev Kit 复用后，9 个不属于本任务的服务/构建辅助进程及其 conhost 被误纳入并终止：42804、35768、45612、11320、83412、85820、82948、70104、60776。另有已终止 conhost41532、72024 的实际归属未能确认。这违反了进程归属约束；不能以“原句柄/命令匹配”掩盖父节点授权错误。

发现后立即停止后续验收、冻结和提交；任务 cancel 标志保留，Stage-Fix 与当前执行器禁用。四分片批次只完成第一片（自然 exit0、0 cleanup signal），第二至第四片未启动。没有重启 VS Code、C# 服务或其它应用；其当前可用性和恢复状态未验证，需要用户人工检查后决定如何继续。

原始 `smoke-all-v2.result.json` 的15次清理信号和 cleanupComplete=false 原样保留；`process-scope-incident.json` 记录已确认9个外部进程与另外2个归属未决进程。新快照发现PID复用时保留外部进程，不能宣称全部记录身份属于本任务或已经证明全会话无孤儿。第一轮完整smoke也保留失败；第二轮还记录了一条Measurement查询点击后等待5秒超时，未完成诊断或修复。两个全量窗口都不是PASS。

可复用的局部证据只有：Web build、448条Node合同、修复后三条定向测试、collection检查，以及完整restore/format的自然exit0。Format仍有工作区加载警告。它们不能替代完整smoke或候选/远端验收。六个代码/配置文件及本报告保留为未提交修改；没有改写候选01、根路线状态或发布策略，也没有把计划中的12条StudioHost回归写为已执行。

## 原始失败与分类

直接 GitHub API 连接失败后，经本机代理只读取得原始 run、job、step 和日志，未触发重跑。证据目录为 `artifacts/m47-p02-smoke-20261010`。

| 原始运行 | 来源与结果 |
|---|---|
| `37731752397`，attempt 1，2026-10-08 | SHA `5424b66672e37f49a2a5633fdde0b54a9b1f0290`；Web job `113162223537` 的 build success、smoke failure。12 failed、303 passed、1 flaky、27 skipped、19 did not run；其它四 job success |
| `38031369602`，attempt 1，2026-10-10 | SHA `c40fa670e3a170c5fac8cefa181d618631862270`，即候选 01；Web job `114152834357` 同样失败。12 failed、304 passed、27 skipped、19 did not run；其它四 job success。这是本轮读取的最近五次列表中的最新一项，不继承其四项成功给后继候选 |

1. **Vite/Node 环境错用**：`copilot-browser-direct.spec.ts` 的 logout 用例在 Node 创建生产 auth store，`createApiClient` 读取 `import.meta.env.BASE_URL` 抛错。生产代码在 Vite 环境下有该变量；不为迎合夹具添加生产回退。用例改为在 Vite 登录页已初始化的 Pinia 中执行真实 store logout，仍断言 public token 不再可用、仅调用本地 health、数据库认证与持久状态清空。
2. **测试收集边界混用**：普通 fixture runner 只启动 Vite，却收集九模型真实权限测试；九个 spec 的第一条在缺少 `SONNETDB_*_REAL_BASE_URL` 时按合同失败，后续串行用例没有执行。显式 `playwright.smoke.config.ts` 选择 fixture suite；原 `playwright.config.ts` 与各 `run-*-real.mjs` 保留真实 suite 和环境拒绝。收集逐项核对为完整 362 条 = fixture 332 条 + 独立真实 30 条（10 个文件，包括 KV atomic）；没有删除/改为 skip/改 mock 的真实测试。
3. **Studio 夹具陈旧**：旧响应缺少 `processOwner/lifecycleState/canStop` 与完整连接身份，还期待已废弃的 `Local healthy` 标签；页面正确显示“宿主合同无法确认”。另一个用例在 Server 正运行时请求打开其它嵌入库，按钮按合同禁用。修复夹具字段、现行标签和停止状态前置，再核单次打开请求与返回状态。生产身份/生命周期守卫不变，已有 `studio-host-client.spec.ts` 负向合同保留。

历史第一条 run 另有 Object harness 的页面导航竞态，重试后通过；候选 01 同用例一次通过。本轮没有据此改写历史 flaky，也没有把该旧观察宣称为已定位的产品错误。

Playwright 默认还会加载 e2e 目录内的 Node `.test.mjs`，把其 TAP 副作用混入浏览器日志。基础配置现在明确只收集 `.spec.ts`；`npm run test:contracts` 用 `node --experimental-vm-modules --test --test-concurrency=2` 独立执行全部 34 个 Web 合同文件和 7 个 e2e Node 文件，CI 新增必经步骤。原 build/smoke step、五 job、required artifact 和 `eng/release-readiness-policy.ps1` 不删减。

## 旧窗口局部验证与来源

以下为旧窗口停止时的结果；接续验收见本报告前部。

| 检查 | 结果 / 边界 |
|---|---|
| 原三条本地复现 | 3 failed；日志与截图/trace 保留 |
| 修复后定向回归 | 3 passed，retries=0 |
| Node 合同 | 448 passed，0 failed，0 skipped |
| Web build | `vue-tsc --noEmit` + Vite 成功；输出到独立任务目录。大 chunk 警告保留；不是预览发行物 |
| fixture 完整 smoke | 两次中断/失败；四分片仅第一片完成，其余 NOT_RUN |
| collection contract | 332 条 fixture 与基础配置中的非 real 用例逐项一致；30 条 real 仍可收集；Node 测试不混入 Playwright |
| 真实测试前置拒绝 | 无 Server URL 的 Document 用例仍明确失败、exit 1；这是预期负向检查，不是真实权限旅程 PASS |
| 原完整门禁命令 | restore 与 Format 自然 exit0；Format有工作区加载警告。因安全事件停止，不执行提交 |

本机 Node `24.15.0`、Playwright `1.55.1`、Vite `6.4.2`、TypeScript `5.6.3`；原 CI 使用 Node 22/Linux。236 个 Web/workflow/policy 输入已保存 SHA256，提交时核对源字节相等。共享工作树中的其它文档、博客和 VS Code pending 不参与本轮 Web 修复提交；本地结果不冒称新的 GitHub run 或干净 CI checkout 结果。

执行器第一次完整 smoke 因短命浏览器进程的命令行不可核而中断，原 FAIL/cleanupComplete=false 保留。随后对 51 个已记录身份及其直接子进程做一次新快照，当前为空；这不反写旧清理结论，也不证明未观察后代。任务级执行器仅把不完整候选留在非归属集合、不发信号，最终仍在则阻断；对已结束身份用单次有界查询，避免逐 PID 查询耗尽预算。原完整 restore 的第一次尝试也因观察超时未被接收，保留原结果；第二次完整命令成功。未修改旧 VS Code helper、未恢复 WB98，未进行按名称终止。

## 停止时的接续边界（历史）

后继候选 02 / `4.5.0-preview.1.2` 原拟在修复提交后创建，实际尚未创建；候选 01 不变。接续须先处理安全事件和完整smoke未决项，再提交并冻结新来源，不能把原 run 的四 job success 移植为新候选的整体 PASS。没有 push、tag、PR、dispatch、发布、部署或安装。

P02 完成后的下一项才是 P03：在候选上实施/验收 C01～C04、真实登录与普通/只读用户、撤权载荷清理、预算和承诺的取消/unknown/恢复。P04～P06 继续处理 C05/D01 和实际包；P07 核最终同 SHA/version/latest attempt 的完整既有门禁。Studio/VSIX 公开下载物仍延期；Graph Beta、MQ database+Topic 逻辑身份与 instance `.system/mq` 持久化、单库备份缺口、一次审批、取消非回滚和 unknown 不重放均保持。旧 workbench 自动化实查 **PAUSED**。
