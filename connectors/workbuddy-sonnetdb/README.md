# SonnetDB WorkBuddy Connector

这是 SonnetDB 的 WorkBuddy MCP 连接器包，采用用户自填凭证模式，连接用户自己的 SonnetDB Server。当前能力是只读 MCP 访问；九模型使用及部署、升级、备份、恢复、维护仅提供指导，不承诺自动执行或端到端运维闭环。

## 0.1.1 上传解析修复

0.1.0 被开放平台拒绝：URL 模板自身缺少协议头。0.1.1 改为 `https://${SONNETDB_HOST}/mcp/${SONNETDB_DATABASE}`，分别填写主机（可含端口，不含协议、路径、@ 或凭据）、数据库名与 Token。正式包固定 HTTPS；本机 HTTP 联调证据不能替代正式 HTTPS 验收。旧包保留仅供追溯，不再上传。平台重新解析结果仍待确认。

## 正式包文件白名单

- `connector-meta.json`：连接器市场元信息。
- `mcp.json`：Streamable HTTP MCP 配置。
- `token-schema.json`：SonnetDB MCP 地址和 Bearer Token 表单。
- `icon.svg`：市场图标。
- `skills/sonnetdb-skill/SKILL.md`：工具参数、返回值、查询与安全边界说明。

`README.md`、`validate-package.py` 和 `docker-compose.verify.yml` 是本地辅助文件，不进入审核包。正式包只允许上述五个文件；禁止直接把整个目录递归压缩，避免包含脚本、测试数据或凭证。该白名单是本项目的安全策略，不是官方 ZIP 格式要求。

## 当前能力

当前九个只读 MCP 工具为 `list_databases`、`list_measurements`、`describe_measurement`、`sample_rows`、`explain_sql`、`query_sql`、`docs_search`、`skill_search`、`skill_load`。它们提供数据库可见性、measurement/schema、样例数据、受支持的只读 SQL 和知识检索。

**九个只读 MCP 工具不等于九模型 typed API。** SonnetDB 的九模型能力不意味着每个模型都能经本连接器直接访问；以运行服务的 `tools/list`、输入/输出 schema 及只读 SQL 子集为准。尤其不得将知识检索工具误写为 KV、文档、全文、向量、对象、MQ 或图模型专用 API。

部署、升级、备份、恢复、写入、DDL 和服务重启不会由当前连接器自动执行。Skill 只提供计划、检查清单和待人工确认的操作建议；需要变更时另行明确授权管理入口。

## 连接与凭证安全

SonnetDB Server 必须已启用 MCP，并提供具体数据库的 `/mcp/{database}` HTTPS 地址。为连接器单独创建仅有目标数据库 `Read` grant 的 Token，不使用管理员或全库 Token。WorkBuddy Token 模式凭证保存在本机；不要在仓库、日志、提示词、SQL、截图或 URL 中保存真实 Token。

生产连接必须使用 HTTPS；HTTP 只用于隔离的本地开发测试。静态验证仅验证模板与指引，无法判断用户运行时填入的地址、证书或授权。

轮换时由用户在管理入口创建新 Token、更新连接器配置并重新连接验证，再撤销旧 Token；疑似泄露则先立即撤销。旧 Token 撤销（revoked）后必须收到 HTTP `401`，新 Token 需单独验证连接及 Read 范围。遇到 `401` 停止调用并更新配置，不用旧 Token 自动重试，不在对话中索取 Token。轮换和撤销不由此 MCP 自动执行。

## 本地静态验证

需要 Python 3.9 或更高版本，仅使用标准库。从仓库根目录运行以下命令；脚本也可从任意目录通过绝对路径运行。

```bash
python connectors/workbuddy-sonnetdb/validate-package.py
```

检查项目包括 JSON 解析与重复键、元信息/英文示例、source/server key、token 字段与变量逐一匹配、Skill frontmatter/工具说明/权限边界、SVG XML 和主动内容、本地 Markdown 引用、启发式 secret 扫描及文件白名单。仅扫描本连接器目录，不读取任何环境变量或 `.env` 内容，不联网；遇到 `.env` 或未知文件直接 FAIL。秘密扫描只是启发式筛查，PASS 不代表可证明没有秘密，仍须人工复核。

静态验证通过后，可选择按白名单生成本地审核 ZIP，默认文件名从 `connector-meta.json` 的 `version` 派生；当前输出到仓库根目录的 `outputs/sonnetdb-workbuddy-0.1.1-review.zip`。请先确认输出目录已存在；同名 ZIP 存在时拒绝覆盖。ZIP 内直接放置上述五个文件，不含本说明、验证器或本机 Compose 配置；不会上传。生成物在源码目录之外，不会影响下一次文件白名单检查。

```bash
python connectors/workbuddy-sonnetdb/validate-package.py --zip
```

需要自定义路径时使用 `--output`（须与 `--zip` 同用）；相对路径基于当前工作目录，输出必须在连接器源码目录之外，且父目录必须已存在。

```bash
python connectors/workbuddy-sonnetdb/validate-package.py --zip --output outputs/sonnetdb-custom-review.zip
```

## 隔离本机联调与数据卷

`docker-compose.verify.yml` 使用 `external: true` 的命名卷；Compose 不会自动创建该卷。每轮新验收前，先将 `container_name` 和卷的 `name` 改为本轮未使用的独立名称，再用 `docker volume create <本轮卷名>` 预创建对应卷。示例配置中的历史名称仅用于追溯，不应直接复用；端口也须避开正在运行的容器，并让验收脚本的 `--base-url` 与回环 HostPort 一致。

每轮必须使用全新独立卷，保留历史卷和脱敏报告；不要删除历史卷，也不要用 `docker compose down -v` 或 `docker volume rm` 清理历史证据。仅重启同一轮容器以验证持久性时，继续使用本轮原卷。

验收脚本首次初始化时，若 `WBVERIFY_ADMIN_USERNAME` 和 `WBVERIFY_ADMIN_PASSWORD` 两项均未设置，会随机生成管理员凭据并仅存内存（仅设置其中一项则报错停止）；运行结束后无法靠脚本找回这些凭据，不能将同一历史卷当作可再次登录的环境。再次运行已有初始化服务需要预先持有的管理员凭据；若未保留凭据，应另开新一轮独立容器和卷，不重置或删除旧卷。需要后续登录时，首次运行前自行通过环境变量提供并在仓库外安全保管管理员凭据，勿写入 Compose、日志或报告。

## 提交前尚需验证

本地静态 PASS 不代表真实服务验证成功或已送审。提交前还需使用真实部署与最小权限 Token 完成：

1. 连接、`tools/list`、九工具实际 schema 与成功/错误返回。
2. 有界查询、`truncated`、只读限制与未授权数据库隔离。
3. 文档/技能 provider 不可用时的错误与恢复边界。
4. 新旧 Token 轮换、撤销旧 Token 后 HTTP `401`，以及更新连接后的新 Token 可用性。
5. HTTPS、无秘密日志、正式包内容人工复核。

本目录不包含真实凭证；验证器不代替上述真实服务测试，不自动上传、送审或发布。
