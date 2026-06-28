# SonnetDB Parity Roadmap — 多模能力对齐路线图

> SonnetDB 的目标是让物联网 / 工业 / 边缘场景下，**一台 SonnetDB 替掉一组开源组件的组合**（PostgreSQL + Redis + InfluxDB + MinIO + NATS + Meilisearch + Qdrant + ClickHouse）。本路线图规定如何对这一目标做"可证伪"的对齐验证。

## 设计前提

1. **不做协议兼容**。SonnetDB 不实现 SigV4 / MQTT 3.1.1 / RESP / Postgres wire / Kafka wire。我们走自己的 `SndbConnection` / `SndbMqClient` / `SndbObjectStorageClient` / EF Core provider / HTTP API。竞品也走它们各自的官方 .NET 客户端（`Npgsql` / `StackExchange.Redis` / `InfluxDB.Client` / `Minio` / `NATS.Client.Core` / `Meilisearch.Net` / `Qdrant.Client` / `ClickHouse.Client`）。
2. **不做替代主张**。SonnetDB 不替代 Redis Cluster / Kafka / Postgres HA / MinIO 分布式集群。我们对齐的是"一台开源组件、单进程、单节点"的能力面。
3. **分布式留作下一步**。不在本里程碑内引入复制 / 副本 / Raft；待客户和长稳数据要求后再启动。
4. **能力够用即可**。我们要证明的是"在边缘 / 单机场景下，SonnetDB 能用一个进程做完九件事，且每一件都做得稳"。
5. **必须能稳**。基础读写功能、可靠性、算法准确度三样必须达到生产门槛；单机性能不强求与专用产品打平，但不能差到不可用（数量级以内即可）。

## 三类对齐

| 对齐维度 | 含义 | 判定方式 |
|---|---|---|
| **能力对齐 (Capability)** | 同一个使用场景，两边 API 都能完成 | 同一份 `Scenario.yaml` 在 `BACKEND=sonnetdb` 与 `BACKEND=composite` 都能跑通，且结果在阈值内一致 |
| **可靠性对齐 (Reliability)** | 异常注入下两边的恢复语义一致 | 同一份 `kill -9 / disk-full / oom / power-loss` 注入剧本，重启后两边 `committed_count` / `lost_count` 都在合同范围内 |
| **算法准确度对齐 (Accuracy)** | 同一组数据，两边算出的统计量与排序在容差内一致 | 数值类容差 ≤ 1e-9 相对误差；分位数 ≤ 0.5%；BM25 / KNN top-K 重合率 ≥ 95% |

> **不**对齐：吞吐绝对值、延迟绝对值、内存占用、进程数。这些写报告，不做 gating。

## 不在 Parity 里做的

- **协议层互操作测试**（aws-cli / mosquitto_pub / redis-cli 直连 SonnetDB）— 永久不做。
- **上层产品专用迁移工具**（SonnetDB 只保留 [Milestone 19](../ROADMAP.md#milestone-19--生态适配底座能力关系--kvcache--对象桶--大量-measurement) 的通用迁移与校验原语）。
- **绝对性能 benchmark**（已在 [tests/SonnetDB.Benchmarks](../tests/SonnetDB.Benchmarks/) 处理，本里程碑只做"数量级"健全性检查）。
- **Schema 迁移、数据回填**（Parity 跑在干净 volume 上）。

## 八大支柱与对齐基线

每个支柱选 1 个主竞品 +（可选）1 个对照品。SonnetDB 走自有连接器，竞品走官方 .NET 客户端。

| 支柱 | 主竞品 | 对照品 | SonnetDB 入口 |
|---|---|---|---|
| 关系型 (Relational) | PostgreSQL 16 | — | `SonnetDB.EntityFrameworkCore` + `SonnetDB.Data` |
| 时序 (TSDB) | InfluxDB 2.7 | VictoriaMetrics 1.106 | `SonnetDB.Data` + Bulk Ingest LP/JSON/Bulk |
| KV / 缓存 | Redis 7 | — | `SonnetDB.Caching` + `KvKeyspace` |
| 对象桶 (Object) | MinIO | — | `SndbObjectStorageClient` |
| 消息 (MQ) | NATS JetStream 2.10 | Mosquitto 2.0（功能对照） | `SndbMqClient` |
| 全文 (FT) | Meilisearch 1.10 | — | SonnetDB 内置全文引擎 + `DocumentFullTextIndexStore` |
| 向量 (Vector) | Qdrant 1.11 | — | SonnetDB 内置向量引擎 + `VectorIndexAdapter` |
| 分析 (Analytics) | ClickHouse 24.8 | — | SQL `GROUP BY` + window functions |

## 测试架构

```
tests/SonnetDB.Parity/
├── docker-compose.parity.yml          # 12 个服务 + harness
├── docker-compose.parity.override.yml # 用户本地覆盖
├── .env                               # 端口 / 凭证 / 数据集大小
├── adapters/
│   ├── IDataPlane.cs                  # 能力契约 + Capability 标志位
│   ├── Scenarios/IScenario.cs         # 单场景接口
│   ├── SonnetDb/
│   ├── Postgres/                      # Npgsql
│   ├── Redis/                         # StackExchange.Redis
│   ├── Influx/                        # InfluxDB.Client
│   ├── VictoriaMetrics/               # Prometheus remote_write + PromQL
│   ├── Minio/                         # AWS SDK pointed at MinIO
│   ├── Nats/                          # NATS.Client.Core
│   ├── Mosquitto/                     # MQTTnet
│   ├── Meilisearch/                   # Meilisearch.NET
│   ├── Qdrant/                        # Qdrant.Client
│   ├── ClickHouse/                    # ClickHouse.Client
│   └── Composite/                     # 路由器：每支柱选最优竞品
├── scenarios/
│   ├── tsdb/                          # ingest_1m, groupby_time, derivative_accuracy ...
│   ├── relational/                    # tpcc_lite, fk_cascade, isolation_read_committed
│   ├── kv/                            # set_get_scan, ttl_accuracy, incr_concurrency
│   ├── object/                        # putget_1gb, multipart_5gb, range_read
│   ├── mq/                            # publish_consume_ack, replay_after_restart
│   ├── fulltext/                      # bm25_ranking, cjk_tokenize, incremental_index
│   ├── vector/                        # ann_recall, filtered_search, upsert_during_query
│   ├── analytics/                     # window_avg_accuracy, percentile_p95
│   ├── reliability/                   # crash_kill9, disk_full, oom_protection, power_loss
│   └── accuracy/                      # 跨支柱算法精度专项
├── runner/
│   ├── ParityRunner.cs                # xUnit + Verify.Xunit
│   ├── BackendSelector.cs             # 读 BACKEND env
│   ├── ScenarioLoader.cs              # 反序列化 yaml
│   ├── Reporting/JsonReporter.cs
│   ├── Reporting/MarkdownReporter.cs
│   └── ResultDiffer.cs                # 计算两边差异 + 容差判定
└── reports/                           # gitignore
    ├── sonnetdb/<run-id>/
    ├── composite/<run-id>/
    └── diff/<run-id>.md
```

### IDataPlane 契约

```csharp
public interface IDataPlane : IAsyncDisposable
{
    Capability Capabilities { get; }
    IRelationalOps Relational { get; }
    ITimeSeriesOps Ts { get; }
    IKvOps Kv { get; }
    IObjectOps Objects { get; }
    IMqOps Mq { get; }
    IFulltextOps Fulltext { get; }
    IVectorOps Vector { get; }
    IAnalyticsOps Analytics { get; }
}

[Flags]
public enum Capability : long
{
    None              = 0,
    Relational        = 1L << 0,
    TimeSeries        = 1L << 1,
    Kv                = 1L << 2,
    Object            = 1L << 3,
    Mq                = 1L << 4,
    Fulltext          = 1L << 5,
    Vector            = 1L << 6,
    Analytics         = 1L << 7,

    // 细粒度能力标志（每个场景声明依赖）
    KvIncr            = 1L << 16,
    KvCas             = 1L << 17,
    KvRangeScan       = 1L << 18,
    ObjectMultipart   = 1L << 19,
    MqConsumerGroup   = 1L << 20,
    MqReplayFromOffset= 1L << 21,
    SqlSubquery       = 1L << 22,
    SqlWindowFunction = 1L << 23,
    SqlForeignKey     = 1L << 24,
    AccuracyPercentile= 1L << 25,
    HnswFiltered      = 1L << 26,
}
```

每个 `IScenario` 声明 `RequiredCapabilities`，runner 看到 backend 不支持时**标 SKIPPED 不算 fail**，但写入报告 `gap_reason` 字段——这就把"我们没有的功能"变成结构化数据，而不是红色 CI。

### 场景声明（Scenario YAML 范例）

```yaml
# scenarios/kv/ttl_accuracy.yaml
name: kv_ttl_accuracy
pillar: kv
required_capabilities: [Kv]
description: TTL 到期回收准确度，对照 Redis EXPIRE 行为
parameters:
  key_count: 10000
  ttl_seconds: 5
  tolerance_seconds: 0.5
steps:
  - kv.set_with_ttl(key, value, ttl)        # 10000 个键
  - sleep(ttl + tolerance)
  - kv.get(key)                             # 期望全部 null
assertions:
  - expired_count == key_count
  - leaked_count == 0
  - early_expired_count <= 1                # 容忍 0.01% 时钟误差
```

## 数值容差与判定规则

| 类型 | 阈值 | 来源 |
|---|---|---|
| 标量数值（sum/avg/min/max） | 相对误差 ≤ 1e-9（IEEE 754 双精度） | 直接比较 |
| 分位数（p50/p95/p99） | 绝对误差 ≤ 0.5% × 数据集 spread | t-digest 误差合同 |
| HyperLogLog distinct_count | 相对误差 ≤ 2% | HLL 标准误差 |
| 时间窗口聚合 | 标量阈值 + 桶边界一致 | 桶起止时间必须完全相等 |
| 排序结果（KNN top-K） | top-K 集合 Jaccard ≥ 0.95 | 允许少量浮点抖动导致顺序差异 |
| 全文 BM25 排序 | top-10 重合率 ≥ 0.8（同义分词差异容忍） | 不强求排序完全一致 |
| 计数（消息 / 行 / 对象） | 必须精确相等 | 任何差异都是 bug |
| 可靠性 commit/lost | `lost_count <= advertised_loss_window` | 由各 backend 各自声明 RPO |

> 任何 SonnetDB 比竞品差**超过 100×** 的延迟必须升 P1 issue，单项不阻塞 CI。比竞品好的不写在 gating 里，写在 README 营销表里。

## Docker Compose 栈

详见 [tests/SonnetDB.Parity/docker-compose.parity.yml](../tests/SonnetDB.Parity/docker-compose.parity.yml)（M20 #127 落地）。要点：

- **单 bridge 网络** `parity-net`，避免与本机已有 service 冲突。
- **端口偏移** 2x000 系列（25080/25432/26379/28086 等），不与原服务默认端口冲突。
- **healthcheck 全量配齐**，harness `depends_on: condition: service_healthy`。
- **profiles**: `light`（仅 sonnetdb + pg + redis + minio + nats + harness，~1.5GB RAM）、`full`（全 12 个服务，~6-8GB RAM）。
- **named volumes** 命名稳定，`docker compose down -v` 一键清空。
- **harness 服务**：.NET 10 console，跑 xUnit 用例，把每场景 JSON + Markdown 报告写到 `harness-reports` volume。

## CI 集成

- `.github/workflows/parity.yml`，**每日凌晨 02:00 UTC + manual dispatch**。
- 矩阵：`{light, full}` × `{ubuntu-latest}`（macOS / Windows runner 暂不跑，containers 不友好）。
- gating 规则：
  - **能力测试通过** = 红绿门槛（任一支柱 fail 就 fail CI）。
  - **可靠性测试通过** = 红绿门槛（数据丢失超合同就 fail CI）。
  - **算法准确度通过** = 红绿门槛（容差超阈值就 fail CI）。
  - **性能数字** = warning only，写到 `tests/SonnetDB.Parity/reports/<run-id>.md` 并贴 PR 评论，不阻塞 merge。
- nightly 运行结果 push 到 [parity-results 分支](https://github.com/IoTSharp/SonnetDB/tree/parity-results)（孤立分支，每次覆盖），README 通过 badge 展示最新通过率。

## 风险与边界

| 风险 | 处理 |
|---|---|
| Docker 在 Windows / macOS CI 不稳 | 仅 ubuntu-latest 跑 parity，开发者本地用 [Docker Desktop](https://www.docker.com/products/docker-desktop/) |
| 竞品镜像版本漂移 | `docker-compose.parity.yml` pin 到具体 tag（不用 `:latest`），手动升级 |
| 竞品官方客户端 NuGet 版本冲突 | adapters 各自 csproj，不互相引用，不进 main solution `SonnetDB.slnx` |
| 测试时长爆炸 | 默认数据集小（10k 行 / 100MB 对象），long-soak 用 `[Trait("Profile","longsoak")]` 仅 nightly 跑 |
| 第三方客户端 IL trim 不安全 | parity 项目设 `<IsAotCompatible>false</IsAotCompatible>`，不污染主仓 AOT 流水线 |
| Mosquitto / NATS .NET 客户端依赖 native | 全在 docker 内运行，本机不安装 |

## 推进顺序与里程碑

详见 [ROADMAP.md Milestone 20](../ROADMAP.md#milestone-20--多模能力对齐与平移测试-parity)，PR 范围 `#127 ~ #136`。

简略路径：

1. **#127**：compose 栈 + harness 骨架 + 第一对适配器（SonnetDB + Postgres）
2. **#128**：IDataPlane 契约 + Capability 标志 + 关系型场景套件
3. **#129**：TSDB 场景套件（vs InfluxDB / VictoriaMetrics）+ 算法准确度判定
4. **#130**：KV 场景套件（vs Redis）+ 向量套件（vs Qdrant）
5. **#131**：对象桶套件（vs MinIO）+ multipart 正确性
6. **#132**：MQ 套件（vs NATS）+ replay 语义对齐
7. **#133**：全文套件（vs Meilisearch）+ BM25 排序对齐
8. **#134**：分析套件（vs ClickHouse）+ 聚合精度对齐
9. **#135**：可靠性套件（kill -9 / disk-full / oom / power-loss）
10. **#136**：CI gating + nightly + parity-results 分支 + README badge

## 推进期间的连带产出

里程碑跑下来会顺带把以下事情做掉（不另立 PR，作为各 PR 的副产品）：

- **KV** 补齐 `INCR` / `DECR` / `CompareAndSet` / `EXPIRE` / `PERSIST` / `TTL`（#130 强需求）
- **MQ** 补 `RecordTypeTombstone` 段滚动（#132 强需求）+ `FlushOnPublish=true` 默认值（不延迟到下一里程碑）
- **对象桶** 补 `ListObjectsV2` `ContinuationToken` 分页（#131 强需求）
- **可靠性** 引入 `tests/SonnetDB.CrashTests/` 真子进程 + `Process.Kill(true)` 注杀（#135）
- **README 措辞对齐**：本里程碑期间逐项把 oversold 文案改成与代码一致（"S3-compatible bucket" → "Embedded object bucket"，"消息队列" → "嵌入式追加日志"）

## 不引入的依赖

- 不引入 [Testcontainers](https://dotnet.testcontainers.org/) — docker compose 直接管，简单、可重入、对 CI 友好；引入 Testcontainers 会让本地与 CI 启动路径分叉。
- 不引入 [k6](https://k6.io/) / [Gatling](https://gatling.io/) — 用 .NET 自身的 BenchmarkDotNet 和 xUnit 即可，工具链统一。
- 不引入测试管理 UI（Allure / TestRail）— 用 GitHub Pages 静态页 + Markdown 报告即可。
- 不引入 Java / Go / Python 客户端 — adapters 全部 .NET，运行环境单一。

## 验收标准

- 八大支柱 × 至少 3 个场景 = 24+ 场景全部 pass（含 SKIPPED 但有 gap_reason）。
- `docker compose --profile full up` 在干净 ubuntu-latest 5 分钟内健康。
- nightly 连续 7 天通过率 ≥ 95%（剩下 5% 留给容器抖动）。
- README 新增 "[Parity vs Open-Source Stack](../README.md#parity)" 段落，链接到最新报告。
- `tests/SonnetDB.Parity/reports/sample-run.md` 产出可读样例报告（含 24+ 场景表格）。
- 至少 1 个真实算法精度差异被 parity 抓出来（证明判定有效，不是橡皮图章）。

## 与其他里程碑的关系

- **不依赖** [Milestone 17 可观测性](../ROADMAP.md#milestone-17--可观测性与运行时可见性-observability--runtime-visibility)：parity 报告自己输出 metrics，不要求 OTel 已经接通。
- **依赖** [Milestone 19 PR #117 对象桶 API](../ROADMAP.md#milestone-19--生态适配底座能力关系--kvcache--对象桶--大量-measurement) 已完成（已 ✅）：对象套件需要 SonnetDB 自己有可工作的 multipart / range read。
- **依赖** [Milestone 12 函数](../ROADMAP.md#milestone-12--函数与算子扩展pid--forecast--udf) 已完成（已 ✅）：算法准确度套件需要 t-digest / HLL / window functions。
- **服务于** [Milestone 19 PR #121 通用长稳报告](../ROADMAP.md#milestone-19--生态适配底座能力关系--kvcache--对象桶--大量-measurement)：parity 框架复用为 SonnetDB 通用能力长稳跑分平台；上层 Profile 长稳由对应项目维护。
- **不冲突** [Milestone 18 VS Code 扩展](../ROADMAP.md#milestone-18--vs-code-数据库扩展sonnetdb-for-vs-code)：完全独立，可并行推进。

## 附录：场景示例（KV TTL 准确度）

```csharp
public sealed class KvTtlAccuracyScenario : IScenario
{
    public string Name => "kv_ttl_accuracy";
    public Capability Required => Capability.Kv;

    public async Task<ScenarioResult> RunAsync(IDataPlane plane, ScenarioContext ctx)
    {
        const int keyCount = 10_000;
        var ttl = TimeSpan.FromSeconds(5);

        // 阶段 1：批量 SET TTL
        for (int i = 0; i < keyCount; i++)
            await plane.Kv.SetAsync($"k{i}", BitConverter.GetBytes(i), ttl);

        // 阶段 2：等过期 + 容差
        await Task.Delay(ttl + TimeSpan.FromMilliseconds(500), ctx.Cancellation);

        // 阶段 3：验证全部回收
        int leaked = 0, earlyExpired = 0;
        for (int i = 0; i < keyCount; i++)
            if ((await plane.Kv.GetAsync($"k{i}")) is not null) leaked++;

        return new ScenarioResult
        {
            Pass = leaked == 0 && earlyExpired <= keyCount * 0.0001,
            Metrics = { ["leaked"] = leaked, ["early_expired"] = earlyExpired },
        };
    }
}
```

同一份代码跑 SonnetDB 与 Redis：
- SonnetDB 后端：`SonnetDbKvAdapter` 调用 `KvKeyspace.Put(key, value, expiresAtUtc)`。
- Redis 后端：`RedisKvAdapter` 调用 `IDatabase.StringSetAsync(key, value, ttl)`。

输出报告：

```markdown
| Scenario | SonnetDB | Redis | Diff |
|---|---|---|---|
| kv_ttl_accuracy | leaked=0 early=0 | leaked=0 early=2 | ✅ within tolerance |
```
