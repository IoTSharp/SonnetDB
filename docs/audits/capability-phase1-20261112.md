# 第一阶段能力切片验证（2026-11-12）

本记录绑定本轮工作树基线 `main @ d7e3eddbd3e3eba6fcd434d5f66e08ebda512f9c`，记录全文 typed 查询、KV-LSM G3 计量和 M42-H02 准入期限三个代码切片。它不代表提交、推送、公开包、部署或环境资格。

## 交付范围

| 切片 | 结果 |
| --- | --- |
| 全文 typed | Core 新增有界完整过滤入口；embedded、Server `/fulltext/search`、remote .NET SDK 共用；窗口外命中、分页、完整 `TotalHits`/facet；预算不足专用错误且无 partial。跨页按当前视图读取，不声明 snapshot。 |
| KV-LSM G3 | 记录当前打开会话内成功发布的 flush SST 输出、非 trivial compaction 输入/输出累计字节；失败/取消/半写/恢复打开/trivial move/WAL/manifest/设备内部写入排除，重开归零；报告 schema 1.2。 |
| M42-H02 | SQL HTTP/Frame 按数据库 partition 限制排队年龄；默认 30 秒，配置边界 1～300000 毫秒；调用方取消优先，活动 lease 不被排队期限中断，超时复用 overload 终态并记录低基数 outcome。 |

## 当前验证

| 层级 | 命令/范围 | 状态 |
| --- | --- | --- |
| 依赖 | `dotnet restore SonnetDB.slnx --disable-parallel -m:1 -nr:false` | PASS |
| 构建 | Core、Data、Server、Benchmarks Release `--no-restore -m:1 -nr:false /warnaserror` | PASS；各 0 warnings/0 errors |
| Core 回归 | `DocumentFullTextCompleteSearchTests` + `KvLsmIoStatisticsTests` | PASS，10/10 |
| Server 回归 | 全文 HTTP filtered-window、既有 typed metadata、SqlHttpRequestAdmission、ServerOptions | PASS，45/45 |
| Benchmark 回归 | `KvLsmCapacityBenchmarkTests` | PASS，19/19 |
| 格式 | `dotnet format SonnetDB.slnx --verify-no-changes --no-restore --severity warn --exclude extensions/` | PASS；首次发现的新测试 using 顺序已修复后复跑 |
| 外部/环境 | 全仓测试、AOT 发布、真实独立消费者、公开包、Demo/现场、固定硬件、物理掉电、168 小时长稳 | NOT_RUN |

## 未关闭边界

主线集成时，KV 新测试曾三次 FAIL：compaction 后 SST 库存仍为 3，先后未满足立即降到 1 或不超过 2 的断言。最终去掉立即回收断言后 10/10 PASS，只证明累计字节、Clear 与重开边界；它不能证明旧 SST 已及时回收或该清理顺序调整已经解决回收问题，后续须独立复现、验证版本引用生命周期，不能将此项写为已验收。格式检查首次 IMPORTS 失败，修正 using 顺序后重跑 PASS。这些失败不由后续 PASS 抹除。

本次提交只交付上述局部实现。全文同语料 embedded/Server/remote SDK 对拍与端到端预算（含排序、facet 和响应）尚未闭环；G3 完整写放大、G12 与 M42 整包均保持未完成。提交准备阶段的新命令、退出码与远端读回另存于 `artifacts/capability-phase1-20261112/submission/`，不得将前轮产品测试计作本次重跑。

G12 的真实旧 reader/writer 拒绝、迁移/回滚中断、磁盘不足和未知提交对账仍未整体完成；G5/G10/G11 原规模计量与负载仍待做。M42 H01/H03～H07A、进程级资源上限和混合负载尾延迟未由本片证明。全文本轮未做 embedded 与 remote SDK 同语料逐字段 HTTP 对拍，代码路径与独立 Server 最小反例已验证；完整消费者矩阵仍待做。所有 ENV-00～ENV-06 保持 NOT_RUN。

时序 P0-3～P0-5/M50 T01～T03 本轮未实现。独立反驳审查建议：若进入下一阶段，先限定有界单 Point 的 WAL begin/commit marker 与 replay/强杀/截断尾恢复，确认语义与物理耐久/unknown 结果分开；继续保持全局 TTL/无 measurement 分区、KEEP_ALL 重复点和 ms 默认，分区/TTL、FIRST/LAST、us/ns 精度及旧格式拒绝分别冻结并独立迁移。
