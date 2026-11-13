# 当前能力代码阶段验证

阶段起点为 `main @ aa5e28fdfbcd71775562973686ee3cdf2e9af815`，父提交 `93cd7807fd727c474ff119e1690b940e40f0ecda`。2026-11-12 开始时已独立核对本地、GitHub main 和 Gitee main。2026-11-13 接续本轮代码工作；阶段范围与退出条件由[完成合同](../design/capability-code-stage-completion-20261112.md)固定。

本轮只授权本地实现和代码验证。没有提交、推送、发布包、部署、重启、生产迁移或连接线上 Demo/现场。HANDOFF 原有博客园段与 CoAP 子模块外来工作保留；`parity-results` 不合并。

## 当前验收表

**当前能力代码阶段完成**，2026-11-13（Asia/Shanghai）。三条工作线的代码、公共入口、必要完整回归、真实进程故障、兼容拒绝、AOT/trim 与格式门禁均已闭合。交付仍为本地未提交候选；不提升发行或环境资格。

| 工作线/门禁 | 本轮事实 | 状态 |
| --- | --- | --- |
| T1 默认接入 | 公共写入、CreateSeries/控制记录、MemTable 发布、滚动/回放/checkpoint/flush、catalog v2、旧目录/备份/重开；4 个公共 Tsdb 强杀边界 | PASS |
| KV G12 代码验证 | 43 个空间/元数据/WAL/SST 故障用例；迁移 13 个、回滚 8 个持久阶段强杀，全值/版本/TTL/计数/索引与幂等恢复；严格 pin 回收 | PASS |
| 全文 | 完整过滤、排序/facet/投影/序列化/收发共享预算及取消；10003 条同语料、独立排序 oracle 与真实独立 Server/源码消费者 | PASS |
| 完整 Core Legacy | `core-legacy-all-03`：8525/8525，0 failed、0 skipped | PASS |
| 完整 Core LSM | `core-lsm-all-02`：8415/8415，0 failed、0 skipped；仅按 CI 排除 `KvEngine=LegacyOnly` | PASS |
| 完整 Server | `server-all-01`：1630/1630，0 failed、0 skipped | PASS |
| 最终全文/管理合同 | `server-fulltext-final-01`：29/29，包含独立进程、请求期限、发送取消与预算恢复 | PASS |
| 完整 Crash | `crash-all-02`：132/132，0 failed、0 skipped | PASS |
| 最终历史兼容矩阵 | `kv/compat-final-source-20261113/matrix.json`：18 次真实操作，全部 exit0；所有拒绝步骤 `filesUnchanged=true` | PASS |
| AOT/trim 分析 | Core/Protocol/Data、Server、CLI 构建均 0 warning/error；Server 独立读取属性确认两分析器 true、JSON 反射回退 false | PASS |
| 规定 solution format | `format-verify-final-01`；保持原命令、severity warn、exclude extensions；有非致命工作区加载 warning | PASS |
| solution 外格式 | Crash 父/Child、本机全文消费者及历史兼容探针的相应 final format 收据 | PASS |
| 公开新包/外部远端/安装/现场/固定硬件/物理掉电/168 小时 | 与本阶段代码退出独立，不由本机结果替代 | NOT_RUN |

原始命令、PID/创建时间/命令行/父进程、输出、TRX 和 Cordonix 进程树回收保存在忽略目录 `artifacts/capability-code-stage-20261112/`。306 定向、8525/8415 两引擎、1630 Server、29 最终全文及 132 Crash 之间存在重叠，不能相加为独立测试总数。上一阶段的 35/193/4/2 项不作为本轮重跑。完整 solution 的其它测试项目、NativeAOT 发布/运行/安装、性能原规模均 NOT_RUN。

## 格式和故障合同

T1 的[持久合同](../design/time-series-atomic-point-wal-v2.md)维持默认 Unix ms、KEEP_ALL、全局 TTL、无 measurement 分区和原 fsync 配置。一个 Point 的全部字段共享一条 WAL 记录及 LSN，整个 WriteMany 不因而原子化。旧 v1 非空 WAL 字节不重编码；新 catalog v2 即使在 WAL 回收后仍阻止旧端。进程强杀不证明介质刷新或物理掉电耐久。

KV 新写 MANIFEST 版本为 2，16 字节头及记录布局保留，只升级版本和头 CRC。当前读取器接受 v1/v2，打开旧 v1 时在返回前写出 v2；新建、滚动和固定 Version 备份都使用 v2。真实 `f94a162ed66749ffc76bf8e85d900798525c281d` 历史 Core 的 reader/writer 不接受 MANIFEST v2，shared SST v3 也由旧 reader 明确拒绝。回滚仍恢复保留的 legacy 数据，不删除失败历史。

全文预算不足在发送成功响应前返回明确错误；请求体读取耗时计入有效总期限；已开始发送后的取消或期限使传输中止，不追加一个可消费的成功 partial。分页继续读取当前视图，TotalHits 和 facet 基于完整过滤命中，保持原 continuation 合同，不声明跨页快照。成功/错误响应均支持最坏每次 1 字节短读，受总字节/工作量/期限约束。

## 历史端身份与已确认修复

- 历史源码从本地确切提交 `f94a162ed66749ffc76bf8e85d900798525c281d` 原样归档并构建；旧 Core SHA-256 为 `2b77b1c26a794a099a6460b285be5937ef01dc676f006b8c5672885bebbcfe4f`，程序集版本 `0.0.0-dev+f94a162ed66749ffc76bf8e85d900798525c281d`。不是将当前读取器修改常量伪装成旧端。
- 初次真实旧公共 reader/writer 可继续读写迁移后的独立 LSM 目录：旧端见 CURRENT 后绕过 generation v3，独立 SST 仍为 v1。保留 `standalone-initial-reject-directory-read/write.json` 的真实 FAIL，修复使用 MANIFEST v2；不能仅选择会拒绝的旧发行端来掩盖该问题。
- 初次修复矩阵在 `kv/compat-final-manifest2-20261113/matrix.json`；最终矩阵在 `kv/compat-final-source-20261113/`，逐操作读回旧/新程序集 SHA、退出码与 `filesUnchanged`。最终候选 Core SHA-256 为 `c8decbbd3d57f1e9595582c31093dca0728cf64cba2010baa8ac1cf2cdd8123c`，与探针加载的复制 DLL 完全一致。18 次包含 seed、升级和读回，不称 18 个独立单元测试。`reject-sst-replace` 的 CreateNew 拒绝只证明不覆盖既有文件，不代替公共旧 writer 的格式拒绝。
- 第一阶段三次旧 SST FAIL 的原目录为空，准确历史根因仍 UNKNOWN。当前无 pin 立即回收、有 snapshot pin 保留、释放后维护回收及冷重开结果不倒改旧结论。G3 仍是当前会话成功发布 SST 的局部字节计量，不叫完整物理写放大。

## 首次失败与独立反驳

保留所有原始失败，不通过改写旧收据或放宽断言收尾：

- 首次历史 probe 因 restore 与 build 使用不同 `--artifacts-path` 而缺 assets；初次历史 Core 还继承了外层仓库 revision。已在相同路径还原并显式绑定正确 SourceRevisionId，独立读回正确程序集版本和 SHA。
- 首次 Core 编译缺 `FieldType` 的命名空间；第二次 Server 构建遇到并发修改的预算 overload 签名。修复后冻结生产签名再串行构建。
- `core-focused-01` 为 72/73：旧 posting “已尝试次数”少计一次。保留原断言，恢复拒绝访问也计入尝试次数的合同。
- `core-focused-02` 为 297/303：四项新 manifest 测试的引擎/文件共享夹具不正确，两项 T1 测试误读活动 WAL 或误断言关闭操作应抛错。修复夹具，同时保留 unknown 后写入及空 flush 必须拒绝、无新 checkpoint、完整重开对账的严格断言。`core-focused-03` 为 306/306。
- 完整相关 Legacy 首轮 1641/1647：六项旧 fixture 仍假设默认 v1 或 catalog 尚不存在。按真实 catalog/有效可查询 Segment/v2 WAL 重造，保留并加强原恢复/LSN gap/数据保留断言；最终完整 Legacy 8525/8525。
- 完整 LSM 首轮 8412/8413：维护并发中 checkpoint `File.Move(overwrite)` 返回 Access denied。确定性负控确认持有读句柄时该替换失败；只加 Delete 共享后的 76/77 仍 FAIL。真实 .NET 文件操作矩阵证明 `File.Replace` 配合 Delete 共享可替换，旧句柄保持旧完整内容、新路径为新完整内容。最终已有 checkpoint 使用 Replace、首次发布用 Move，64B 复制后释放读句柄；未添加重试。78/78 定向、最终 LSM 8415/8415、最终 Legacy 8525/8525 均 PASS。**原压力失败的精确句柄持有者仍 UNKNOWN**，不倒改原失败归因。
- 完整 Crash 首轮 131/132：一项旧 fixture 用 v1 reader 读取默认 v2，且在 Dispose 后寻找已回收的写入 WAL。改为保留真实未 checkpoint 整点再追加截断尾，仍核完整恢复；最终完整 Crash 132/132。测试名中的 `power_loss` 不代表实际操作电源。
- Server 初次全局 AOT 参数下传给 netstandard2 构建期生成器触发 NETSDK1210，原 FAIL 保留。按项目既有配置读取 Server 两分析器均 true 后重新构建 PASS，未修改外来项目、未压制 IL 警告。
- 格式首轮 FAIL 只修本任务 5 个文件的缩进/imports；Child 一处属性换行另保留 FAIL 后修正并 PASS。没有降低 severity 或改断言。
- 独立反驳的 catalog v1/v2 fallback、升级目标冲突、catalog 缺失、WAL 发现预算已落实；请求/错误体、fingerprint、工作量、单输出字符串、碎片流、发送 Flush/Abort、确定性取消及总期限已在最终 Core/Server 套件闭合。

## Git、身份和交接

main 本地/双端最终读回仍为 `aa5e28fdfbcd71775562973686ee3cdf2e9af815`，见 `final-refs.json`；没有 stage/commit/push。parity-results 保留本地与双端各自独立引用。CoAP 子模块为外来任务，在本轮期间从原 gitlink 状态推进到 `8da9eda76817c18f322ca654f71f435ce87d702a`，当前子模块干净；本任务未改其字节或暂存状态，也未暂存主仓库 gitlink。

源码、程序集、最终验收和资源回收入口为 `delivery-source.json/zip`、`delivery-final.json` 与 `cleanup-final.json`；根 HANDOFF 以原字节前缀追加工程交接。必要源码归档、旧二进制、原始失败及最终收据保留，任务专用脚本和已无必要的展开材料按归属清理。提交/推送、发行及环境操作等待用户另行明确授权。
