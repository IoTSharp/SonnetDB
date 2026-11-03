# WorkBuddy SonnetDB 连接器提交审核清单

## A. 连接器包

- [ ] `connector-meta.json` 元信息完整，描述覆盖部署、连接、九模型、升级、备份和维护。
- [ ] `mcp.json` 仅配置一个 MCP Server，使用 HTTPS Streamable HTTP。
- [ ] URL 和 Token 使用用户自填变量，不包含真实凭证。
- [ ] `icon.svg` 可渲染，尺寸和标题正确。
- [ ] Skill frontmatter、名称、描述和版本完整。
- [ ] README 说明安装前提、能力、边界和验收方式。
- [ ] 连接器与Skill版本统一0.2.0，元信息/表单说明5.0的43工具、默认Read及已明确授权的Write。

## B. 真实服务联调

- [ ] 使用真实 SonnetDB Server 验证连接。
- [ ] `tools/list`与输入/输出schema符合43工具基线（28只读/15写入）；工具发现不等于写授权，旧九工具服务须明确不满足本版。
- [ ] `list_databases` 只返回 Token 可见数据库。
- [ ] 至少验证 schema 发现、样例读取、`EXPLAIN` 和有界只读查询。
- [ ] 验证结果上限和 `truncated` 语义。
- [ ] 验证 `bad_request`、`db_not_found`、`forbidden`、`invalid_sql`、`read_only_violation`。
- [ ] 验证 Token 轮换和旧 Token 失效。
- [ ] 验证超时、取消和服务不可用时的用户提示。
- [ ] 默认Read凭据被独立写工具拒绝，已有明确授权和Write grant时另验写入/回读；query_sql仍只读。
- [ ] 验证write_outcome_unknown/写入断连或取消时不盲重试，以及预算/游标的错误终态。

## C. 九模型材料

- [ ] 时序。
- [ ] 关系表。
- [ ] KV/缓存。
- [ ] JSON 文档。
- [ ] 全文检索。
- [ ] 向量检索。
- [ ] 对象存储。
- [ ] SonnetMQ。
- [ ] 原生属性图（Graph Beta）。

每种模型都应有用途、schema 发现、最小读取示例、边界、写入确认规则和版本来源。尚未真实验收的模型必须标记为“未验证”。

## D. 运维闭环

- [ ] 部署指南：版本、运行时、目录、HTTPS、鉴权、grant、健康检查、日志、指标。
- [ ] 连接指南：MCP URL、Token、最小权限和首次验证顺序。
- [ ] 升级指南：兼容性、备份、WAL、回滚、升级后验收。
- [ ] 备份指南：恢复对象、校验、演练、外部资源边界。
- [ ] 维护指南：健康、日志、指标、资源、故障分类和变更确认。
- [ ] 明确单节点、Graph Beta、非 wire 兼容等产品边界。
- [ ] 明确数据库Write不包含部署、升级、备份恢复或服务重启授权，实例MQ不属于单库备份。

## E. 清洁包和证据

- [ ] 移除 Token、密码、客户数据和内部敏感日志。
- [ ] 本地 JSON/Markdown/SVG 静态检查通过。
- [ ] 真实联调证据与静态检查证据分开。
- [ ] SonnetDB 产品测试、连接器测试和 WorkBuddy 审核结果分开。
- [ ] 版本、提交、文档来源和验证时间可追溯。
- [ ] 2026-11-03 r2九工具/六模型MCP历史报告保持原date/source；新43工具生产证据、0.2.0静态包与宿主/HTTPS/平台审核分开，不改写旧报告为43PASS。
- [ ] 资料库文档已上传且可检索。
