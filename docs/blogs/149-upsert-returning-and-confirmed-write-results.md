---
title: SonnetDB UPSERT 与 RETURNING：取得真实写入结果
categories: SonnetDB
tags: SQL,UPSERT,RETURNING,ADO.NET
draft: false
---

# SonnetDB UPSERT 与 RETURNING：取得真实写入结果

SonnetDB 的关系表支持条件 UPSERT 和 RETURNING，可以把“找到冲突行、判断是否更新、取得写入后的值”放进一条语句。本文用设备状态表说明如何处理更新、跳过和返回结果，并区分语句返回值与事务最终提交。

![SonnetDB UPSERT 与 RETURNING：条件更新与真实返回值](https://img2024.cnblogs.com/blog/1725297/202610/1725297-20261012111327223-849851304.png)

图：AI 主题示意图，并非实测截图；图中 TRUE/FALSE/NULL 指命中冲突后的 WHERE 判断，新键仍走插入路径。SQL、返回值与事务边界以正文和对应源码为准。

## 1. 先确定版本和操作入口

2026-10-12 核查时，GitHub 的 latest 正式发行与 NuGet.org 的 SonnetDB.Core 页面均指向 **4.0.0**。本文事实核查使用本地提交 `dcadffc7137f5653ec46c55a4171cf5f2d83de03`，相关 SQL、事务实现与回归文件同公开提交 `5424b66672e37f49a2a5633fdde0b54a9b1f0290` 一致。文末链接固定在该公开提交，方便逐项检查；当前源码复核不代替读者实际安装包的运行验收。

以下 SQL 可通过嵌入式 `SndbConnection` 或 Server 的 REST SQL 入口执行。使用 ADO.NET 的 `Protocol=frame-http2` 时，写入回落到同一 HTTP/2 连接上的 REST SQL/NDJSON 路径；原生 Frame 查询端点本身仍只读。3.1.0 不具备这里介绍的关系 ON CONFLICT 合同，不能只升级一端就假定新旧客户端和服务端语义一致。

## 2. 建立带版本的设备状态表

在独立示例数据库中执行：

```sql
CREATE TABLE device_state (
    id INT,
    reading INT,
    version INT ROWVERSION,
    PRIMARY KEY (id)
);

INSERT INTO device_state (id, reading)
VALUES (1, 10), (2, 20);
```

`version` 是引擎维护的 INT 列，插入初值为 1，成功更新时自动增加。不要在 INSERT 列清单或 UPDATE SET 中手动赋予它业务版本值。若需要设备事件序号，应另设普通列。

冲突目标必须对应主键或唯一索引；复合目标按索引声明的列顺序匹配。`DO UPDATE` 必须显式写目标，例如 `ON CONFLICT (id)`，不能用任意 WHERE 条件代替唯一约束。当前 ON CONFLICT 路径限关系表的 VALUES 插入，不能外推到 measurement、文档写入或任意 INSERT SELECT。

## 3. 只接受更大的读数

```sql
INSERT INTO device_state (id, reading)
VALUES (1, 11), (2, 19), (3, 30)
ON CONFLICT (id) DO UPDATE SET reading = excluded.reading
WHERE excluded.reading > reading
RETURNING id, reading, version;
```

`excluded.reading` 是当前候选插入行的值；未限定的 `reading` 是冲突发生前目标行的值。WHERE 只有结果为 SQL TRUE 时更新，FALSE 和 NULL 均跳过。新键没有冲突，直接按插入路径处理；这个 WHERE 不是针对全部候选行的通用过滤器。

按上述数据与合同，预期结果是：

| 候选 | 决定 | 返回行 |
| --- | --- | --- |
| id=1，reading=11 | 11 大于现有 10，更新 | 1、11、2 |
| id=2，reading=19 | 19 不大于现有 20，跳过 | 无 |
| id=3，reading=30 | 新主键，插入 | 3、30、1 |

受影响数为 2，RETURNING 按输入顺序返回 id=1 与 id=3；跳过行不计影响数，也不返回占位行。再执行 `SELECT id, reading, version FROM device_state ORDER BY id;`，可检查 id=2 仍为 reading=20、version=1。这里是根据实现与既有测试推导的预期，不是本轮数据库执行截图或新测试回执。

同一语句重复更新同一目标行会整句拒绝。不要依赖 VALUES 中后出现的一行覆盖前一行；在应用侧先定义重复事件的取舍，再提交唯一候选。

## 4. 用 ADO.NET 读取返回行与影响数

下面是可以放进控制台项目的完整最小片段。先在目标环境安装与 Server 匹配的 SonnetDB 包，或从上述源码基线引用 SonnetDB.Data 项目；使用新目录避免重复建表。

```csharp
using SonnetDB.Data;

using var connection = new SndbConnection("Data Source=./upsert-demo");
connection.Open();

using (var setup = connection.CreateCommand())
{
    setup.CommandText = """
        CREATE TABLE readings (id INT, value INT, version INT ROWVERSION,
            PRIMARY KEY (id))
        """;
    setup.ExecuteNonQuery();
}

using var command = connection.CreateCommand();
command.CommandText = """
    INSERT INTO readings (id, value) VALUES (@id, @value)
    ON CONFLICT (id) DO UPDATE SET value = excluded.value
    WHERE excluded.value > value
    RETURNING id, value, version
    """;
command.Parameters.AddWithValue("id", 1L);
command.Parameters.AddWithValue("value", 12L);
using var reader = command.ExecuteReader();
for (int i = 0; i < 10 && reader.Read(); i++)
    Console.WriteLine($"{reader.GetInt64(0)}, {reader.GetInt64(1)}, {reader.GetInt64(2)}");
Console.WriteLine($"受影响行：{reader.RecordsAffected}");
```

这个示例只有一个候选行，读取循环另设 10 行上限。SQL INT 的 CLR 值是 Int64；返回列按 RETURNING 的声明顺序读取。Reader 应读完再核对 RecordsAffected。只要首列时可用 ExecuteScalar；它返回第一行第一列，不能据此推断全部影响数。不要使用连接级 LAST_INSERT_ID() 代替本条语句的 RETURNING。

当前 RETURNING 投影只接受列名或 `*`，不接受任意表达式与别名。想输出中文字段名，可以在应用层映射，避免拼出本引擎尚不支持的语法。

## 5. 事务里的返回值还需要提交确认

新 Server 提供短期事务会话。ADO.NET 在 BeginTransaction 时创建会话，后续 UPSERT 使用同一 SqlTransactionContext，RETURNING 来自实际缓冲写入；Commit 不重新执行整组 SQL。旧 Server 没有会话端点时，客户端使用旧协议，并在入队前拒绝 `DO UPDATE ... RETURNING`。不能把旧路径的回滚预览称为真实提交结果。

即使新会话已经返回 reading=11，也只是这个事务中的待提交值。另一事务的并发修改可能令 Commit 发生乐观冲突；Rollback 后该返回值不会变成已提交事实。应先读完并关闭 Reader，再提交，然后才向上游确认业务成功。

会话属于单个 Server 进程，活动租约为 2 分钟，最多 128 个活动会话，终态保留 2 分钟。负载均衡需保持会话路由，不能把会话 ID 发到另一实例当作分布式事务。服务端重启及终态过期后的结果需要独立对账。

提交响应丢失时，当前客户端会先查询原会话终态；只有确认仍 active 且未取消时，才允许一次同会话幂等提交重试。提交与终态都无法确认时会报告“提交结果未知”，并提供原会话查询路径。应用不应另开新事务重放 `value = value + excluded.value`，否则可能重复累加。

## 6. 怎样判断一次接入是否正确

先用独立数据库分别检查新键插入、命中更新、条件 FALSE、条件 NULL、重复目标拒绝；读取返回行、RecordsAffected 与最终 SELECT，三者一起解释。接着测试事务回滚及 Commit 冲突。远程环境还要核对服务端版本、会话创建路由和响应丢失后的终态查询，而非仅观察 HTTP 200。

既有仓库审计记录了 Core 合同和本地真实 Kestrel 验证；本次只读取源码与证据，没有重跑产品测试、SQL 示例、性能基准、固定硬件或长期生产恢复测试。本文不提供吞吐数字，也不把本地回归等同于集群或生产验收。九模型整体能力仍分别有 partial/Beta 边界；关系表 UPSERT 不扩展为跨九模型事务。

## 参考与继续阅读

- [固定提交的 SQL 参考](https://github.com/IoTSharp/SonnetDB/blob/5424b66672e37f49a2a5633fdde0b54a9b1f0290/docs/sql-reference.md)
- [条件 UPSERT 的历史审计](https://github.com/IoTSharp/SonnetDB/blob/5424b66672e37f49a2a5633fdde0b54a9b1f0290/docs/audits/github-issue-184-conditional-upsert-20260925.md)：其中旧远程协议限制应结合后续会话审计阅读。
- [远程事务会话审计与剩余边界](https://github.com/IoTSharp/SonnetDB/blob/5424b66672e37f49a2a5633fdde0b54a9b1f0290/docs/audits/github-issue-184-remote-session-20260925.md)
- [UPSERT 可核查的回归示例](https://github.com/IoTSharp/SonnetDB/blob/5424b66672e37f49a2a5633fdde0b54a9b1f0290/tests/SonnetDB.Core.Tests/Sql/SqlOnConflictDoUpdateReviewTests.cs)
- [当前路线图](https://github.com/IoTSharp/SonnetDB/blob/5424b66672e37f49a2a5633fdde0b54a9b1f0290/ROADMAP.md)
- [SonnetDB 业务表从建表到演进：主键、默认值、约束与索引](https://www.cnblogs.com/IoTSharp/p/23260377)
