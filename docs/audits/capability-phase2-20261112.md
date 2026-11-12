# 第二阶段能力切片验证（2026-11-12）

基线 `main @ 93cd7807fd727c474ff119e1690b940e40f0ecda`。开始时本地 HEAD、origin/main、gitee/main 及两端实时 `ls-remote` 均为同一 SHA。起始 dirty 只有 HANDOFF.md 末尾博客园台账段；该外来内容保留，不暂存。本轮仅本地代码/验证，没有提交、推送、包发布、部署、生产迁移或对外消息。

## 三个交付切片

| 切片 | 实际交付 | 未覆盖边界 |
| --- | --- | --- |
| T1 | 显式 WAL v2 单 Point envelope、版本双向拒绝、1024 字段/8 MiB、LSN 上界、严格 UTF-8、恢复总预算/取消、损坏尾/unknown 对账；确定性进程强杀两边界。 | Tsdb/WalReplay/catalog/rotation/checkpoint/flush 默认接入、真实半写/满盘/fsync 故障、历史二进制和物理掉电未验收。 |
| KV | 恢复严格回收断言：无 pin 时 CompactAll 返回即旧 SST 全删、物理集合等于当前 Version；有 snapshot pin 时保留旧文件/旧值，释放并维护后回收，冷重开读回。仅测试变化。 | G12 整体未完成；真实旧 reader/writer v3、迁移/回滚全中断矩阵、物理磁盘不足与完整规模未由本片证明。 |
| 全文 | 105 条同语料，embedded、实际 loopback Server HTTP、源码 remote .NET SDK 逐字段对拍；70 过滤命中四种排序、分页/TotalHits/facet、评分元数据/offset/highlight、查询长度预算错误及既有字段上限。 | 真实独立远端与公开包 NOT_RUN；统一序列化字节上限，以及排序/facet/投影/响应发送的端到端期限未闭环。 |

T1 选择整点单记录 CRC，避免 begin/commit 跨记录状态；这是显式格式底座，不改变默认时序语义或把整个 WriteMany batch 原子化。详细接口、格式和降级拒绝见[T1 合同](../design/time-series-atomic-point-wal-v2.md)。保持默认 ms、KEEP_ALL、当前全局 TTL/无 measurement 分区，不提升物理耐久声称。

## 验证与原始收据

原始 JSON 命令、PID、清理结果和 TRX 位于本地忽略目录 `artifacts/capability-phase2-20261112/`。所有主线重型验证串行，后续使用 CPU2、单 MSBuild 节点、禁持久 build server 和 Cordonix deadline/Job 清理。

| 验证 | 本轮状态与范围 | 收据 |
| --- | --- | --- |
| solution restore | PASS | restore.json |
| CrashTests 与 Child restore | PASS，两项目显式恢复 | crash-restore.json |
| 最终 Core 定向 | PASS，35/35；T1 26，KV 9 | core-test-final.json；core-final/core-phase2-final.trx |
| 既有 Core 回归 | PASS，193/193；Wal 命名空间、TsdbWal、KvVersionSet、DocumentFullTextCompleteSearch | core-regression.json；core-regression/core-regression.trx |
| 最终全文三端 | PASS，4/4 | fts-test-final.json；fts-final/fts-final.trx |
| 确定性强杀 | PASS，2/2；写前与完整写入/OS flush 后确认前 | crash-test.json；crash/atomic-point-kill.trx |
| 规定 solution format | PASS；前两轮 IMPORTS/WHITESPACE FAIL 已保留 | format.json、format-final.json、format-final-02.json |
| solution 外新增 Crash/Child 格式 | PASS，分别检查新增父测试及 Child 场景/分发文件 | crash-format.json、crash-child-format.json |
| 全仓测试/NativeAOT 发布/安装/性能基准 | NOT_RUN | 没有以定向测试替代 |
| 真实远端/公开 NuGet 消费/Demo/现场/固定硬件/物理掉电/168h | NOT_RUN | ENV-00～ENV-06 不提升 |

Release 定向构建未出现编译 warning/error。完整 solution build 没有另跑；受影响项目由对应 test 命令构建。193 与 35 有交集，不能相加成不同测试总数。193 包含既有 embedded 10001 语料回归，新三端同语料为 105 条，不能宣称三端都验证过 10000 窗口边界。

## 反驳落实与失败历史

- T1 初次构建 FAIL：缺 SonnetDB.Buffers/Storage.Format using，已修复；子智能体误启动一轮重型测试，主线随后也遇同一构建错误。子智能体 PID 18460、主线 PID 29424 均清理；首轮回执 `t1-first-build-failure.json` 保留。后续 19/19、28/28 是中间版本结果，不代替最终 35/35。
- 规定格式首次 FAIL：WalRecord.cs imports 顺序；第二轮 FAIL：全文测试清理代码的三处 catch 换行。已精确调整，最终 format-final-02.json 为 PASS，只有工作区加载 warning，未降低检查级别。
- 独立反驳发现并修复 LSN 回绕、非法 surrogate 名称编码碰撞和无总预算的 v2 恢复；新增真实行为测试。CRC 有效但 count/重复字段/SeriesId/vector 结构非法时整点拒绝且不越过后继。
- 全文补独立 score 单调/同分 ID 顺序断言；异常清理继续回收客户端、host、deadline、自有数据和 content root，删除前验证绝对父目录/GUID/reparse 边界。消费者间相等不能单独证明排序预期。
- 第一阶段旧 SST 三次 FAIL 的目录仍存在但为空，准确历史根因 UNKNOWN。当前严格无 pin/有 pin 回归 PASS 只证明本轮负载，不倒改旧结论。G3 仍为会话内成功 SST 作业局部字节计量，排除 WAL/manifest/失败/恢复/设备内部写入，不叫完整写放大。
- unknown 注入在完整写入后的 hook 报错，不能代表真实半写 I/O；强杀在两个确定边界，不能代表随机 200 轮、物理掉电或长稳。

## 下一顺序与资源

先评审 T1 独立格式和默认 Tsdb 接入合同，再完成 KV G12 真实旧二进制/故障对账及全文总响应预算。环境资格继续独立。提交/推送、发行、部署和生产迁移等待用户明确授权，不由本地 PASS 推导权限。

运行命令与各进程清理见原始收据；成功/失败命令均使用任务所属进程树清理，没有常驻服务。loopback host 与强杀 child 由测试释放，临时 GUID 数据目录按归属清理。根 HANDOFF 在结束时追加本轮工程入口，原 2143383 字节前缀及外来段保持。最终源文件哈希、TRX 汇总、归属与清理核对保存为本地 `delivery.json`。

结束核对另发现 `extensions/IoTSharp.CoAP.NET` 的四个项目文件、未跟踪测试和发行文档存在范围外改动，见本地 `shared-submodule-state.json`。本轮只读记录并保留，没有纳入拥有文件/源码哈希，也没有 reset、clean、stash 或写入该子模块。两端 main 的结束实时读回仍为起点 SHA，见 `remote-final.json`。
