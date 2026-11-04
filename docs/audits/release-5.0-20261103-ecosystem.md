# SonnetDB 5.0 生态发布核查：FreeSql、SqlSugar 与现有 .NET 集成

核查日期：2026-11-03（北京时间）。本报告区分数据库实现、适配器实现、上游合并、公开包与消费者验收；不能据源码或候选测试宣布所有上游项目已经公开支持 5.0。

## 核查结论

- 冻结 SonnetDB `455bf3b53b1e56df0064d8f574b0595cbb0b609e` 的候选包和正式构建 NativeAOT Server，已通过本轮真实 SqlSugar embedded、REST、frame-http2 消费者，每种 **93/93 个逻辑检查**，0 失败。SqlSugar 适配器需要本轮组合修补，原上游 PR 不能原样标为完整支持。
- FreeSql 的原接入 #2234 已合并，5.0 兼容修正 #2291 仍未合并；公开 Core 仍为 `3.5.311`，`FreeSql.Provider.SonnetDB` 索引仍404。原 237/237 与三协议各25项业务/8项重开的精确候选证据属于 2026-11-02，本轮核对其来源及当前 PR，不冒称重新执行。
- 本轮查询时 SonnetDB/Core/EF 的公开稳定包仍为4.0.0，Protocol/CAP/Testcontainers 索引404；这只是查询时快照，正式发布后须重新回读。本报告不沿用“密钥过期”作为当前发布失败原因；当前流水线诊断由发布审计单独记录。
- `Testcontainers.SonnetDB` 有已发生的保留命名空间 HTTP409 拒绝证据，不能因更新密钥便假定消除。用户后续批准的新包名及新发行候选，须按其新产物分别验证；本报告的消费者结果仍绑定上述旧候选。

XCode/Chloe 由同轮独立报告核验，不将 FreeSql 或 SqlSugar 的结果推导为其它 ORM 的通过。

## 上游 PR 与公开组合

| 项目 | 本轮实际回读 | 适配与交付判断 |
| --- | --- | --- |
| FreeSql 接入 | [dotnetcore/FreeSql#2234](https://github.com/dotnetcore/FreeSql/pull/2234) 已合并，merge `05997ef16f0d6a12f051f9a50794a2c21dcd4dc5` | 已合入源码，不等于当前公开 Core 包包含 SonnetDB 枚举。 |
| FreeSql 5.0 修正 | [#2291](https://github.com/dotnetcore/FreeSql/pull/2291) OPEN、非 Draft，head `a2268c1ff580b77d9ee202b3d34f230be59eab8b`，本账号只有 READ | 候选237项及三协议证据已存在；上游合并、新Core/Provider发包、全公开组合仍待完成。 |
| SqlSugar | [DotNetNext/SqlSugar#1457](https://github.com/DotNetNext/SqlSugar/pull/1457) Draft/OPEN；head repo `IoTSharp/SqlSugar`，branch `master`；本轮从精确 `65e8226ce25a8e1cb9cca2590683f1fd6778cab3` 正常前推到 `18f9df7ede7b575b6caba5f46bcc070a4da95784` | 本轮12文件候选修正及5.0.0依赖已推至现有PR，三协议通过；未将PR转Ready、未上游合并或发包。 |
| ABP/EF JSON | [IoTSharp/SonnetDB.Abp#1](https://github.com/IoTSharp/SonnetDB.Abp/pull/1) 已合并；main 的 `Directory.Packages.props` 仍固定 `4.5.0-preview.ef266.5` | 既有公开预览组合已验，不属于本轮正式5.0组合验收；当前公开EF包尚无5.0。 |
| CAP | SonnetDB自有 `extensions/SonnetDB.CAP`；候选库存已含其5.0包，公开索引查询仍404 | 已有适配实现及CI，须随正式十包公开交付；不把候选库存当公开安装成功。 |
| Distributed/EasyCaching、ADO.NET、EF Core | 已有实现、样例和发行包合同，见 `docs/ecosystem-integration.md` 与现行发布清单 | 按正式5.0包恢复与真实入口回读；不因ORM专项通过扩称上层应用迁移/生产验收。 |
| Hangfire | 现行发行包清单与扩展目录中没有独立 Hangfire Provider | 不作为已发布接入宣传；不为本轮凭空增加一套实现。 |

API搜索返回与本次范围相关的67个Issue，已读取完整100项以内结果；实现类Issue关闭不等于公开适配完成。原始API响应保留于 ignored `artifacts/release-5.0-20261103-ecosystem/`。

## SqlSugar 的最小适配修正

固定官方源码 ZIP SHA256 为 `46615584f90e8e7857a91140130581f9e4c3d7e7ae20b48a9c9f05383633d766`，与历史验收完全一致。只修改12个文件：

1. 组合既有 #245 原生 TEMP CTAS 补丁，保留 Fastest 的集合批量执行、会话隔离、调用方提交/回滚与真实复制失败清理。
2. 组合既有 #247 输出参数补丁：成功关闭原生 Reader 后，向本次冻结的 `SugarParameter` 目标回写；失败、取消、重复关闭和 Provider 复用不重复公开输出。
3. 组合既有 #250 维护补丁：真实主键约束名、自增下一值、主键删除及方向/INCLUDE 元数据；公共 SqlSugar 核心只新增受保护的主键约束名钩子，不模拟删除重建 identity 列。
4. 将 Provider 的 SonnetDB 依赖升级到5.0.0。
5. 修复本轮真实发现的 TIME 绑定错误：运行实体绑定通过 SqlSugar 已有 `TimeSpan` 枚举路径，DbFirst 单独生成 `TimeOnly` 属性。原来把 `TimeOnly` 返回给 `CSharpDataType` 枚举解析会抛错；实际生成模型编译与两种实体读回均重新通过。
6. 同步完整能力用例与README：旧 DATETIME 保留毫秒合同，含亚毫秒参数必须拒绝且不写入；原生 DATETIME2 显式使用 `DbType.DateTime2` 并严格比较全部ticks。未降低精度断言，也未让客户端静默截断。
7. 给生成模型编译进程加120秒期限、PID/启动时间记录和超时清理；允许同一套件通过环境连接字符串运行三种模式，输出实际加载程序集版本与SHA。

候选补丁不包含本机绝对路径、NuGet.Config、包缓存屏障、bin/obj、运行目录或凭据。完整12文件原始/候选SHA、补丁SHA和独立回放校验由 `candidate-summary.json`、`fork-checkout-verification.json` 记录；本地构建与测试证据均保留在ignored目录。上游原有obsolete/nullable警告保留，不以增量构建0warning冒称整个外部工程干净重建0warning。

## 本轮真实执行

| 执行组 | embedded | REST | frame-http2 |
| --- | --- | --- | --- |
| 完整 Provider 公开入口功能/能力套件 | 24/24 | 24/24 | 24/24 |
| 原生 TEMP Fastest（10000行） | 10/10 | 10/10 | 10/10 |
| Reader 输出参数生命周期 | 23/23 | 23/23 | 23/23 |
| 标准存储过程公开入口 | 11/11 | 11/11 | 11/11 |
| 维护接口 | 12/12 | 12/12 | 12/12 |
| 维护数据重新打开 | 5/5 | 5/5 | 5/5 |
| 组合数据重新打开 | 8/8 | 8/8 | 8/8 |
| 每模式逻辑检查合计 | **93/93** | **93/93** | **93/93** |

完整24组套件包含原16组功能与8组能力；它不是24个xUnit测试。三协议共279个逻辑检查，不与历史套件或2行smoke累加为独立测试总数。所有消费者进程返回0，未跳过。

Core/Data/Protocol运行DLL与候选包的对应 `lib/net10.0` 内容逐字节相同；nuspec均为5.0.0并绑定 `455bf3b5`。正式run [41348714535](https://github.com/IoTSharp/SonnetDB/actions/runs/41348714535) 的 Windows NativeAOT Server EXE SHA256为 `b3a2991a7bbc4750c2d75f711e3151d5d7059f04b3bad853308e1481a8f22ca4`，使用共享只读发布产物及独占端口/数据目录运行。

服务端实际记录 REST 376项业务请求和24项重开请求，均为HTTP/1.1；frame-http2模式376项业务请求和24项重开请求均为HTTP/2，其中分别106和14项访问Frame端点。事务、部分写入和元数据仍使用既有HTTP/2 REST端点，因此不能称作全部操作的纯二进制Frame验收。

REST和frame-http2各终止原Server进程，再启动新进程读取同一数据目录，分别通过13项重开检查；终止由本任务持有的确切Process执行，不是物理掉电。4个Server全部退出，根有界执行收据为 `cleanup_complete=true`、0残余子进程。

首次完整原合同测试的两项失败（TIME绑定、旧DATETIME精度假设）和首次专项smoke的9PASS/14FAIL都保留。后者发现git形式Reader补丁在子目录返回0却跳过文件；已改为源根显式`--directory`应用，核新文件存在、关键逻辑和SHA，再重建运行。只采用最终绑定候选DLL的成功矩阵。

## 发布及关闭边界

- 本报告确认冻结候选的数据库能力支持上述SqlSugar需求，以及本地修正后的真实适配可用。它不证明SqlSugar上游PR已合并、Provider包已发布或最终新SHA/公开5.0组合已经验收。
- FreeSql #2291、SqlSugar #1457、ABP正式5.0依赖、SonnetDB正式十包和Server各自保持实际交付状态。不能为了关闭 #230 而把上游404或仍OPEN的PR写成完成。
- 新候选包与公开包就绪后，使用相同脚本和全新数据库复验；候选源码/包/DLL/Server身份必须重新绑定。可复用脚本不等于新候选已执行。
- EF OwnsMany元素路径、JSON ExecuteUpdate、EF Provider NativeAOT，任意主键类型转换、identity声明切换，以及物理掉电/生产负载保持既有显式边界。既有应用迁移、浏览器WebAuthn、全模型/实例MQ恢复与固定硬件验收不由本报告推断。

主监督复核完整12文件diff与279项矩阵后，授权正常提交/推送至用户owned fork的现有PR。最终12个工作文件与运行候选逐字节核对，暂存清单只含12路径（包括新Reader），工作树和暂存区diff check均通过；从原head正常非force推送 `18f9df7ede7b575b6caba5f46bcc070a4da95784`。本地HEAD、`git ls-remote`与GitHub PR head三方一致，owned工作树干净；收据为 `sqlsugar-commit-push.json`。

本轮没有修改SonnetDB共享产品源码或索引，未发包、合并上游PR或外发评论。SqlSugar PR描述和最终Ready状态由主监督统一处理；后续交接与正式发布状态也由主监督统一归档。
