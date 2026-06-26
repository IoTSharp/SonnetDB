---
layout: default
title: "SonnetDB 全面指南"
description: "涵盖架构、安装、SQL、API、扩展功能与运维的完整帮助文档"
permalink: /help/comprehensive-guide/
---

# SonnetDB 全面指南

> 版本 2.5.0 · 开源时序数据库 · MIT 许可证
> GitHub: https://github.com/maikebing/SonnetDB

---

## 目录

1. [产品概述](#1-产品概述)
2. [快速安装与启动](#2-快速安装与启动)
3. [系统架构](#3-系统架构)
4. [数据模型](#4-数据模型)
5. [存储格式与文件布局](#5-存储格式与文件布局)
6. [SQL 参考——数据面](#6-sql-参考数据面)
7. [SQL 参考——控制面](#7-sql-参考控制面)
8. [内建函数全集](#8-内建函数全集)
9. [向量检索](#9-向量检索)
10. [地理空间功能](#10-地理空间功能)
11. [PID 控制律](#11-pid-控制律)
12. [预测与异常检测](#12-预测与异常检测)
13. [用户自定义函数](#13-用户自定义函数)
14. [嵌入式 API](#14-嵌入式-api)
15. [ADO.NET 提供程序](#15-adonet-提供程序)
16. [CLI 命令行工具](#16-cli-命令行工具)
17. [HTTP REST API](#17-http-rest-api)
18. [Web 管理后台](#18-web-管理后台)
19. [Copilot AI 助手](#19-copilot-ai-助手)
20. [批量写入](#20-批量写入)
21. [部署与运维](#21-部署与运维)
22. [VS Code 扩展](#22-vs-code-扩展)
23. [性能基准](#23-性能基准)
24. [常见问题](#24-常见问题)

---

## 1. 产品概述

SonnetDB 是一款基于 C# / .NET 10 构建的开源时序数据库，由 maikebing 开发并维护。它定位为嵌入式优先（Embedded-First）的时序引擎，同时提供完整的 HTTP 服务端、Web 管理界面、CLI 命令行工具和 ADO.NET 数据提供程序。

### 核心特性

- **嵌入式优先**：可在进程内直接打开数据库目录，无需独立服务进程
- **纯安全代码**：零 unsafe 关键字，基于 Span\<T\> 和 MemoryMarshal 实现高性能二进制 I/O
- **完整的 SQL 支持**：递归下降解析器，零第三方依赖，覆盖 DDL/DML/查询/聚合/窗口函数
- **50+ 内建函数**：涵盖数学、统计、窗口、向量、地理空间、PID 控制、预测、异常检测
- **向量检索**：原生 VECTOR 类型 + HNSW 索引 + KNN 表值函数 + pgvector 兼容运算符
- **地理空间**：原生 GEOPOINT 类型 + 球面距离/方位/范围过滤 + 轨迹聚合
- **PID 控制律**：内置 PID 控制器与自整定算法（ZN/CC/IMC）
- **AI Copilot**：内建 AI 助手，支持知识库、技能库、多轮对话与 SQL 自纠正
- **AOT 兼容**：服务端原生 AOT 发布，零反射零警告
- **目录式持久化**：WAL + MemTable + 不可变 Segment + 分层压缩

### 适用场景

- IoT 物联网数据采集与监控
- 工业控制系统（含 PID 闭环）
- 车联网轨迹追踪与分析
- 运维监控指标体系
- 实时异常检测与预测
- AI/ML 向量相似性检索

---

## 2. 快速安装与启动

### 2.1 Docker 部署（推荐）

```bash
# 拉取镜像
docker pull iotsharp/sonnetdb:latest

# 启动容器
docker run --rm -p 5080:5080 -v ./sonnetdb-data:/data iotsharp/sonnetdb
```

### 2.2 从源码构建

```bash
git clone https://github.com/maikebing/SonnetDB.git
cd SonnetDB
dotnet build src/SonnetDB -c Release
dotnet run --project src/SonnetDB
```

### 2.3 通过 CLI 工具安装

```bash
dotnet tool install --global SonnetDB.Cli
sndb --help
```

### 2.4 首次安装向导

浏览器访问 `http://127.0.0.1:5080/admin/`：

1. 填写服务器标识与组织名称
2. 创建管理员用户与密码
3. 生成初始 Bearer Token
4. 进入管理后台

### 2.5 NuGet 引用（嵌入式）

```xml
<PackageReference Include="SonnetDB.Core" />
<PackageReference Include="SonnetDB" />
<PackageReference Include="SonnetDB.EntityFrameworkCore" />
```

---

## 3. 系统架构

### 3.1 整体架构

```
┌─────────────────────────────────────────────────────┐
│  Application / CLI / ADO.NET / Admin UI             │
└──────────────────────┬──────────────────────────────┘
                       │
┌──────────────────────▼──────────────────────────────┐
│  SQL Engine / Control Plane / Auth                  │
│  ┌──────────┐ ┌──────────┐ ┌────────────────────┐  │
│  │ Parser   │ │ Executor │ │ Function Registry  │  │
│  └──────────┘ └──────────┘ └────────────────────┘  │
└──────────────────────┬──────────────────────────────┘
                       │
┌──────────────────────▼──────────────────────────────┐
│  Storage Layer                                      │
│  ┌───────┐ ┌──────────┐ ┌────────┐ ┌───────────┐  │
│  │  WAL  │ │ MemTable │ │ Segment│ │Compaction │  │
│  └───────┘ └──────────┘ └────────┘ └───────────┘  │
└─────────────────────────────────────────────────────┘
```

### 3.2 写入路径

```
数据到达 → WAL 追加写入 → MemTable 缓冲 → Flush → 不可变 Segment
                  ↓                              ↓
            WAL Truncator                  Compaction Worker
```

### 3.3 查询路径

```
SQL → Parser → Executor
                  ↓
    ┌─────────────┴─────────────┐
    ↓                           ↓
Control Plane             Query Engine
(用户/权限/DB管理)       ↓           ↓
                    MemTable    SegmentReader
                    (未Flush)   (已持久化)
                                 ↓
                           MultiSegmentIndex
                           (N路归并合并)
```

### 3.4 核心组件

| 组件 | 说明 |
|------|------|
| Tsdb | 引擎外观，所有操作的入口 |
| WalWriter/Reader | 预写日志，追加写入 + CRC + 崩溃恢复 |
| MemTable | 内存写缓冲区，未 Flush 数据 |
| Segment Writer/Reader | 不可变数据段文件（.SDBSEG） |
| SegmentManager | 多段管理与查询路由 |
| Compaction Worker | 分层大小分级压缩 |
| QueryEngine | 查询执行引擎，混合 MemTable + Segment |
| SeriesCatalog | 序列注册表（SeriesId = XxHash64） |
| TagInvertedIndex | Tag 倒排索引加速 WHERE 过滤 |

---

## 4. 数据模型

### 4.1 核心概念

| 概念 | 说明 | 类比 |
|------|------|------|
| Measurement | 时序数据实体 | 数据库表 |
| Tag | 序列标识/过滤维度（字符串） | 标签列 |
| Field | 观测值列 | 指标列 |
| Time | 保留时间戳列（Unix 毫秒） | 主键 |
| Series | measurement + 所有 tag 的键值对 | 序列标识 |

### 4.2 字段类型

| 类型 | 说明 | SQL 语法 |
|------|------|----------|
| FLOAT | 64 位浮点数 | `FIELD FLOAT` |
| INT | 64 位整数 | `FIELD INT` |
| BOOL | 布尔值 | `FIELD BOOL` |
| STRING | 字符串 | `FIELD STRING` |
| VECTOR(n) | n 维浮点向量 | `FIELD VECTOR(384)` |
| GEOPOINT | 地理坐标点 (lat, lon) | `FIELD GEOPOINT` |

### 4.3 序列标识

```
SeriesKey = measurement + 排序后的 tag 键值对
SeriesId  = XxHash64(SeriesKey)
```

### 4.4 数据点结构

```
Point {
    Time:  long (Unix ms)
    Fields: map[string]FieldValue
}

FieldValue = { Float64 | Int64 | Boolean | String | Vector | GeoPoint }
```

### 4.5 稀疏字段语义

SonnetDB 的 field 采用稀疏模型：同一个 measurement 的不同数据点可以携带不同的 field 集合。某个时间点没有写入某个 field 时，查询结果会把该列返回为 `NULL`，表示“未记录该字段”，而不是写入了一个显式空值。

因此，`CREATE MEASUREMENT` 中的 `NULL` / `NOT NULL` 修饰符当前只作为 SQL 兼容信息被 parser 接受，不会持久化为 catalog 约束，也不会强制 `NOT NULL`。`DEFAULT` 子句会在执行建表时明确报不支持；如需默认值，请在写入侧提供具体值，如需缺值，请省略该 field。

---

## 5. 存储格式与文件布局

### 5.1 目录结构

```
<database-root>/
├── catalog.SDBCAT              # 序列编目
├── measurements.tslschema      # Measurement Schema（v2）
├── tombstones.tslmanifest      # 删除墓碑文件
├── wal/
│   ├── 0000000000000001.SDBWAL
│   └── 0000000000000002.SDBWAL
└── segments/
    ├── a1b2c3d4e5f6a7b8.SDBSEG  # v6 起包含可选 HNSW / 聚合 sketch section
    └── ...
```

### 5.2 系统目录（服务端模式）

```
<data-root>/.system/
├── installation.json  # 安装元数据
├── users.json         # 用户 + 密码哈希 + Token
└── grants.json        # 数据库授权
```

### 5.3 Segment 格式（当前 v6）

| 版本 | 引入 PR | 变更内容 |
|------|---------|----------|
| v1 | 原始 | 初始格式 |
| v2 | #50 | BlockHeader 扩展（72B，AggregateMin/Max） |
| v3 | #58c | VectorRaw 编码支持 |
| v4 | #70 | GeoPointRaw 编码支持 |
| v5 | #76 | GeoHash 段内过滤 |
| v6 | 当前 | Header mini-footer 摘要；HNSW / TDigest / HyperLogLog section 内嵌到 `.SDBSEG` |

### 5.4 Schema 文件版本

| 版本 | 变更内容 |
|------|----------|
| v1 | 原始格式 |
| v2 | VECTOR(dim) 维度信息 |

---

## 6. SQL 参考——数据面

### 6.1 CREATE MEASUREMENT

定义 measurement 的结构化 schema。

```sql
-- 基础用法
CREATE MEASUREMENT cpu (
    host      TAG,
    region    TAG,
    usage     FIELD FLOAT NULL,
    cores     FIELD INT,
    throttled FIELD BOOL,
    label     FIELD STRING NOT NULL
);

-- 向量字段 + HNSW 索引
CREATE MEASUREMENT documents (
    source    TAG,
    embedding FIELD VECTOR(384) WITH INDEX hnsw(m=16, ef=200)
);

-- 地理空间字段
CREATE MEASUREMENT vehicle (
    device   TAG,
    position FIELD GEOPOINT,
    speed    FIELD FLOAT
);
```

**规则：**
- 必须至少有一个 FIELD 列
- time 列不在 schema 中定义，是保留伪列
- TAG 默认字符串类型，TAG 和 TAG STRING 等价
- NULL / NOT NULL 为 DDL 兼容修饰符，当前不持久化为约束，也不改变稀疏字段语义
- DEFAULT 子句当前执行时明确报不支持
- VECTOR(dim) 的 dim 必须在 CREATE 时指定
- HNSW 索引参数：m=邻居数，ef=搜索广度

### 6.2 INSERT INTO

```sql
-- 单行写入
INSERT INTO cpu (time, host, region, usage) VALUES (1713676800000, 'server-01', 'cn-hz', 0.71);

-- 多行写入
INSERT INTO cpu (time, host, region, usage) VALUES
    (1713676800000, 'server-01', 'cn-hz', 0.71),
    (1713676860000, 'server-01', 'cn-hz', 0.73);

-- 向量数据
INSERT INTO documents (time, source, embedding) VALUES
    (1713676800000, 'wiki', [0.10, 0.20, 0.30, 0.40]);

-- 地理空间数据
INSERT INTO vehicle (time, device, position, speed) VALUES
    (1713676800000, 'car-1', POINT(39.9042, 116.4074), 0);
```

**规则：**
- time 为 Unix 毫秒，省略时使用当前 UTC 时间
- TAG 列必须为字符串字面量
- FIELD FLOAT 可接受整数或浮点
- NULL 当前不能作为 INSERT 的显式列值；表达缺值请省略该 field

### 6.3 SELECT 查询

```sql
-- 查询所有列
SELECT * FROM cpu WHERE host = 'server-01';

-- 显式投影 + 时间范围
SELECT time, host, usage FROM cpu
WHERE host = 'server-01' AND time >= 1713676800000 AND time < 1713677400000;

-- 投影内算术表达式
SELECT time, usage, usage * 100 AS pct, cores + 2 FROM cpu;

-- 一元负号
SELECT time, -usage AS neg FROM cpu;

-- 标量函数嵌套
SELECT time, round(abs(usage - 0.5), 2) AS dev FROM cpu;

-- 单表别名 + 限定列名
SELECT c.time, c.host, c."usage" FROM cpu AS c
WHERE c.host = 'server-01'
ORDER BY c.time ASC
LIMIT 10;

-- MM4：measurement JOIN 关系维表
SELECT t.time, d.name, t.value
FROM temperature AS t
JOIN devices AS d ON t.device_id = d.id
WHERE d.tenant = 'tenant-1'
ORDER BY t.time DESC
LIMIT 100;

-- 分页：LIMIT / OFFSET 风格
SELECT * FROM cpu WHERE host = 'server-01'
ORDER BY time ASC
LIMIT 10 OFFSET 0;

-- 分页：SQL 标准 FETCH 风格
SELECT * FROM cpu WHERE host = 'server-01'
ORDER BY time ASC
OFFSET 5 ROWS FETCH NEXT 5 ROWS ONLY;
```

### 6.4 WHERE 子句

```sql
-- Tag 等值过滤
WHERE host = 'server-01'

-- 时间范围过滤
WHERE time >= 1713676800000 AND time < 1713677400000

-- 空间范围过滤
WHERE geo_within(position, 31.2304, 121.4737, 50000)

-- 空间矩形过滤
WHERE geo_bbox(position, 30.0, 113.0, 35.0, 122.0)

-- 多条件 AND 连接
WHERE host = 'server-01' AND region = 'cn-hz'
  AND time >= 1713676800000 AND time < 1713677400000
```

**当前限制：**
- 仅支持 AND 连接
- 不支持 OR / NOT
- 不支持 field 不等式条件（如 usage > 50）

### 6.5 聚合查询

```sql
-- 基础聚合
SELECT count(usage), sum(usage), min(usage), max(usage),
       avg(usage), first(usage), last(usage)
FROM cpu WHERE host = 'server-01';

-- count(*)
SELECT count(*) FROM cpu WHERE host = 'server-01';

-- 统计聚合
SELECT stddev(usage), variance(usage), spread(usage),
       median(usage), mode(usage)
FROM cpu WHERE host = 'server-01';

-- 分位数聚合
SELECT percentile(usage, 50), p50(usage), p90(usage), p95(usage), p99(usage)
FROM cpu WHERE host = 'server-01';

-- T-Digest 内部状态
SELECT tdigest_agg(usage) FROM cpu WHERE host = 'server-01';

-- 去重计数（HyperLogLog）
SELECT distinct_count(usage) FROM cpu WHERE host = 'server-01';

-- 直方图
SELECT histogram(usage, 5) FROM cpu WHERE host = 'server-01';

-- 向量质心
SELECT centroid(embedding) FROM documents;
```

### 6.6 GROUP BY time

```sql
-- 按 2 分钟时间桶聚合
SELECT avg(usage), max(usage), count(usage)
FROM cpu WHERE host = 'server-01'
GROUP BY time(2m);

-- 时长单位：ms, s, m, h, d
-- 支持 ns, us
SELECT count(*)
FROM cpu WHERE host = 'server-01'
GROUP BY time(1000ms);
```

### 6.7 窗口函数

#### 差分与变化量

```sql
-- 当前行与前一行之差
SELECT time, difference(usage) FROM cpu WHERE host = 'server-01';

-- delta 同义词
SELECT time, delta(usage) FROM cpu WHERE host = 'server-01';

-- 计数器增量（抑制回零）
SELECT time, increase(usage) FROM cpu WHERE host = 'server-01';
```

#### 变化率

```sql
-- 每秒变化率
SELECT time, derivative(usage) FROM cpu WHERE host = 'server-01';

-- 非负变化率
SELECT time, non_negative_derivative(usage) FROM cpu WHERE host = 'server-01';

-- rate（non_negative_derivative 同义词）
SELECT time, rate(usage) FROM cpu WHERE host = 'server-01';

-- 瞬时率
SELECT time, irate(usage) FROM cpu WHERE host = 'server-01';
```

#### 累计与积分

```sql
-- 累积求和
SELECT time, cumulative_sum(usage) FROM cpu WHERE host = 'server-01';

-- 梯形积分
SELECT time, integral(usage) FROM cpu WHERE host = 'server-01';
```

#### 平滑

```sql
-- 移动平均（窗口=3）
SELECT time, moving_average(usage, 3) FROM cpu WHERE host = 'server-01';

-- 指数加权移动平均
SELECT time, ewma(usage, 0.3) FROM cpu WHERE host = 'server-01';

-- 双指数平滑（Holt 加法）
SELECT time, holt_winters(usage, 0.5, 0.1) FROM cpu WHERE host = 'server-01';
```

#### 空值处理

```sql
-- 常量填充
SELECT time, fill(usage, -1) FROM cpu WHERE host = 'server-01';

-- 前向填充（Last Observation Carried Forward）
SELECT time, locf(usage) FROM cpu WHERE host = 'server-01';

-- 线性插值
SELECT time, interpolate(usage) FROM cpu WHERE host = 'server-01';
```

#### 状态分析

```sql
-- 状态变化检测
SELECT time, state_changes(throttled) FROM cpu WHERE host = 'server-01';

-- 状态持续时长（毫秒）
SELECT time, state_duration(throttled) FROM cpu WHERE host = 'server-01';
```

### 6.8 DELETE

```sql
-- 按时间范围删除（Tombstone 机制）
DELETE FROM cpu
WHERE host = 'server-01'
  AND time >= 1713676800000 AND time <= 1713677400000;

-- 按 tag 删除整个序列
DELETE FROM cpu WHERE host = 'server-01';
```

### 6.9 元数据查询

```sql
-- 列出所有 measurement
SHOW MEASUREMENTS;
SHOW TABLES;  -- 等价别名

-- 描述表结构
DESCRIBE MEASUREMENT cpu;
DESCRIBE cpu;  -- MEASUREMENT 关键字可省略
DESC cpu;      -- DESC 是 DESCRIBE 的别名
```

### 6.10 表值函数

```sql
-- KNN 向量检索
SELECT * FROM knn(documents, embedding, [0.10, 0.20, 0.30, 0.40], 3);
SELECT * FROM knn(documents, embedding, [0.10, 0.20, 0.30, 0.40], 5, 'l2');
SELECT * FROM knn(documents, embedding, [0.10, 0.20, 0.30, 0.40], 5)
WHERE source = 'wiki';

-- 时间序列预测
SELECT * FROM forecast(cpu, usage, 5, 'linear') WHERE host = 'server-01';
SELECT * FROM forecast(reactor, temperature, 6, 'holt_winters')
WHERE device = 'r1';
SELECT * FROM forecast(reactor, temperature, 6, 'holt_winters', 5)
WHERE device = 'r1';
```

### 6.11 注释语法

```sql
-- 单行注释（标准 SQL 风格）
// 单行注释（C 风格）
/* 多行注释
   块注释风格 */
REM 行注释（BASIC 风格）
```

### 6.12 标识符引用

```sql
-- 双引号包裹含特殊字符的列名
SELECT "usage" FROM cpu WHERE host = 'server-01';
```

---

## 7. SQL 参考——控制面

### 7.1 用户管理

```sql
-- 创建用户
CREATE USER viewer WITH PASSWORD 'viewer123';
CREATE USER admin WITH PASSWORD 'admin456' SUPERUSER;

-- 修改密码
ALTER USER viewer WITH PASSWORD 'newpass999';

-- 删除用户
DROP USER viewer;
```

### 7.2 数据库管理

```sql
-- 创建数据库
CREATE DATABASE demo;

-- 删除数据库（不可逆）
DROP DATABASE demo;

-- 查看所有数据库
SHOW DATABASES;
```

### 7.3 授权管理

```sql
-- 授权
GRANT READ ON DATABASE demo TO viewer;
GRANT WRITE ON DATABASE demo TO writer;
GRANT ADMIN ON DATABASE demo TO admin;

-- 撤销授权
REVOKE ON DATABASE demo FROM viewer;

-- 查看授权
SHOW GRANTS;
SHOW GRANTS FOR viewer;
```

### 7.4 Token 管理

```sql
-- 签发 Token（明文仅返回一次）
ISSUE TOKEN FOR writer;

-- 查看 Token 列表
SHOW TOKENS;
SHOW TOKENS FOR writer;

-- 吊销 Token
REVOKE TOKEN 'tok_xxxxxx';
```

### 7.5 角色与权限

| 角色 | SELECT | INSERT/DELETE | 控制面 | 管理用户 |
|------|--------|---------------|--------|----------|
| readonly | ✓ | ✗ | ✗ | ✗ |
| readwrite | ✓ | ✓ | ✗ | ✗ |
| admin | ✓ | ✓ | ✓ | ✓ |

---

## 8. 内建函数全集

### 8.1 数学标量函数

| 函数 | 说明 | 示例 |
|------|------|------|
| `abs(x)` | 绝对值 | `abs(-1.5) → 1.5` |
| `round(x [, d])` | 四舍五入 | `round(3.14159, 2) → 3.14` |
| `sqrt(x)` | 平方根 | `sqrt(9) → 3` |
| `log(x, base)` | 对数 | `log(8, 2) → 3` |
| `coalesce(x, default)` | 空值合并 | `coalesce(null, 0) → 0` |

### 8.2 聚合函数

| 函数 | 说明 | 算法 |
|------|------|------|
| `count(x)` | 计数 | 精确计数 |
| `sum(x)` | 求和 | 精确求和 |
| `min(x)` | 最小值 | 流式比较 |
| `max(x)` | 最大值 | 流式比较 |
| `avg(x)` | 平均值 | 流式 Sum/Count |
| `first(x)` | 第一个值 | 流式 |
| `last(x)` | 最后一个值 | 流式 |
| `stddev(x)` | 标准差 | Welford 在线算法 |
| `variance(x)` | 方差 | Welford 在线算法 |
| `spread(x)` | 极差(max-min) | 流式 |
| `median(x)` | 中位数 | T-Digest |
| `mode(x)` | 众数 | 频率统计 |
| `percentile(x, p)` | p 分位数 | T-Digest |
| `p50(x)` / `p90(x)` / `p95(x)` / `p99(x)` | 快捷分位数 | T-Digest |
| `tdigest_agg(x)` | T-Digest 状态(JSON) | T-Digest |
| `distinct_count(x)` | 去重计数 | HyperLogLog |
| `histogram(x, n)` | 直方图 | 等宽分桶 |
| `centroid(vec)` | 向量质心 | 逐维度均值 |

### 8.3 窗口函数

| 函数 | 说明 | 参数 |
|------|------|------|
| `difference(x)` | 行间差分 | field |
| `delta(x)` | 行间差分(同义词) | field |
| `increase(x)` | 计数器增量(max(0, diff)) | field |
| `derivative(x)` | 每秒变化率 | field |
| `non_negative_derivative(x)` | 非负变化率 | field |
| `rate(x)` | 同 non_negative_derivative | field |
| `irate(x)` | 瞬时变化率 | field |
| `cumulative_sum(x)` | 累积求和 | field |
| `integral(x)` | 梯形积分 | field [, time_unit] |
| `moving_average(x, n)` | 移动平均 | field, window |
| `ewma(x, alpha)` | 指数加权移动平均 | field, alpha |
| `holt_winters(x, alpha, beta)` | 双指数平滑 | field, alpha, beta |
| `fill(x, value)` | 空值常量填充 | field, default |
| `locf(x)` | 前向填充 | field |
| `interpolate(x)` | 线性插值 | field |
| `state_changes(x)` | 状态变化点 | field |
| `state_duration(x)` | 状态持续时长 | field |

### 8.4 向量函数

| 函数 | 说明 | 示例 |
|------|------|------|
| `cosine_distance(a, b)` | 余弦距离 | `cosine_distance(emb, [0.1,0.2])` |
| `l2_distance(a, b)` | 欧几里得距离 | `l2_distance(emb, [0.1,0.2])` |
| `inner_product(a, b)` | 内积 | `inner_product(emb, [0.1,0.2])` |
| `vector_norm(v)` | L2 范数 | `vector_norm(emb)` |
| `a <=> b` | 余弦距离（运算符） | `emb <=> [0.1,0.2]` |
| `a <-> b` | L2 距离（运算符） | `emb <-> [0.1,0.2]` |
| `a <#> b` | 负内积（运算符） | `emb <#> [0.1,0.2]` |

### 8.5 地理空间函数

| 函数 | 说明 | 返回 |
|------|------|------|
| `lat(point)` | 提取纬度 | double |
| `lon(point)` | 提取经度 | double |
| `geo_distance(p1, p2)` | 球面距离(米) | double |
| `geo_bearing(p1, p2)` | 方位角(度) | double |
| `geo_within(p, lat, lon, r)` | 半径内判断 | bool |
| `geo_bbox(p, lat1, lon1, lat2, lon2)` | 矩形内判断 | bool |
| `geo_speed(p, time)` | 瞬时速度(m/s) | double |
| `ST_Distance(p1, p2)` | geo_distance 别名 | double |
| `ST_Within(p, lat, lon, r)` | geo_within 别名 | bool |
| `ST_DWithin(p, lat, lon, r)` | geo_within 别名 | bool |

#### 轨迹聚合函数

| 函数 | 说明 |
|------|------|
| `trajectory_length(position)` | 轨迹总长度(米) |
| `trajectory_centroid(position)` | 轨迹质心(GeoPoint) |
| `trajectory_bbox(position)` | 轨迹包络矩形(JSON) |
| `trajectory_speed_max(position, time)` | 最大速度(m/s) |
| `trajectory_speed_avg(position, time)` | 平均速度(m/s) |
| `trajectory_speed_p95(position, time)` | P95 速度(m/s) |

### 8.6 PID 控制函数

| 函数 | 说明 | 适用 |
|------|------|------|
| `pid_series(field, sp, kp, ki, kd)` | 行级 PID 输出 | 窗口函数 |
| `pid(field, sp, kp, ki, kd)` | 桶级 PID 聚合 | 聚合函数 |
| `pid_estimate(field, method, step, ...)` | PID 参数自整定 | 聚合函数 |

**整定方法：** `'imc'`（Skogestad IMC）、`'zn'`（Ziegler-Nichols）、`'cc'`（Cohen-Coon）

### 8.7 预测与检测函数

```sql
-- 预测
SELECT * FROM forecast(measurement, field, horizon, algorithm [, season]);

-- 异常检测
SELECT time, anomaly(field, 'zscore', 2.0) AS outlier FROM ...;
SELECT time, anomaly(field, 'mad', 2.5) AS outlier FROM ...;
SELECT time, anomaly(field, 'iqr', 1.5) AS outlier FROM ...;

-- 变点检测
SELECT time, changepoint(field, 'cusum', 4.0) AS shift FROM ...;
```

---

## 9. 向量检索

### 9.1 创建含向量字段的表

```sql
CREATE MEASUREMENT documents (
    source    TAG,
    category  TAG,
    title     FIELD STRING,
    embedding FIELD VECTOR(4)  -- 指定向量维度
);

-- 带 HNSW 索引
CREATE MEASUREMENT docs_indexed (
    source    TAG,
    embedding FIELD VECTOR(384) WITH INDEX hnsw(m=16, ef=200)
);
```

### 9.2 写入向量数据

```sql
INSERT INTO documents (time, source, category, title, embedding) VALUES
    (1713676800000, 'wiki', 'tech', '时序数据库简介', [0.10, 0.20, 0.30, 0.40]),
    (1713676801000, 'wiki', 'tech', '向量检索原理',   [0.80, 0.10, 0.05, 0.05]);
```

### 9.3 KNN 向量检索

```sql
-- 余弦距离（默认）
SELECT * FROM knn(documents, embedding, [0.10, 0.20, 0.30, 0.40], 3);

-- L2 距离
SELECT * FROM knn(documents, embedding, [0.10, 0.20, 0.30, 0.40], 3, 'l2');

-- 内积距离
SELECT * FROM knn(documents, embedding, [0.10, 0.20, 0.30, 0.40], 3, 'inner_product');

-- 带标签过滤
SELECT * FROM knn(documents, embedding, [...], 5) WHERE source = 'wiki';

-- 带时间范围过滤
SELECT * FROM knn(documents, embedding, [...], 5)
WHERE time >= 1713676800000 AND time < 1713676805000;
```

### 9.4 向量距离函数

```sql
-- 函数调用形式
SELECT cosine_distance(embedding, [0.10, 0.20, 0.30, 0.40]) AS cos_dist,
       l2_distance(embedding, [0.10, 0.20, 0.30, 0.40])     AS l2_dist,
       inner_product(embedding, [0.10, 0.20, 0.30, 0.40])   AS dot_prod,
       vector_norm(embedding)                                AS norm
FROM documents WHERE source = 'wiki';

-- pgvector 兼容运算符形式
SELECT embedding <=> [0.10, 0.20, 0.30, 0.40] AS cos_dist FROM documents;
SELECT embedding <-> [0.10, 0.20, 0.30, 0.40] AS l2_dist FROM documents;
SELECT embedding <#> [0.10, 0.20, 0.30, 0.40] AS neg_inner_product FROM documents;
```

### 9.5 HNSW 索引

HNSW（Hierarchical Navigable Small World）是一种高效的 ANN 近似最近邻索引：

- **m**：每层最大邻居数（默认 16）
- **ef**：搜索广度（默认 200）
- v6 新段把索引 section 内嵌到 `.SDBSEG`，旧 `.SDBVIDX` 侧边文件仍可读取
- 自动退化为暴力搜索（无索引时）
- 支持精确率召回基准测试

---

## 10. 地理空间功能

### 10.1 创建含地理空间字段的表

```sql
CREATE MEASUREMENT vehicle (
    device   TAG,
    position FIELD GEOPOINT,
    speed    FIELD FLOAT
);
```

### 10.2 写入地理空间数据

```sql
INSERT INTO vehicle (time, device, position, speed) VALUES
    (1713676800000, 'car-1', POINT(39.9042, 116.4074), 0),
    (1713676860000, 'car-1', POINT(31.2304, 121.4737), 80);
-- POINT(lat, lon): 纬度在前，经度在后
```

### 10.3 坐标提取与距离计算

```sql
-- 提取经纬度
SELECT lat(position), lon(position) FROM vehicle;

-- 球面距离（米）
SELECT geo_distance(POINT(39.9042, 116.4074), POINT(31.2304, 121.4737)) AS distance_m;

-- 方位角（度）
SELECT geo_bearing(POINT(39.9042, 116.4074), POINT(31.2304, 121.4737)) AS bearing_deg;

-- PostGIS 兼容别名
SELECT ST_Distance(position, POINT(39.9042, 116.4074)) FROM vehicle;
```

### 10.4 空间范围过滤

```sql
-- 圆形半径过滤（查找上海 50km 内记录）
SELECT * FROM vehicle
WHERE geo_within(position, 31.2304, 121.4737, 50000);

-- ST_ 别名
SELECT * FROM vehicle
WHERE ST_Within(position, 31.2304, 121.4737, 50000);

-- 矩形范围过滤（华东区域）
SELECT * FROM vehicle
WHERE geo_bbox(position, 30.0, 113.0, 35.0, 122.0);
```

### 10.5 瞬时速度

```sql
-- 根据相邻点经纬度差与时间差计算速度（m/s）
SELECT time, device, geo_speed(position, time) AS speed_ms
FROM vehicle WHERE device = 'car-1';
```

### 10.6 轨迹聚合

```sql
-- 轨迹总长度
SELECT trajectory_length(position) AS total_m FROM vehicle;

-- 轨迹质心
SELECT trajectory_centroid(position) AS centroid FROM vehicle;

-- 轨迹包络矩形（JSON）
SELECT trajectory_bbox(position) AS bbox FROM vehicle;

-- 速度统计
SELECT trajectory_speed_max(position, time) AS max_ms,
       trajectory_speed_avg(position, time) AS avg_ms,
       trajectory_speed_p95(position, time) AS p95_ms
FROM vehicle;

-- 按时间桶分组轨迹聚合
SELECT trajectory_length(position), trajectory_speed_avg(position, time)
FROM vehicle GROUP BY time(2m);

-- 空间过滤 + 轨迹聚合联用
SELECT trajectory_length(position) FROM vehicle
WHERE geo_bbox(position, 30.0, 113.0, 35.0, 122.0);
```

### 10.7 GeoJSON 输出

```sql
-- REST API 端点返回 GeoJSON FeatureCollection
GET /v1/db/{db}/geo/{measurement}/trajectory
```

### 10.8 地图片段过滤（GeoHash）

SonnetDB 使用 GeoHash32 在 Segment 级别进行空间剪枝。每个 segment 的 block header 中存储了该 block 内所有点的 GeoHash 最小值和最大值。查询时，`geo_within` / `geo_bbox` 谓词会：

1. 计算查询范围的 GeoHash 区间
2. 跳过与查询区间无交集的 block
3. 在命中的 block 内逐点精确判断

这实现了类似空间索引的效果，无需额外构建空间索引文件。

---

## 11. PID 控制律

### 11.1 概述

SonnetDB 内置完整的 PID 控制算法，可作为时序数据的流式处理函数，适用于工业控制、过程监控等场景。

### 11.2 行级 PID

```sql
-- pid_series：逐行输出控制量
SELECT time, temperature,
       pid_series(temperature, 75.0, 0.6, 0.1, 0.05) AS valve_opening
FROM reactor WHERE device = 'r1';
```

### 11.3 桶级 PID 聚合

```sql
-- pid：每时间桶输出该桶末控制量
SELECT pid(temperature, 75.0, 0.6, 0.1, 0.05) AS valve
FROM reactor WHERE device = 'r1'
GROUP BY time(30s);
```

### 11.4 PID 参数自整定

```sql
-- Skogestad IMC 整定
SELECT pid_estimate(temperature, 'imc', 1.0, 0.1, 0.1, NULL)
FROM reactor WHERE device = 'r1';

-- Ziegler-Nichols 整定
SELECT pid_estimate(temperature, 'zn', 1.0, 0.1, 0.1, NULL)
FROM reactor WHERE device = 'r1';

-- Cohen-Coon 整定
SELECT pid_estimate(temperature, 'cc', 1.0, 0.1, 0.1, NULL)
FROM reactor WHERE device = 'r1';
```

**整定参数：**
- `method`：整定算法（'imc', 'zn', 'cc'）
- `step`：阶跃幅度
- `t_frac` / `t_settle`：暂态参数
- 返回 JSON 格式的整定结果

### 11.5 PID 控制器公式

```
输出(t) = Kp * e(t) + Ki * ∫e(τ)dτ + Kd * de/dt

其中：
  e(t) = setpoint - process_value
  Kp = 比例增益
  Ki = 积分增益
  Kd = 微分增益
```

---

## 12. 预测与异常检测

### 12.1 时间序列预测

`forecast()` 表值函数支持两种算法：

**线性预测：**

```sql
SELECT * FROM forecast(cpu, usage, 5, 'linear')
WHERE host = 'server-01';
```

**Holt-Winters 指数平滑：**

```sql
-- 无季节项
SELECT * FROM forecast(reactor, temperature, 6, 'holt_winters')
WHERE device = 'r1';

-- 带季节项（季节周期=5）
SELECT * FROM forecast(reactor, temperature, 6, 'holt_winters', 5)
WHERE device = 'r1';
```

**参数说明：**

| 参数 | 说明 |
|------|------|
| measurement | 表名 |
| field | 待预测 field 列 |
| horizon | 预测步数 |
| algorithm | 'linear' 或 'holt_winters' |
| season (可选) | 季节周期长度 |

### 12.2 异常检测

三种异常检测方法：

```sql
-- Z-Score 方法（阈值 2.0）
SELECT time, usage, anomaly(usage, 'zscore', 2.0) AS outlier
FROM cpu WHERE host = 'server-01';

-- MAD 方法（推荐，鲁棒性更强）
SELECT time, usage, anomaly(usage, 'mad', 2.5) AS outlier
FROM cpu WHERE host = 'server-01';

-- IQR 方法（Tukey 箱线图风格）
SELECT time, usage, anomaly(usage, 'iqr', 1.5) AS outlier
FROM cpu WHERE host = 'server-01';
```

**方法对比：**

| 方法 | 鲁棒性 | 适合场景 |
|------|--------|----------|
| zscore | 低（受离群值影响） | 数据质量好，已知分布 |
| mad | 高（不受离群值影响） | 通用场景，推荐 |
| iqr | 中 | 偏态分布 |

### 12.3 变点检测

```sql
-- CUSUM 算法
SELECT time, value,
       changepoint(value, 'cusum', 4.0) AS shift_detected
FROM signal WHERE source = 's-1';

-- 更保守的阈值
SELECT time, value,
       changepoint(value, 'cusum', 5.0, 0.5) AS shift_conservative
FROM signal WHERE source = 's-1';
```

**参数：**
- `field`：监测字段
- `'cusum'`：仅支持 CUSUM 算法
- `threshold`：检测灵敏度（越小越敏感）
- `drift` (可选)：允许的漂移容忍度

---

## 13. 用户自定义函数

### 13.1 概述

SonnetDB 支持通过 C# API 注册自定义函数，无需修改引擎代码。UDF 仅在嵌入式模式下可用。

### 13.2 注册 UDF

```csharp
using SonnetDB.Engine;
using SonnetDB.Query.Functions;

using var db = Tsdb.Open(options);

// 注册标量函数
db.Functions.RegisterScalar("multiply", (args) =>
{
    double a = (double)args[0]!;
    double b = (double)args[1]!;
    return a * b;
});

// 注册聚合函数
db.Functions.RegisterAggregate(new MyCustomAggregate());

// 注册窗口函数
db.Functions.RegisterWindow(new MyWindowFunction());

// 注册表值函数
db.Functions.RegisterTableValuedFunction("my_tvf", (args) => ...);
```

### 13.3 四种函数类型

| 类型 | 接口 | 说明 |
|------|------|------|
| 标量 | IScalarFunction | 逐行映射，输入→输出 |
| 聚合 | IAggregateAccumulator | 多行→单行聚合结果 |
| 窗口 | IWindowFunction | 滑动窗口逐行输出 |
| TVF | — | 返回多行结果集的表值函数 |

---

## 14. 嵌入式 API

### 14.1 基本用法

```csharp
using SonnetDB.Engine;
using SonnetDB.Sql.Execution;
using SonnetDB.Model;

// 打开数据库
using var db = Tsdb.Open(new TsdbOptions
{
    RootDirectory = "./my-data"
});

// 执行 SQL
var result = SqlExecutor.Execute(db,
    "CREATE MEASUREMENT cpu (host TAG, usage FIELD FLOAT)");

// 写入数据
SqlExecutor.Execute(db,
    "INSERT INTO cpu (time, host, usage) VALUES (1713676800000, 'server-01', 0.71)");

// 查询
var queryResult = (SelectExecutionResult)SqlExecutor.Execute(db,
    "SELECT time, host, usage FROM cpu WHERE host = 'server-01'");

foreach (var row in queryResult.Rows)
{
    Console.WriteLine($"{row[0]}: {row[2]}");
}
```

### 14.2 批处理写入

```csharp
// 批量写入
var points = new Point[]
{
    new Point(1000, new Dictionary<string, FieldValue>
    {
        ["usage"] = FieldValue.FromFloat64(0.71)
    }),
    new Point(2000, new Dictionary<string, FieldValue>
    {
        ["usage"] = FieldValue.FromFloat64(0.73)
    })
};
db.WriteMany(seriesId, points);
```

### 14.3 配置选项

```csharp
var options = new TsdbOptions
{
    RootDirectory = "./data",
    SegmentWriterOptions = new SegmentWriterOptions
    {
        FsyncOnCommit = true,
    },
};
```

---

## 15. ADO.NET 提供程序

### 15.1 安装

```xml
<PackageReference Include="SonnetDB" />
```

### 15.2 本地嵌入式连接

```csharp
using var conn = new SndbConnection("Data Source=./demo-data");
conn.Open();

using var cmd = conn.CreateCommand();
cmd.CommandText = "SELECT time, host, usage FROM cpu WHERE host = 'server-01'";

using var reader = cmd.ExecuteReader();
while (reader.Read())
{
    Console.WriteLine($"{reader.GetInt64(0)}: {reader.GetDouble(2)}");
}
```

### 15.3 远程 HTTP 连接

```csharp
var connStr = "Data Source=sonnetdb+http://127.0.0.1:5080/metrics;Token=<your-token>";
using var conn = new SndbConnection(connStr);
conn.Open();

using var cmd = conn.CreateCommand();
cmd.CommandText = "SELECT count(*) FROM cpu";
var count = cmd.ExecuteScalar();
```

### 15.4 连接字符串格式

| 模式 | 格式 | 说明 |
|------|------|------|
| 嵌入式 | `Data Source=./path` | 本地目录 |
| 远程 | `Data Source=sonnetdb+http://host:port/db;Token=xxx` | HTTP 远程 |

### 15.5 批量写入

```csharp
using var cmd = conn.CreateCommand();
cmd.CommandType = CommandType.TableDirect;
cmd.CommandText = "cpu";
// 通过 DataReader 方式写入
```

---

## 16. CLI 命令行工具

### 16.1 安装

```bash
dotnet tool install --global SonnetDB.Cli
```

### 16.2 基本用法

```bash
# 查看版本
sndb version

# 执行单条 SQL
sndb sql --connection "Data Source=./demo-data" \
         --command "SELECT count(*) FROM cpu"

# 交互式 REPL
sndb repl --connection "Data Source=./demo-data"

# 远程连接
sndb sql --connection "Data Source=sonnetdb+http://127.0.0.1:5080/metrics;Token=xxx" \
         --command "SHOW MEASUREMENTS"
```

### 16.3 配置文件

```bash
# 保存连接配置
sndb config set my-local "Data Source=./demo-data"
sndb config set my-remote "Data Source=sonnetdb+http://127.0.0.1:5080/metrics;Token=xxx"

# 使用配置
sndb sql --profile my-local --command "SELECT * FROM cpu"
```

---

## 17. HTTP REST API

### 17.1 数据面

```bash
# 执行 SQL
curl -X POST "http://127.0.0.1:5080/v1/db/metrics/sql" \
  -H "Authorization: Bearer <token>" \
  -H "Content-Type: application/json" \
  -d '{"sql":"SELECT * FROM cpu LIMIT 10"}'

# 批量 SQL
curl -X POST "http://127.0.0.1:5080/v1/db/metrics/sql/batch" \
  -H "Authorization: Bearer <token>" \
  -H "Content-Type: application/json" \
  -d '{"sqls":["CREATE MEASUREMENT ...","INSERT INTO ..."]}'
```

### 17.2 批量写入

```bash
# Line Protocol
curl -X POST "http://127.0.0.1:5080/v1/db/metrics/measurements/cpu/lp?flush=true" \
  -H "Authorization: Bearer <token>" \
  -H "Content-Type: text/plain" \
  -d 'cpu,host=server-01 usage=0.71 1713676800000'

# JSON 格式
curl -X POST "http://127.0.0.1:5080/v1/db/metrics/measurements/cpu/json" \
  -H "Authorization: Bearer <token>" \
  -H "Content-Type: application/json" \
  -d '[{"time":1713676800000,"usage":0.71}]'

# SQL VALUES 批量
curl -X POST "http://127.0.0.1:5080/v1/db/metrics/measurements/cpu/bulk" \
  -H "Authorization: Bearer <token>" \
  -H "Content-Type: application/json" \
  -d '{"columns":["time","usage"],"rows":[[1713676800000,0.71],[1713676860000,0.73]]}'
```

### 17.3 控制面

```bash
# 创建数据库
curl -X POST "http://127.0.0.1:5080/v1/db" \
  -H "Authorization: Bearer <token>" \
  -H "Content-Type: application/json" \
  -d '{"name":"metrics"}'
```

### 17.4 MCP 端点

```bash
# 获取可用工具列表
POST /mcp/{db}
# 工具：query_sql, list_measurements, describe_measurement,
#        list_databases, sample_rows, explain_sql,
#        docs_search, skill_search
```

### 17.5 端点到端汇总

| 端点 | 方法 | 说明 |
|------|------|------|
| `/v1/db/{db}/sql` | POST | 单条 SQL |
| `/v1/db/{db}/sql/batch` | POST | 批量 SQL |
| `/v1/db/{db}/measurements/{m}/lp` | POST | Line Protocol 批量 |
| `/v1/db/{db}/measurements/{m}/json` | POST | JSON 批量 |
| `/v1/db/{db}/measurements/{m}/bulk` | POST | VALUES 批量 |
| `/v1/db/{db}/geo/{m}/trajectory` | GET | 轨迹 GeoJSON |
| `/v1/setup/status` | GET | 安装状态 |
| `/v1/setup/initialize` | POST | 首次安装 |
| `/v1/db` | GET | 数据库列表 |
| `/v1/db` | POST | 创建数据库 |
| `/v1/sql` | POST | 控制面 SQL |
| `/healthz` | GET | 健康检查 |
| `/metrics` | GET | Prometheus 指标 |
| `/v1/copilot/chat` | POST | Copilot 对话 |
| `/v1/copilot/chat/stream` | POST | Copilot 流式对话 |
| `/v1/copilot/models` | GET | 模型列表 |
| `/v1/copilot/knowledge/status` | GET | 知识库状态 |
| `/v1/events` | GET | SSE 事件流 |
| `/mcp/{db}` | POST | MCP 协议 |

---

## 18. Web 管理后台

### 18.1 访问

浏览器访问 `http://127.0.0.1:5080/admin/`

### 18.2 功能页面

| 路由 | 功能 |
|------|------|
| `/` | 产品首页 |
| `/admin/setup` | 首次安装向导 |
| `/admin/login` | 登录 |
| `/admin/app/dashboard` | 仪表盘 |
| `/admin/app/sql` | SQL 控制台（多标签、文本/表格/图表/地图四视图） |
| `/admin/app/trajectory-map` | 轨迹地图（MapLibre + ECharts） |
| `/admin/app/databases` | 数据库管理 |
| `/admin/app/events` | SSE 事件流 |
| `/admin/app/users` | 用户管理 |
| `/admin/app/grants` | 授权管理 |
| `/admin/app/tokens` | Token 管理 |
| `/admin/app/ai-settings` | Copilot 设置 |

### 18.3 SQL 控制台四视图

SonnetDB 的 SQL 控制台支持四种结果展示模式：

1. **文本视图**：Markdown 格式的结果文本
2. **表格视图**：Naive UI NDataTable 分页表格
3. **图表视图**：自动检测时序数据并渲染 SVG 折线图
4. **地图视图**：自动检测 GEOPOINT 结果并渲染至 MapLibre 地图

---

## 19. Copilot AI 助手

### 19.1 概述

SonnetDB Copilot 是一个内建的 AI 助手，基于 Microsoft Agent Framework 构建，提供嵌入、聊天、知识库、技能库和 MCP 工具集成。

### 19.2 架构

```
用户输入 → Agent Orchestrator
              ↓
    ┌─────────┴──────────┐
    ↓                    ↓
  Knowledge Base      Skill Library
  (文档向量检索)       (6+ 内建技能)
    ↓                    ↓
  └─────────┬──────────┘
            ↓
    Response Generation
            ↓
    SQL Self-Correction
    (最多 3 次重试)
            ↓
        输出
```

### 19.3 嵌入提供程序

| 提供程序 | 说明 | 默认 |
|----------|------|------|
| BuiltinHashEmbedding | 零依赖，SHA-256+词袋哈希投影 384 维 | 默认 |
| LocalOnnxEmbedding | bge-small-zh-v1.5 ONNX 模型 | 可选 |
| OpenAICompatibleEmbedding | OpenAI 兼容 API | 可选 |

### 19.4 聊天提供程序

支持 OpenAI 兼容 API：
- OpenAI / Azure OpenAI
- DashScope（阿里通义千问）
- ZhiPu（智谱）
- Moonshot（月之暗面）
- DeepSeek

### 19.5 内置技能

| 技能 | 用途 |
|------|------|
| 查询聚合 | 帮助编写聚合查询 SQL |
| PID 调优 | PID 控制器参数建议 |
| 预测指南 | 如何使用预测功能 |
| 慢查询排查 | SQL 性能问题诊断 |
| Schema 设计 | Measurement 结构设计建议 |
| 批量写入 | 大规模数据导入方案 |

### 19.6 CopilotDock

Web 管理后台中的全局浮动 AI 面板：

- **拖拽/折叠/全屏**：灵活定位
- **页面感知**：自动感知当前页面上下文
- **读写模式切换**：只读浏览 / 可写执行
- **模型选择器**：切换不同 AI 模型
- **会话历史**：本地持久化，最多 50 个会话
- **SQL 发送**：对话中的 SQL 一键发送到控制台
- **权限审批**：写入操作需用户确认
- **启动模板**：7 类预设提示模板

### 19.7 MCP 工具

Copilot 通过 MCP（Model Context Protocol）暴露以下工具：

| 工具 | 说明 |
|------|------|
| `query_sql` | 执行 SQL 查询 |
| `list_measurements` | 列出所有 measurement |
| `describe_measurement` | 描述表结构 |
| `list_databases` | 列出数据库 |
| `sample_rows` | 采样数据行 |
| `explain_sql` | SQL 查询计划估计 |
| `docs_search` | 文档库检索 |
| `skill_search` | 技能库检索 |

---

## 20. 批量写入

### 20.1 概述

SonnetDB 支持三种批量写入格式，均通过 HTTP API 提供。

### 20.2 Line Protocol（类 InfluxDB）

```
cpu,host=server-01 usage=0.71 1713676800000
cpu,host=server-01 usage=0.73 1713676860000
cpu,host=server-02 usage=0.20 1713676800000
```

```bash
curl -X POST "http://127.0.0.1:5080/v1/db/metrics/measurements/cpu/lp?flush=true" \
  -H "Authorization: Bearer <token>" \
  -H "Content-Type: text/plain" \
  -d 'cpu,host=server-01 usage=0.71 1713676800000'
```

### 20.3 JSON 格式

```json
[
  {"time": 1713676800000, "usage": 0.71, "host": "server-01"},
  {"time": 1713676860000, "usage": 0.73, "host": "server-01"}
]
```

```bash
curl -X POST "http://127.0.0.1:5080/v1/db/metrics/measurements/cpu/json" \
  -H "Authorization: Bearer <token>" \
  -H "Content-Type: application/json" \
  -d '[{"time":1713676800000,"usage":0.71}]'
```

### 20.4 SQL VALUES 批量

```json
{
  "columns": ["time", "usage"],
  "rows": [
    [1713676800000, 0.71],
    [1713676860000, 0.73]
  ]
}
```

### 20.5 Flush 模式

| flush 参数 | 说明 |
|------------|------|
| `?flush=false` | 不触发 flush（默认） |
| `?flush=true` | 同步 flush，等待数据落盘 |
| `?flush=async` | 异步触发 flush，不等待 |

### 20.6 嵌入式批量

```csharp
// WriteMany 批量写入
db.WriteMany(seriesId, points);
```

性能：嵌入式 WriteMany 可达 **183 万点/秒** 写入速度。

---

## 21. 部署与运维

### 21.1 Docker 部署

```bash
# 生产部署
docker run -d --name sonnetdb \
  -p 5080:5080 \
  -v /data/sonnetdb:/data \
  -e SONNETDB__DATA__ROOT=/data \
  iotsharp/sonnetdb:latest
```

```yaml
# docker-compose.yml
version: '3'
services:
  sonnetdb:
    image: iotsharp/sonnetdb:latest
    ports:
      - "5080:5080"
    volumes:
      - ./data:/data
    environment:
      - SONNETDB__DATA__ROOT=/data
```

### 21.2 安装器

| 平台 | 格式 |
|------|------|
| Windows | `.msi` |
| Linux (Debian) | `.deb` |
| Linux (Red Hat) | `.rpm` |

### 21.3 NuGet 包

| 包名 | 说明 |
|------|------|
| SonnetDB.Core | 核心嵌入式引擎 |
| SonnetDB | ADO.NET 提供程序（命名空间 `SonnetDB.Data`） |
| SonnetDB.EntityFrameworkCore | Entity Framework Core Provider |
| SonnetDB.Cli | CLI 命令行工具 |

### 21.4 健康检查

```bash
curl http://127.0.0.1:5080/healthz
# 返回: Healthy
```

### 21.5 监控指标

```bash
curl http://127.0.0.1:5080/metrics
# Prometheus 格式指标输出
```

### 21.6 SSE 事件流

```bash
curl -N http://127.0.0.1:5080/v1/events
# 实时推送：指标、慢查询、数据库事件
```

### 21.7 数据生命周期

1. **写入**：WAL → MemTable
2. **Flush**：MemTable → 不可变 Segment
3. **压缩**：Size-Tiered 合并小 Segment
4. **保留**：TTL 自动过期删除
5. **删除**：Tombstone 标记，Compaction 回收

---

## 22. VS Code 扩展

### 22.1 概述

SonnetDB for VS Code 扩展（开发中）提供远程连接、SQL 执行、结果展示和 Copilot 面板。

### 22.2 规划功能

- 远程服务器连接管理器
- 数据库浏览器（树形视图）
- SQL 编辑器与执行
- 结果数据查看器
- Copilot AI 辅助面板
- 本地管理服务器

---

## 23. 性能基准

### 23.1 写入性能（100 万点，单序列）

| 场景 | 耗时 | 相对基线 | 内存 |
|------|------|----------|------|
| SonnetDB 嵌入式 WriteMany | **545 ms** | 1.00x | 530 MB |
| SQLite | 811 ms | 1.49x | 465 MB |
| InfluxDB 2.7 | 5,222 ms | 9.58x | 1,457 MB |
| TDengine 3.3 REST | 44,137 ms | 81x | 156 MB |
| TDengine schemaless LP | 996 ms | 1.83x | 61 MB |
| SonnetDB Server Bulk | 1.12 s | — | — |
| SonnetDB Server LP | 1.29 s | — | — |
| SonnetDB Server JSON | 1.35 s | — | — |

### 23.2 范围查询（~10 万行）

| 场景 | 耗时 |
|------|------|
| SonnetDB 嵌入式 | **6.71 ms** |
| SQLite | 44.54 ms |
| InfluxDB 2.7 | 411.13 ms |
| TDengine 3.3 | 56.29 ms |

### 23.3 聚合查询（16,667 桶）

| 场景 | 耗时 |
|------|------|
| SonnetDB 嵌入式 | **42.26 ms** |
| SQLite | 327.29 ms |
| InfluxDB 2.7 | 81.48 ms |
| TDengine 3.3 | 59.63 ms |

测试环境：i9-13900HX / Windows 11 / .NET 10.0.6 / Docker WSL2

---

## 24. 常见问题

### 24.1 如何创建数据库？

```sql
CREATE DATABASE metrics;
```

### 24.2 如何创建表？

```sql
CREATE MEASUREMENT cpu (host TAG, usage FIELD FLOAT);
```

### 24.3 支持哪些数据类型？

FLOAT、INT、BOOL、STRING、VECTOR(n)、GEOPOINT

### 24.4 如何删除数据？

使用 DELETE 语句（Tombstone 机制，不原地覆写）。

### 24.5 如何查看所有表？

```sql
SHOW MEASUREMENTS;
SHOW TABLES;
```

### 24.6 支持 OR 条件吗？

当前仅支持 AND 连接，不支持 OR。

### 24.7 如何向量搜索？

```sql
SELECT * FROM knn(documents, embedding, [0.1, 0.2, 0.3, 0.4], 5);
```

### 24.8 如何连接到远程服务器？

通过 ADO.NET：`Data Source=sonnetdb+http://host:5080/db;Token=xxx`

### 24.9 是否支持 AOT 发布？

是的，服务端项目原生 AOT 兼容，零反射零警告。

### 24.10 是否支持 Docker？

是，镜像发布在 `iotsharp/sonnetdb` 和 `ghcr.io`。

### 24.11 如何启用 Copilot？

在管理后台 `/admin/app/ai-settings` 中配置 AI 提供程序。

### 24.12 许可证是什么？

MIT 许可证，完全开源免费。

---

> © 2026 maikebing · SonnetDB v2.5.0 · MIT License
> GitHub: https://github.com/maikebing/SonnetDB
