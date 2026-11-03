# 5.0 发布与 MCP / WorkBuddy 任务合并

日期：2026-11-04（Asia/Shanghai）。本记录将发布核查与用户指定的另一聊天统一为同一交付流程，不表示版本已经放行或发布。

## 统一的目标与分工

用户要求合并[原分支/MCP聊天](codex://threads/01a1a21a-84e7-74c2-aea2-77a9cc1abafd)与[本发布聊天](codex://threads/01a1a0f7-bf7e-7e12-a697-1e8509b4de86)。已向原聊天发送协调指令，并独立读取其回复：它停止 main、正式标签及发布资产写入，仅继续 MCP 与 WorkBuddy 两个切片。本发布聊天负责逐项主线整合、最终候选、十二类门禁以及 GitHub / NuGet / Docker 发布与回读。

另一聊天中用户明确选择了“本次也补齐写入工具，并按数据库权限校验”。因此整合范围包括九模型的 MCP 发现/读取以及适用写工具，保持旧只读入口兼容，验证只读凭据拒绝、数据库权限、输入/响应预算、MQ 实例归属及 source-generated JSON / NativeAOT。不能把原九个只读工具的历史验收提升为新读写工具的通过。

WorkBuddy PR #390 还需修复并验证重启时的容器端口归属；其标题/描述和真实联调证据应匹配最终代码。原聊天已承诺不再合并或发布旧候选。

## 分支与发布身份

本次 fetch 后远端 main 仍为 `78c2c74699e7c8231e6228b878fbaa4bf345f27a`。先前 EXISTS、测试宿主、VS Code 与所列 Dependabot 分支均已是 main 祖先，不能重复包装为待合入实现。

当前待整合的发布 PR 为 [#389](https://github.com/IoTSharp/SonnetDB/pull/389)，已推头 `29f206fc9238919721f314a7763a66a835cab842`；WorkBuddy 为 [#390](https://github.com/IoTSharp/SonnetDB/pull/390)。`parity-results` 是独立结果分支，保持原合同，不合并到开发主线、不清理其引用。后续 MCP 与审查修复分支按精确 SHA 单独核验后再整合。

用户已授权必要修复后将 `v5.0.0` 重新固定到新提交，也已批准 Testcontainers 包改名 `SonnetDB.Testcontainers`。旧标签对象 `a3d3e7baf378054254b86942d7e854026cbc6050` 指向 `455bf3b53b1e56df0064d8f574b0595cbb0b609e`，本流程尚未替换。原正式 Publish 的失败、公开包缺失与旧 Docker 镜像部分分发保留历史身份，不能算新候选交付。

## 本轮发现的真实阻断

| 阻断 | 已确认的原因与当前边界 |
| --- | --- |
| EF / ABP 时间写入 | 默认 DateTime 原用 legacy DATETIME / DbType.DateTime，真实 Clock 亚毫秒触发拒绝。修复后最终EF309/309；新Core候选下ABP标准迁移及原22项全部通过。公开包与统一最终SHA仍另验。 |
| 自引用表非主键 schema 演进 | 原 native schema 路径无条件按主键迁移处理入站引用，普通日期列演进也被 self-FK guard 拒绝。最小修复保留journal/floor/FK校验，定向8/8、完整相关组403/403；真实存量GUID/STRING主键首次进入native的编码/重开也已验证。 |
| Ubuntu 流式测试 | 旧 cb 候选仅一项在故障注入前的初开2.598秒超过夹具两秒期限。仅该测试改十秒，45秒总限和故障断言保留；本机相关287/287通过。 |
| Full Parity | 旧29候选在构建最终镜像时磁盘耗尽，SQL runner未执行；不是SQL对照不一致。专属builder逐项构建/释放后再启动完整参考栈，Linux八项编排合同通过，最终 hosted full 尚待新候选验收。 |

四个 ORM 的原候选测试、外部 fork 更新、上游合并、公开包和最终 Server 消费者各有身份。FreeSql、XCode、Chloe、SqlSugar 的候选 PASS 不能替代新候选/公开包复验。XCode后续Draft PR #103和Chloe现有Draft PR #435已分别推送f21d0c75/742ea2ac，SqlSugar现有PR #1457已推18f9df7e，ABP升级Draft PR #2已推979c3e4e。外部上游合入与发包保持各自实际状态。完整证据见各独占报告及 ignored `artifacts/release-5.0-20261103-*`。

用户要求后续的SonnetDB未完成项直接登记Issue。已去重创建并回读 #392—#401，具体范围见[后续任务清单](release-5.0-20261104-followups.md)。本轮已经修复的日期、非主键自引用迁移、MCP基础模型/权限/预算和Testcontainers包名不重复立项。

## 后续顺序

1. 收敛 EF/ABP、自引用 schema、Parity 资源、MCP 与 WorkBuddy 及外部适配修复，保留失败原始证据。
2. 在每次最终待提交树完成规定 restore + Format，逐一整合相应 PR，冻结统一提交。
3. 对该提交和精确5.0.0取得十二类工作流、raw Parity 与候选包/Server证据。
4. 门槛通过后按用户授权替换正式标签，完成十包、完整 Release/连接器资产和 Docker 新源码身份回读。
5. 全新公开缓存消费者复验，更新最终交接；上游无权合并或尚未发布的 Provider 保持明确待办。

这是一条继续进行的统一任务。尚未创建第二条正式发布路径，尚未以旧候选重跑结果放行5.0。
