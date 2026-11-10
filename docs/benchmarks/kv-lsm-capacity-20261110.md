# KV-LSM 本机容量基线与迁移恢复证据

本报告绑定 `main` 基线 `a037b01f` 与本轮未提交改动。测量面向开发机上的 Legacy/LSM 对照；报告中的 PASS 表示所选语料、校验和运行完成，不代表设计 §9 的所有门禁通过。固定硬件、一亿条及 168 小时长稳单独验收。

## 当前执行范围

- `KvLsmCapacityBenchmark` 提供 `kv`、`tables`、`migration`、`memory` 四种独立进程场景。
- KV：顺序或随机写入、首遍／热读、正反向 seek + scan100、写后快照。每个语料全量对拍键序与完整 value；测量后的断言不计入请求延迟。
- 表：一张热表与显式数量的冷表，对照无写者与并发写者时冷表点查，记录热表吞吐、冷热表计数和文件数。默认 1000 张冷表、每表 10 行属于缩减语料；设计规模须显式指定每表 10000 行。
- 迁移：生成 Legacy 数据后以 LSM 首次打开，核对引擎、sequence、generation，迁移后与再次重开后都全量读回。记录每逻辑 GiB 耗时和阶段采样磁盘占用；逻辑 GiB = 条目数 ×（16 字节 key + value 字节数），不是源目录物理 GiB。
- 真实强杀：7 个独立 KV 阶段与 6 个共享表阶段；子进程落盘 ready 后由父进程 Kill(true)，不执行子进程的正常 Dispose；父进程核对非零退出、阶段身份和两次重开。
- 内存：`memory` 场景填充、checkpoint、全量读回后静置 10 秒，采样 100 次；最后 5 秒的 RSS 极差不超过 max(4 MiB, 均值的 5%) 时记录观测稳定值，否则该稳定值保持空。此判定只描述当前窗口。

## 运行与复现

先在最终源码上 restore/build；每组结果使用独立进程与新输出文件。命令示例（PowerShell 7，仓库根目录）：

```powershell
dotnet restore SonnetDB.slnx
dotnet restore tests/SonnetDB.CrashTests/Child/SonnetDB.CrashTests.Child.csproj
dotnet build tests/SonnetDB.Benchmarks/SonnetDB.Benchmarks.csproj -c Release --no-restore -m:1 -p:UseSharedCompilation=false
$env:DOTNET_PROCESSOR_COUNT = '2'
dotnet tests/SonnetDB.Benchmarks/bin/Release/net10.0/SonnetDB.Benchmarks.dll --kv-lsm-capacity --scenario kv --engine Lsm --entries 100000 --value-bytes 100 --samples 200 --order random --sync-wal false --seconds 300 --source a037b01f+local-kv-lsm-evidence --output artifacts/kv-lsm-capacity-rerun.json
dotnet test tests/SonnetDB.CrashTests/SonnetDB.CrashTests.csproj -c Release --filter FullyQualifiedName~KvLsmMigration
```

`--scenario tables` 使用 `--cold-tables` 与 `--rows-per-table`；`--entries` 是热表写入最大次数。`--scenario migration` 要求 `--engine Lsm`，源数据始终由 Legacy 生成；`--scenario memory` 测量静置 RSS 窗口。`--order sequential|random`、`--value-bytes 8..65536`、`--sync-wal true|false` 与随机种子全部写入 JSON。默认样本数 200、超时 120 秒、RSS 预算 2048 MiB。参数校验拒绝缺失来源／输出、非法规模及重复参数，已有报告不会被覆盖。

每个循环同时受数据规模与取消／墙钟预算约束；执行器另设进程级硬超时，记录 PID、创建时间、父进程、命令行并回收所属子进程树。取消或超时的不完整结果记 UNKNOWN，存储／断言失败记 FAIL。数据只写入带唯一所有权标记的临时目录；正常结束（包括捕获的失败、取消）回收该目录。外层强杀若留下不完整报告或临时目录，按运行 ID 对账后清理，不能盲目重跑覆盖。

## 测量口径与限制

| 指标 | 实际口径 |
|---|---|
| 写入吞吐 | 单条 Put，包含编码与预算检查；SyncWal=false 是缓冲 WAL，不代表逐次 fsync 的持久写吞吐 |
| 点查／扫描延迟 | Stopwatch 请求级样本，P50/P99 为 nearest-rank；完整 value／范围结果校验在计时外 |
| 快照成本 | checkpoint 后预写 min(N,10000) 次，再逐次写入后测 AcquireReadSnapshot；点查与释放在计时外 |
| 分配 | 写入为进程总分配增量；请求样本为测量线程分配；并发热写分配不计入冷读线程值 |
| 首遍读取 | 未清空操作系统页缓存或进程共享块缓存，不能称为真正冷缓存 |
| 内存 | 原始三场景为 checkpoint 后 1 秒窗口；memory 场景为 10 秒窗口和上述稳定性判定。均记录进程峰值，不能替代千万条长期稳态 G7 |
| 迁移耗时 | 包含迁移阶段磁盘目录采样；最大观测磁盘占用不是连续采样得到的绝对峰值 |
| 多表并发 | 记录热写实际次数和是否达到迭代上限；提前达到上限的结果不能作为持续热写干扰门禁 |
| 磁盘字节 | 仅统计本任务数据目录，最多 100000 文件、深度 8；并发 compaction 导致消失的文件另记数量 |

写放大、真实冷缓存读放大、完整 G3/G6/G7/G10/G11、随机强杀至少 200 轮、真实旧版本二进制和固定硬件门禁均不由这些本机运行自动通过。多表单批次尚未实现，不能声称每事务仅一次 fsync。

## 证据与结果

原始命令、进程收据、TRX 与基准 JSON 位于 `artifacts/kv-lsm-evidence-20261110/`。每份 JSON 自带版本声明、实际 Core 程序集版本、机器／CPU／磁盘／.NET、命令、语料、限额与清理状态；本轮准确文件 SHA 清单在最终汇总时生成。

| 检查 | 状态 | 证据与边界 |
|---|---|---|
| KV-LSM 基线 ca495cae 远端 CI | PASS | [CI 42754412252](https://github.com/IoTSharp/SonnetDB/actions/runs/42754412252)：Linux/Windows Core LSM 与三个 AOT Publish 作业成功；不外推到本轮未提交源码 |
| 新增迁移真实强杀 | PASS | `crash-tests/kv-migration-02.trx`，13/13，0 失败、0 跳过 |
| 基准小语料回归 | PASS | 首批 `benchmark-tests/kv-capacity-01.trx`：18/18；memory 场景及最终完整回归单独追加 |
| 10 万／100 万条基线 | PASS | 16 组独立进程测量完成，全部语料断言通过，所属数据目录清理完成；不等于完整设计门禁通过 |
| 内存与预热随机冷读补测 | PASS | 4 组十秒内存窗口与 2 组冷热表测量；合计 22 组完成，见下文 |
| 最终格式检查 | PASS | `dotnet format SonnetDB.slnx --verify-no-changes --no-restore --severity warn --exclude extensions/`，退出 0，见 `format-final.json` |
| 完整 1000 表 × 1 万行、千万条及固定硬件 | NOT_RUN | 本轮授权为有界本机基线 |

首次 CrashTests 构建失败来自 Child 子项目的旧 assets：EF Core 引用缺失、System.IO.Hashing/Tensors 版本冲突。solution restore 通过后，显式刷新 Child restore，再运行 13 项强杀测试通过；父项目的旧 assets 警告通过显式 restore 继续消除，初始失败日志保留。CrashTests 项目未列入根 solution，单独执行其 restore/build/test。

16 组原始测量使用报告 schema 1；准确源码保存在 `measured-source-v1.zip`（SHA-256 `747389d307a390f441b02c3d0db2b46ed36064b33bf46d1cc95d1273fecbc3a4`），源码与实际 DLL 哈希在 `source-before-baselines.json`。后续 schema 1.1 增加运行开始时的 UNKNOWN 目录记录及独立 memory 场景；原始测量不改写，后续源码身份与内存结果分别保存。

后续顺序：按实测结果定位性能与容量瓶颈，再独立完成 K7 多表事务／元数据／计数／回滚 CLI。发布版本与生产迁移另行授权。

## 本机数值（单轮，不作为生产承诺）

机器：Intel Core Ultra 9 185H（16 核／22 逻辑处理器），约 64 GiB RAM，NVMe PC SN8000S WD 2048GB；每个测量进程设置 `DOTNET_PROCESSOR_COUNT=2`。基准 JSON 记录操作系统与 .NET 的准确版本。所有下列随机写均为 `SyncWal=false`，样本 200，16 字节 key，固定种子 42。

| 引擎 | 条目数 | value | 写入 ops/s | 热点查 P99 µs | 快照 P99 µs | 正向 scan100 P99 µs | 反向 scan100 P99 µs | 进程峰值 MiB |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| Legacy | 100000 | 100 B | 65814.3 | 30.9 | 6138.1 | 457.7 | 342.5 | 102.5 |
| Lsm | 100000 | 100 B | 52318.5 | 17.5 | 5.1 | 230.8 | 409 | 85.6 |
| Legacy | 100000 | 1024 B | 57484.3 | 62.2 | 5335.7 | 1519.8 | 1394.9 | 182.2 |
| Lsm | 100000 | 1024 B | 44359.8 | 20.5 | 4 | 1590.9 | 2518.4 | 200.9 |
| Legacy | 1000000 | 100 B | 30896.5 | 26.3 | 7558.9 | 1127.6 | 508.9 | 179.8 |
| Lsm | 1000000 | 100 B | 57822.2 | 16.3 | 8.4 | 1212.1 | 1032.9 | 212.5 |
| Legacy | 1000000 | 1024 B | 15168.8 | 27.8 | 4528.5 | 1389.4 | 1441.6 | 526.5 |
| Lsm | 1000000 | 1024 B | 42481.4 | 21.5 | 4.5 | 3585.1 | 3580.3 | 248.9 |

百万条／100 B 的 LSM 随机写吞吐约为 Legacy 的 1.87 倍；百万条／1 KiB 为 2.80 倍。快照尾延迟明显降低，但 scan100 的 P99 有退化：百万条／1 KiB 的 LSM 正反向分别 3585.1／3580.3 µs，Legacy 为 1389.4／1441.6 µs。当前只是一轮受限样本，应先增加重复轮与剖析再决定优化方向。

逐次 fsync 的顺序写（10 万条、100 B）单独测得 Legacy 2219.4 ops/s、LSM 1714.1 ops/s；本轮 LSM 更慢。不能用缓冲 WAL 吞吐替代持久写吞吐，也不能据此宣称全部性能门禁通过。

| 迁移条目数 | value | 首次打开迁移秒数 | 秒／逻辑 GiB | 最大观测额外磁盘 MiB | 完整读回次数 |
|---|---:|---:|---:|---:|---:|
| 100000 | 100 B | 0.737 | 68.207 | 11.8 | 2 |
| 100000 | 1024 B | 1.081 | 11.161 | 100.2 | 2 |
| 1000000 | 100 B | 9.766 | 90.4 | 117.6 | 2 |
| 1000000 | 1024 B | 9.239 | 9.538 | 1002.2 | 2 |

迁移数据均在迁移后及再次重开后逐条对拍；耗时不包含源库生成。小 value 的每 GiB 包含更多条目，因此不能将不同 value 大小的每 GiB 数值直接当作磁盘带宽对比。

## 内存窗口与冷热表补测

以下六组使用 schema 1.1；源码与实际 DLL 哈希见 `source-before-memory.json`。四组百万条语料都完成了十秒静置观察，并满足最后五秒极差 ≤ max(4 MiB, 均值的 5%) 的本机窗口判定。该结果不替代 G7 的千万条规模或长期混合负载。

| 引擎 | value | 观测稳定 RSS MiB | 进程峰值 MiB | 窗口判定 |
|---|---:|---:|---:|---|
| Legacy | 100 B | 176 | 201.2 | PASS |
| Lsm | 100 B | 197.7 | 221.6 | PASS |
| Legacy | 1024 B | 473 | 521.6 | PASS |
| Lsm | 1024 B | 183.8 | 257.1 | PASS |

冷热表补测为 1 张热表 + 1000 张冷表，每表 10 行，100 B payload；使用固定种子的随机表／行序列，两个测量阶段前已对该序列预热。2000 次读样本期间热写未达到 100000 次迭代上限。早期未预热、轮询读的两份 schema 1 结果保留为原始试跑，不用于下表的干扰比较。

| 引擎 | 无热写冷表 P99 µs | 有热写冷表 P99 µs | 热表写 ops/s | 热写次数 | 文件数 | 进程峰值 MiB |
|---|---:|---:|---:|---:|---:|---:|
| Legacy | 51.2 | 46.9 | 17040.1 | 1119 | 5015 | 156.9 |
| Lsm | 53.5 | 43.6 | 19482 | 1921 | 4022 | 85.2 |

这两组未出现冷表尾延迟放大，但观察窗口很短，且冷表只有 10 行。不能据此关闭完整 G10；1000 表 × 10000 行及多表事务每次 fsync = 1 仍未验证。

## 最终验证边界与后续项

- Legacy KV：414/414 PASS；LSM KV 首轮 320/321，`RandomWorkloadWithReopensAndHeldSnapshots_MatchesModel(seed:12, backgroundFlush:false)` 在 `KvVersionSet.WriteNewManifestLocked` 改名 CURRENT 时遇到 Windows Access denied，原始 FAIL 保留。随后该方法 3/3、完整 LSM KV 321/321 PASS；偶发原因仍 UNKNOWN，未修改该路径、未加重试或跳过。
- 完整 Benchmarks.Tests：84/84 PASS；最后补充的随机冷读预热／内容对拍与迁移钩子目录隔离，再由最终定向回归 19/19 PASS 核验。
- 迁移真实强杀最终 13/13 PASS。额外完整 CrashTests 超过 360 秒执行预算，外层执行器 380 秒硬超时回收所属进程树；未生成完整 TRX，记 UNKNOWN，不能声称整套 CrashTests 通过。
- 超时窗口内发现 `C:\Users\mysti\AppData\Local\Temp\sndb-crash-g3cbpi1v.lbu`，其中进程记录指向 `crash_kill9_mid_compaction`；缺少存活父进程链供完整归属复核，保留目录并在 `temporary-root-audit.json` 记录，不扩大清理。22 份基准报告均为 PASS、CleanupComplete=true，数据目录已独立核对不存在。
- 最终规定格式只读门禁 PASS，收据 `format-final.json`。提交、推送、tag、发布、部署均未执行。

后续应优先调查 Windows CURRENT 改名偶发、完整 CrashTests 的时间／输出边界，再对 fsync 写入与范围扫描 P99 做重复采样和剖析；K7 功能、发布和固定硬件工作仍独立推进。
