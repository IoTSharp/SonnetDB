# 九模型 MCP 读写工具复核（2026-11-04）

本切片基于 `main` 的 `78c2c74699e7c8231e6228b878fbaa4bf345f27a`，分支 `codex/mcp-nine-models-20261104`。原有九个只读工具保留；新增34个工具，当前共43个，其中28个只读、15个写入。服务端真实 `tools/list`、输入/输出 schema 和权限是依据，旧连接器报告的九工具快照不代表新接口已部署。

## 实现与权限

- KV、桶/对象、文档、实例 MQ 和原生图增加发现、读取与明确的写工具；关系表/时序通过独立 `execute_sql` 数据面入口写入，全文/向量通过文档和原生索引维护写入，专用搜索返回文档 ID。
- 新写工具逐调用计算绑定数据库 Write grant。只读角色和真实普通用户降权都会被拒绝；`query_sql` 不变为写入口。控制面、多语句、事务、RETURNING、INSERT SELECT 和带 Modbus 绑定的建表不开放。
- 新工具的 MCP 取消令牌与 HTTP 取消令牌共同参与15秒截止令牌；原生同步路径的抢占限制在使用文档明确说明。写入开始后的取消/I/O 故障保留 `write_outcome_unknown`，不自动重试。
- Base64单值64 KiB、向量4096维、列表1000条和每份成功负载256 KiB预算；对象支持有界范围与版本读取，文档/对象/名称分页保留真实游标。JSON DTO 全部注册 ServerJsonContext，未添加运行时依赖、unsafe 或反射序列化。
- MQ 复用 REST 的数据库限定主题合同，过滤其它库与未限定历史主题；`read_mq` 不创建组、不推进 offset。存储仍在实例 `.system/mq`，不是单库备份范围。

## 已运行的验证

独立审计曾发现预算滞后：先取limit+1整页、Graph先全量SerializeToUtf8Bytes、响应先全量编码再判断。此问题已经修复，新增按调用预算的Core API、Graph字段/数组预检和固定容量writer；原有Core默认入口未收紧。预算修复后Core读取/快照/文档预算回归89/89、MCP/认证/图SQL/View授权回归118/118通过，无失败或跳过。冷4 MiB存量值拒绝时物理读取为0、分配低于512 KiB；冷大页只读取实际返回的value，返回游标后遇超大记录明确失败。真实MCP另覆盖热/冷KV与文档、32 KiB大页续读、Graph六组负边界、64 KiB二进制正边界及数字枚举属性正例。前次Graph负测试被客户端枚举字符串转换提前拒绝，现按生产context生成JsonElement，确认到达预算工具路径。

预算修复后重新发布Windows x64 NativeAOT并运行实物：43工具发现、36项调用/负边界检查全部PASS，包括64 KiB二进制、按字节截页后续读、2 MiB存量value拒绝、Graph数组预检且未写入、只读拒绝且原值不变。新实物SHA256为 `440cdfee0c68ffa2609723da960aa3c4b9ba7fbe136ccee199f0771e37f6ca57`；产物在 `aot-budget/`，报告 `aot-budget-smoke.json`，旧产物保留。发布前记录并在运行后重新核对13个生产源码SHA256完全不变，清单为 `budget-source-inputs.json`。本次Server进程57192已退出、临时数据目录确认删除、owned Job清理完成。

以下47/106与最初NativeAOT记录属于预算修复前的证据，保留但不能替代上述新源码/实物验收。最终提交仍须取得本树完整restore和CI同等Format通过。

- Server Release build，`/warnaserror`：0警告、0错误。
- MCP 定向完整组：47/47通过，0失败、0跳过，包含真实 Kestrel + 官方 MCP client 的九模型写读、对象范围/游标、二进制/TTL/版本、MQ跨库过滤/只读查看不建组、图元素版本/邻接/删除、数据面SQL拒绝控制面/批处理/RETURNING、15个写工具只读拒绝及真实用户撤销Write后改授Read立即生效。
- 增加全文/向量索引发现断言并修正格式后，扩大到 MCP、AuthControlPlaneEndToEnd、GraphSqlAuthorization 与 SqlViewAuthorization，106/106通过，0失败、0跳过；回执 `mcp-models-regression.trx`。
- 首两轮测试的失败来自新增测试夹具：内联主键语法改为表级主键；权限降级测试改为先Revoke再Grant Read（Grant原生合同只保留最高权限）。修复后的47项实际通过；原TRX保留，不修改生产SQL或授权语义来迎合夹具。
- Windows x64 NativeAOT publish，使用CI规定的 `-p:SonnetDbPublishAot=true /warnaserror`：退出0，无IL/AOT告警。中途NuGet下载EOF后由还原正常恢复。最初两次本机调用因缺少RID还原、误用全局PublishAot失败，记录保留；未修改发布工程或压制告警。
- 启动实际NativeAOT Server，43个工具发现及25项调用/拒绝检查通过，覆盖九模型读写、全文/向量搜索、只读用户写入拒绝与原值不变。二进制SHA256为 `6b0898e2cb1b79d5b4bfacaec59da7564a372bf4373c7d23aa03dac5ca76477a`。Server进程43708已退出，临时数据目录确认删除，Cordonix owned Job清理完成。报告为 `aot-smoke-native.json`。
- 首次NativeAOT启动smoke错误地只用 `--urls`，被发布包内Kestrel配置覆盖，探测端口未监听而失败。脚本现显式将Http和FrameH2端点绑定到独立回环端口；前次Server已退出、临时目录已删除，原失败 `aot-smoke.json` 与启动日志保留。该失败不计作MCP产品故障，也不覆盖为PASS。

原始回执保存在任务工作树 `artifacts/mcp-models-20261104/`。提交前在最终树执行完整solution restore与CI同等Format；本切片不合并main、不移动标签或发包。统一发布聊天将整合后冻结新SHA，再运行全部发布门禁。

## 使用与边界

工具名称、权限、分页和示例见[工具文档](../mcp-model-tools.md)与[兼容合同](../mcp-contract.md)。本切片归M51，沿用已有M27 typed contract；这不是原生管理API全覆盖、事务SDK、Microsoft Agent Framework、真实embedding质量、固定硬件长稳或生产部署验收。WorkBuddy宿主安装、市场审核和正式Server/连接器发布仍独立验收。

WorkBuddy端口归属修复独立提交为 `57deb851140affc192989cc635bb462b607144fd`，分支 `codex/workbuddy-port-review-20261104`，不混入本切片。PR #390标题/描述已填充并标明该修复待统一整合。
