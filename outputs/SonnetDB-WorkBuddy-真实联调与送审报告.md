# SonnetDB × WorkBuddy 真实联调与送审报告

## 0.1.1 纠正（2026-11-03）

0.1.0 在开放平台上传解析时被拒绝：MCP URL 模板自身缺少协议头。旧包及其 SHA256 仅保留作历史证据，不再建议上传或送审。0.1.1 已改为 `https://${SONNETDB_HOST}/mcp/${SONNETDB_DATABASE}`，将主机与数据库拆为凭证表单字段；现有新包 `sonnetdb-workbuddy-0.1.1-review.zip` 的实算 SHA256 为 `b8f4e68dc398a956791f27e0497a86b767ce2f0acb4b221832458f5dd741f10a`。

**0.1.1 仍待开放平台重新解析，未确认上传解析通过，未正式送审或发布。** 以下本地 HTTP/MCP 真实联调证据保留原有范围，不代表已完成修订后 HTTPS 连接模板、WorkBuddy 宿主凭证表单或审核网络验收。

日期：2026-11-03。结论：本地真实服务已执行用例通过，0.1.1 修复候选包已存在；完整九模型 MCP 闭环、平台重新解析和正式送审尚未完成。

## 真实环境

- Docker Desktop 4.91.0，Linux/amd64 引擎 29.8.0。
- 官方镜像 iotsharp/sonnetdb:latest 的 OCI 版本为 5.0.0，源码修订 455bf3b53b1e56df0064d8f574b0595cbb0b609e。
- 固定镜像 digest：sha256:6c5974f4ba4e81fd12a82cb08a6265c959d3c63f589f325df7d02ff9a1d81597。
- 仓库基线为 78c2c74699e7c8231e6228b878fbaa4bf345f27a，与镜像修订不同；本报告证明已发布镜像的实际行为，不冒称验证本地源码构建。
- 两轮均为独立容器、独立持久卷、回环端口、随机内存凭证及合成数据。结束后两个容器均正常停止，保留数据卷和原始脱敏证据，未触碰其它业务容器。未保存密码或 Token；这些容器不是可直接交付登录凭据的日常开发环境。

## MCP 与权限验证

第二轮 19 项通用检查全部通过：

- 首次初始化、创建非超级用户、授予目标库 Read。
- MCP initialize 与 tools/list：实际发现九个只读工具。
- resources/list、list_measurements、describe_measurement、sample_rows、explain_sql。
- 可见数据库隔离：隐藏数据库不泄露。
- 无 Bearer 返回 HTTP 401，无数据库 grant 返回 HTTP 403。
- 只读 MCP 拒绝 DELETE，返回 read_only_violation。
- maxRows=0、-1、1001 均拒绝。
- 无 LIMIT、LIMIT 3 配合 maxRows=2 均返回两行且 truncated=true；LIMIT 1 返回一行且 truncated=false。
- 签发新 Token 后验证可读，吊销旧 Token 后验证旧 Token 返回 401、新 Token 仍可读。
- 容器重启成功，随后九模型逐项读回通过。

本次是标准 MCP HTTP 协议客户端真实调用，不是 WorkBuddy 宿主安装与连接表单验收。轮换验证发生在服务 API 和 MCP 层，没有将新 Token 填入实际 WorkBuddy 连接器表单。

## 九模型实测矩阵

| 模型 | 原生 HTTP 写入并读回 | 容器重启后读回 | MCP 代表性只读查询 |
|---|---|---|---|
| 时序 | PASS | PASS | PASS |
| 关系 | PASS | PASS | PASS |
| KV | PASS | PASS | 未提供/未确认对应入口 |
| JSON 文档 | PASS | PASS | PASS，经 SELECT |
| 全文 | PASS | PASS | PASS，经 SELECT/MATCH |
| 向量 | PASS | PASS | PASS，经 SELECT/vector_search |
| 对象存储 | PASS | PASS | 未提供/未确认对应入口 |
| SonnetMQ | PASS | PASS | 未提供/未确认对应入口 |
| 原生图 Graph Beta | PASS | PASS | PASS，经 GRAPH_TABLE |

因此是原生 API 9/9、重启读回 9/9、MCP 6/9 已验证；其余三种不能算 PASS。向量样本为合成数值，仅证明存取和检索合同，不证明真实 embedding 质量。Graph Beta 成功不等于生产就绪。正常容器重启不等于物理掉电、完整备份恢复或跨版本升级验收。

## 首轮失败与修正

首轮九模型原生 API、重启、MCP 权限/截断/轮换均通过，但六个 MCP 模型的行值比较失败。原因是验收脚本未解码 JsonElementValue（kind/stringValue/integerValue/doubleValue/booleanValue），把带类型单元格与标量直接比较。仅修正验收脚本，不修改 Server 或放宽预期；第二轮在全新独立容器复验通过。首轮失败 JSON 保留，不覆盖历史。

## 连接器清洁包

- JSON 结构、重复键、元信息、英文文案、变量与凭证表单逐一匹配通过。
- Skill frontmatter、工具参数、安全边界及本地引用检查通过。
- SVG XML 与主动内容检查通过。
- 目录白名单和启发式凭证扫描通过；仍须人工安全复核，扫描不能数学证明无秘密。
- ZIP 仅含 connector-meta.json、mcp.json、token-schema.json、icon.svg、skills/sonnetdb-skill/SKILL.md。
- 不包含测试脚本、Docker 配置、数据卷、运行数据、凭证或内部日志。
- 当前 0.1.1 候选包：`sonnetdb-workbuddy-0.1.1-review.zip`，SHA256：`b8f4e68dc398a956791f27e0497a86b767ce2f0acb4b221832458f5dd741f10a`；新包待平台重新解析。
- 历史 0.1.0 包：`sonnetdb-workbuddy-0.1.0-review.zip`，SHA256：`cf8f817ff5caa5b7b29ec3af869d27100c0489529acc038de38483d97a40b4f5`；上传解析已拒绝，仅供追溯，不再上传或送审。

## 未闭合项与送审状态

正式送审状态：NOT_SUBMITTED，未取得提交回执、申请编号或审核结果。

1. 当前连接器缺少 KV、对象存储、MQ 的专用 MCP 入口；九模型原生 API 成功不能替代这三项。若以完整九模型可执行连接器送审，须先实现最小权限、有界工具并重新验收。
2. WorkBuddy 宿主安装、凭证表单、重新连接和 Token 轮换交互尚未测试。
3. 本次仅回环 HTTP；生产 HTTPS、证书和约定审核网络可访问性尚未测试。
4. docs_search、skill_search、skill_load 的 provider 成功/不可用路径，以及取消/超时、全部错误码尚未完成。
5. 部署、升级、备份和恢复仍是 Skill 指导，没有通过当前连接器执行完整运维闭环。
6. 官方文档 https://open.workbuddy.cn/docs/connector 要求将连接器目录打包提交 WorkBuddy 团队审核；https://open.workbuddy.cn/docs/ 说明先完成开发者/企业资质、创建业务、提交审核、审核后发布。公开文档未提供可直接调用的提交 API 或具体上传页面，本次未猜测接口、绕过登录或代填主体资料。

待 0.1.1 平台重新解析通过并完成适用验收后，可选择以明确限定的“只读查询与使用指导”范围提交初版，或先补齐完整九模型 MCP 能力再送审；不能将当前包描述为完整九模型和运维执行闭环。0.1.0 已被解析拒绝，不再作为上传候选。正式提交需要用户可用的开放平台开发者入口及主体信息，并完成上述适用验收。当前 0.1.1 ZIP 是待平台解析的候选审核材料，不是已提交或已批准产品。

## 证据与复现

交付包含第二轮机器报告、实际 tools/list schema、首轮失败报告、白名单审核 ZIP。验收脚本使用 Python 标准库，逐行检查 SQL NDJSON 的终态 end 与错误帧；不以 HTTP 200 代替业务成功。

本地完整 dotnet restore SonnetDB.slnx 及规定 dotnet format 验证流程完成，输出一条工作区加载警告，未报告格式差异；没有执行 git commit/push、打标签或发布。后续如提交代码，必须在最终待提交树重新运行规定检查。
