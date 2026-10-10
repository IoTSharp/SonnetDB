# Preview 1 范围修订 R1：拒绝尚未证明服务端预算的读取

2026-10-10，M47-P03。依据 P01 范围文档末段的修订规则，本修订从候选03 / `4.5.0-preview.1.3` 起生效。候选01、02保持不可变，发布状态仍为 **NOT_READY**。

## 原因与影响

原 P01 纳入了九模型专用读取，但明确要求“Server 资源预算未证路径不放行”。P03 核查发现：Document Find 的高级查询、KV Scan、FullText search、Vector indexes/search-preview、Object list/Range、MQ browse/统计、Graph overview/visualization 的既有 REST 路径，尚未取得统一 30 秒及 4 MiB 物化预算证据。部分接口有行数、元素或 Range 限制；它们不能证明扫描、排序、单值大小及取消边界。Vector 索引列表还会调用 CountVectorRows；原 schema 接口会递归统计数据库目录。不能通过缩小浏览器显示条数来宣称这些路径已受控。

| 动作 | 候选03状态 | 验收边界 |
|---|---|---|
| session / overview / About | 保留 | 同源用户名密码登录、获授权库选择、退出、七模块位置；四个延期模块拒绝直接路由。About 只显示产品和当前范围说明，不触发原主机硬件采集 |
| sql.read | 保留有预算的子集 | 单条 SELECT、EXPLAIN SELECT、SHOW TABLES/MEASUREMENTS、DESCRIBE TABLE/MEASUREMENT；引擎不支持预算的查询形态执行前拒绝。其它 SHOW/DESCRIBE、自由写、批量及控制面 SQL 拒绝 |
| relation.read / measurement.read | 保留 | 复用既有页面与 SQL 读取；各自更小行数上限优先。Preview schema 只投影表和 measurement，资源、列及主键项合计最多1000，并限制估算字节；不加载备份、索引统计或存储目录 |
| relation.insert.one | 保留 | 有效 HTTP 写角色和数据库 Write/Admin；一次批准的一行参数化 INSERT。DEFAULT VALUES、字面量、INSERT SELECT、多行及其它写拒绝 |
| document.read / kv.read / fulltext.read / vector.read / object.read / mq.read / graph.read.beta | **本预览延期** | 九模型选择项保留，显示明确的服务端预算延期原因；专用组件不挂载，专用 REST 读取和元数据在发送边界全部拒绝，包括小 Range。没有把旧模型局部 PASS 迁移成 P03 接受证据 |
| result.window | 保留于当前开放读取 | 当前窗口导出；再次核验权限及上下文，最多4 MiB。延期模型、Graph 文件导出和对象下载不放行 |

自由 SQL 通过引擎的预算准入检查，并不保证任意 SELECT 都受支持。该入口可以访问引擎已支持的有界来源；这不等同于延期模型专用工作台获得验收。SQL 标识符原拼写、字符串键语义、Graph Beta、MQ 的 database + Topic 逻辑身份与实例 `.system/mq` 持久化、单库备份不含实例 MQ 等合同不变。

## 预算解释与凭据边界

共同上限保持 30 秒、一个在途读取、最多1000行、响应/解码后保留载荷/导出各4 MiB、输入64 KiB UTF-8。浏览器逐块拒绝超限响应，SQL 行数和完成标记校验后才能交付；解码估算计入 UTF-16 文本和容器/标量开销。为避免跨页累积，当前上下文的每次响应均保守累计收费，清空并显式重读后重置。此估算和引擎物化预算均不是 CLR/V8 物理堆的硬上限。

Server 单条受支持 SQL 使用累计物化行数10000、物化估算字节4 MiB和30秒截止；输出窗口仍最多1000，关系200、measurement500。重复物化可重复计费，所以复杂查询可能在输出不足1000行时被拒绝。未支持的预算路径不会回退到无预算执行。schema 使用相同超时及有界投影；它不是完整 catalog/备份响应。

现有 UserStore 发放的是可撤销 token，没有过期时间字段。P03 用真实 token 撤销后的401验证“凭据失效”清理，不声称完成不存在的定时过期流程，也不为此新增认证机制。首次安装和登录均通过真实 UI；用于准备隔离数据库的测试管理员凭据不替代这些登录步骤。

所有限制作用于显式 `preview-1` Web profile；默认完整模式保持。匹配 Server 的其它现有 API 能力和授权不被移除，Preview 不是网络安全隔离或多租户沙箱。P04 固定包内 profile/版本、P05 实物匹配/重开/回退、P06～P08 分发与完整放行均未执行。恢复延期动作需要新的预算实现、真实验收和新候选，不能在打包时悄悄开启。
