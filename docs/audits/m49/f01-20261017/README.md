# M49-F01：关系 SQL 自动提交 Frame 切片

日期：2026-10-17（Asia/Shanghai）。实施分支 `codex/m49-connector-batches-roadmap`，父基线 `39437db0189e9f8acbc3efebf890087ed17052f6`。本次为可独立交付切片，F01/M49 均未整体完成。

## 实际行为

新 `SonnetDB.Client.ExecuteSqlAsync` 通过真实 HTTP/2 Frame 调用产品 Server。`Sql/Execute=2` 使用独立 payload v1、精确二进制参数与类型化 NULL、累计 4 MiB 输入预算、单帧 committed/影响行数终态。支持关系表 CREATE/DROP TABLE 和无 RETURNING 的 INSERT/UPDATE/DELETE；复用 Write 权限、SQL admission、AST 参数绑定及现有引擎语句原子性。

Decimal 四个 Int32 保留 scale/符号；原生 DateTimeOffset/Duration 保留完整 ticks。既有 DATETIME 只存毫秒，超精度 DateTime 参数执行前明确拒绝，避免静默截断。协议表、调用例和全部拒绝边界见 [SQL Execute](../../../sql-execute-frame.md)。

## 验证与身份

[`acceptance.json`](acceptance.json) 保存最终计数、原 TRX/GZip 哈希、生产/测试源码与实际 DLL/PDB 哈希、执行命令/时间/退出及清理记录。GZip 解压后必须与记录的原 TRX SHA256 相同；不要把新文件时间当成另一次运行。

| 运行 | 验证范围 |
|---|---|
| client-01 | 新 Client 全项目，包括依赖隔离、HTTP/2、期限/取消/关闭、参数精度/NULL/畸形输入、终态缺失；含原有 D01 回归 |
| server-final-02 | 新 Client 直连真实产品 Server；精确值/完整宿主重开、位置参数/注入文本、权限/Preview/HTTP1拒绝、约束整句回滚、能力/版本/精度拒绝、提交后断响应无重试；同时覆盖原 Frame/pipe 生命周期与旧 ADO 完整类 |
| server-after-format | 仅两处测试初始化器格式修正后，重新构建并复验新真实服务测试类；扩大窗口的旧测试源码与全部生产源码保持不变 |
| core-final | 既有 Span IO、FrameHeader 与 SQL Frame codec 定向回归 |
| native-final | Native 托管 SQL 生命周期回归；不代表新 NativeAOT/C 构建 |

本片在受限 CPU/禁用共享编译器的 Windows Job 中运行，有明确墙钟/轮数、取消文件与自有子树清理。完整原始 spec/result/stdout/stderr/进程身份在旧机 `artifacts/m49-f01-20261017`；换机不依赖这些绝对路径，按根交接中的工程命令重新验证。

## 失败记录与修正

- 中间尝试直接切换旧 ADO 的写入通道，153 项有 31 失败，包括已有 RETURNING 功能回归及原 REST/版本断言。已撤出该范围，旧 RemoteConnectionImpl、FrameChannel 和 ADO 测试与父基线完全一致；最终独立回归按 acceptance 的分组计数确认。未通过删测试、降门禁或把已有功能改成拒绝来收口。
- `server-01`：1/16 通过；CREATE TABLE 返回 TableSchema，初版影响行数选择器未处理。修正为受支持 DDL 返回零；原失败 TRX 保留。
- `server-02`：17/18 通过；测试读取已有 Query v6 时漏传原生日期/时长/偏移解码标记。仅修测试 helper，旧 Query codec 未修改。
- `server-03`：17/18 通过；旧 Query v6 的 TIME 本来就是保留七位小数的字符串。按其实际合同断言，未修改生产输出。
- `server-final`：254/255 通过；重开暴露既有 DATETIME 的毫秒精度。新增写入前精度拒绝和独立负向测试，DATETIMEOFFSET 的完整 ticks 正向继续保留。
- 首次完整 Format Check 只报新测试两处初始化器空白格式。修正前后去空白内容相同，扩大窗口的测试源码/DLL/PDB 原哈希单列保存；格式后独立复验新类，并重新完整 restore/原样 Format。没有改动生产逻辑或旧测试。

失败 TRX 与最终 TRX 分开入库，不覆盖原记录。更早的 CLI 编译/夹具失败只存在当前聊天工具输出，不伪造其 TRX。

## 未验证与下一步

旧混合 Data/ADO 的 REST/session 仍是迁移缺口，新 Client 无任何此类回落。先补 SQL Frame 多结果/RETURNING 与适用事务，再迁移 ADO，保持现有功能。新 Client 尚无 SQL 多帧查询接口；其它模型、Core pull、请求执行总预算、性能/固定硬件、TLS/ALPN、其它平台/语言、NativeAOT 发布运行、正式包消费者、远端 CI 和公开发行均不由本片宣布完成。

源码构建继续开启现有 AOT/trim 分析与警告铁律，没有更改 JsonSerializer 或包兼容门禁。提交前必须在最终源码完整 restore 和原样 Format Check；工具的 workspace-load warning 单列，不能冒称完全没有工具警告。最终 SHA 及远端回读在提交后确认，不用父 SHA 冒充本片交付。
