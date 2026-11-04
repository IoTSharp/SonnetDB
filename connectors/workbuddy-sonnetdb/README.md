# SonnetDB WorkBuddy Connector

这是 SonnetDB 的 WorkBuddy MCP 连接器包，采用用户自填凭证模式。当前0.2.0以包含九模型43工具的SonnetDB 5.0合同为接入基线：28个只读工具、15个独立写工具，实际以现场 `tools/list`、`inputSchema` / `outputSchema` 和目标数据库 grant 为准。工具存在不等于用户写授权；默认推荐 Read 凭据，用户已明确授权写入时才使用适当 Write grant。部署、升级、备份、恢复和服务重启需要另有授权，不由数据库写权限自动涵盖。

## 0.2.0 当前合同

连接器和Skill版本同步为0.2.0，沿用0.1.1修复后的HTTPS模板与五文件白名单。43工具生产实现绑定 [MCP切片49731955](https://github.com/IoTSharp/SonnetDB/pull/391)，本次更新只修连接器文案、发现规则和本地校验，不代表该切片已合入、镜像已更新、平台解析/HTTPS/宿主/审核已通过。某些历史同名5.0镜像仍仅公布九工具，须按实际发现判断，不能用版本字符串代替能力校验。

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

时序和关系表保留 `query_sql` 等原九个只读入口，写操作使用受限 `execute_sql`；KV使用 `list_keyspaces` / `get_kv` / `scan_kv` / `put_kv` / `delete_kv`；桶与对象使用列表、范围读取及独立创建/写入/删除工具；MQ提供数据库限定主题发现、只读offset查看、发布和确认。文档、全文/显式向量搜索及Graph Beta也有专用发现、读取和经授权写工具。完整名称及参数以Skill和现场schema为准。

43工具是本版可发现能力基线，不等于全部原生API、事务SDK或所有运维功能。旧九工具服务缺少本版所需能力时明确停止相应操作，不能模拟成功。`docs_search` / `skill_search` 是知识检索，不代替数据模型工具。

写入前须同时满足：用户已明确授权当前操作/范围、现场存在对应工具、目标库拥有所需Write grant。会话已有的明确授权可以沿用，不重复索取；超出范围再澄清。`query_sql` 仍只读，写入使用独立写工具。遇 `write_outcome_unknown`、写入取消或断连先回读，不盲重试。MQ物理存储仍为Server实例范围，单库备份不覆盖实例MQ。

## 连接与凭证安全

SonnetDB Server必须已启用MCP并提供 `/mcp/{database}` HTTPS地址。默认单独创建仅有目标库Read grant的最小权限Token；已有明确写授权时，另选或配置该库所需Write grant，不要求管理员或全库权限。凭证只填写连接器配置字段，不进入提示词、SQL、日志、仓库、截图或URL；连接器实际凭证保存和外发行为仍需真实宿主验收。

生产连接必须使用 HTTPS；HTTP 只用于隔离的本地开发测试。静态验证仅验证模板与指引，无法判断用户运行时填入的地址、证书或授权。

轮换时由用户在管理入口创建新 Token、更新连接器配置并重新连接验证，再撤销旧 Token；疑似泄露则先立即撤销。旧 Token 撤销（revoked）后必须收到 HTTP `401`，新 Token 需单独验证连接及 Read 范围。遇到 `401` 停止调用并更新配置，不用旧 Token 自动重试，不在对话中索取 Token。轮换和撤销不由此 MCP 自动执行。

## 本地静态验证

需要 Python 3.9 或更高版本，仅使用标准库。从仓库根目录运行以下命令；脚本也可从任意目录通过绝对路径运行。

```bash
python connectors/workbuddy-sonnetdb/validate-package.py
```

检查项目包括 JSON 解析与重复键、元信息/英文示例、source/server key、token 字段与变量逐一匹配、Skill frontmatter/工具说明/权限边界、SVG XML 和主动内容、本地 Markdown 引用、启发式 secret 扫描及文件白名单。仅扫描本连接器目录，不读取任何环境变量或 `.env` 内容，不联网；遇到 `.env` 或未知文件直接 FAIL。秘密扫描只是启发式筛查，PASS 不代表可证明没有秘密，仍须人工复核。

静态验证通过后，可按白名单生成本地审核ZIP，当前默认输出 `outputs/sonnetdb-workbuddy-0.2.0-review.zip`。请先确认父目录存在；同名ZIP拒绝覆盖。包内仅有上述五文件，不含README、验证脚本或本机Compose配置，不会上传。旧0.1.0/0.1.1包和历史实调报告保留来源，不代表本版已通过。

```bash
python connectors/workbuddy-sonnetdb/validate-package.py --zip
```

需要自定义路径时使用 `--output`（须与 `--zip` 同用）；相对路径基于当前工作目录，输出必须在连接器源码目录之外，且父目录必须已存在。

```bash
python connectors/workbuddy-sonnetdb/validate-package.py --zip --output outputs/sonnetdb-custom-review.zip
```

## 隔离本机联调与数据卷

`docker-compose.verify.yml` 使用 `external: true` 的命名卷；Compose 不会自动创建该卷。每轮新验收前，先将 `container_name` 和卷的 `name` 改为本轮未使用的独立名称，再用 `docker volume create <本轮卷名>` 预创建对应卷。示例配置中的历史名称仅用于追溯，不应直接复用；端口也须避开正在运行的容器，并让验收脚本的 `--base-url` 与回环 HostPort 一致。

Compose固定的455bf3b5历史镜像只验证过九工具/六模型MCP，未包含43工具。测试0.2.0时须先选定含本版MCP切片的实际镜像和digest，记录source；不能沿用旧镜像报告写成43工具PASS。联调脚本当前要求43工具并使用只读凭据做读取/写拒绝检查，原生API的合成数据播种与可选容器重启分别列明；它不自动把管理员Token用于MCP写入。

每轮必须使用全新独立卷，保留历史卷和脱敏报告；不要删除历史卷，也不要用 `docker compose down -v` 或 `docker volume rm` 清理历史证据。仅重启同一轮容器以验证持久性时，继续使用本轮原卷。

验收脚本首次初始化时，若 `WBVERIFY_ADMIN_USERNAME` 和 `WBVERIFY_ADMIN_PASSWORD` 两项均未设置，会随机生成管理员凭据并仅存内存（仅设置其中一项则报错停止）；运行结束后无法靠脚本找回这些凭据，不能将同一历史卷当作可再次登录的环境。再次运行已有初始化服务需要预先持有的管理员凭据；若未保留凭据，应另开新一轮独立容器和卷，不重置或删除旧卷。需要后续登录时，首次运行前自行通过环境变量提供并在仓库外安全保管管理员凭据，勿写入 Compose、日志或报告。

## 提交前尚需验证

本地静态 PASS 不代表真实服务验证成功或已送审。提交前还需使用真实部署与最小权限 Token 完成：

1. 连接、`tools/list`、本版43工具实际schema与成功/错误返回；缺少能力明确报错。
2. 有界读取/游标/`truncated`、Read凭据写拒绝和未授权数据库隔离；已有明确写授权时另验Write工具及回读，不把发现工具作为写授权。
3. 文档/技能 provider 不可用时的错误与恢复边界。
4. 新旧 Token 轮换、撤销旧 Token 后 HTTP `401`，以及更新连接后的新 Token 可用性。
5. HTTPS、无秘密日志、正式包内容人工复核。

本目录不包含真实凭证；验证器不代替上述真实服务测试，不自动上传、送审或发布。
