---
layout: default
title: "关系表与 measurement 类型边界"
description: "VECTOR/GEOPOINT 的模型范围、ADO.NET 能力元数据和关系实体替代模型。"
permalink: /relation-type-boundary/
---

# 关系表与 measurement 类型边界

GH-Issue #193 采用明确产品边界的选项：`VECTOR(N)` 与 `GEOPOINT` 是 measurement 的 `FIELD` 类型，当前不是关系表列类型。`CREATE TABLE`、`ALTER TABLE ADD COLUMN` 和 `ALTER COLUMN ... TYPE` 对二者确定性拒绝；失败不得生成半成品表或改变已有列和行。ORM/CodeFirst 必须明确拒绝这些关系属性，只有用户显式选择下述替代映射时才能转换。

## SQL 声明类型矩阵

| SQL 类型 | 关系表列 | measurement FIELD | CLR 值/说明 |
|---|---|---|---|
| `INT` | 支持 | 支持 | `long` |
| `FLOAT` | 支持 | 支持 | `double` |
| `BOOL` | 支持 | 支持 | `bool` |
| `STRING` | 支持 | 支持 | `string` |
| `DECIMAL(p,s)` / `NUMERIC(p,s)` | 支持 | 不支持 | `decimal`；元数据以 `DECIMAL` 为规范名称 |
| `DATETIME` | 支持 | 不支持 | UTC `DateTime`；measurement 保留列 `time` 不属于 FIELD |
| `TIME` | 支持 | 不支持 | `TimeOnly` |
| `BLOB` | 支持 | 不支持 | `byte[]` |
| `JSON` | 支持 | 不支持 | JSON 文本 |
| `VECTOR(N)` | **不支持** | **支持** | `float[]`，维度由 FIELD schema 显式声明 |
| `GEOPOINT` | **不支持** | **支持** | `SonnetDB.Model.GeoPoint`，WGS84 纬度/经度 |

该表描述 SQL 列声明，不将文档集合中的 JSON 数组或专用向量 API 的值解释为关系表 `VECTOR`。measurement 的 `TAG` 仍是字符串。向量距离/索引、地理函数和各数据模型的性能合同不因关系表替代映射而自动获得。

## ADO.NET 的可发现合同

`SndbConnection.GetSchema("DataTypes")` 保留原九行及全部标准列，在尾部增加 `VECTOR`、`GEOPOINT` 两行，并追加两个布尔列：

| 扩展列 | 含义 |
|---|---|
| `SupportsRelationalColumn` | 是否允许用于 `CREATE TABLE` / `ALTER TABLE` 的列声明 |
| `SupportsMeasurementField` | 是否允许用于 `CREATE MEASUREMENT ... FIELD` |

`VECTOR` 和 `GEOPOINT` 的两个标志分别为 `false` / `true`。两者使用 `ProviderDbType=DbType.Object`、`IsBestMatch=false`，不让通用 `DbType.Object` 被自动映射成某个专用关系类型；`IsSearchable=false` 不承诺标准关系比较谓词，专用向量/地理函数需遵循各自合同。`VECTOR` 的 `CreateFormat` 为 `VECTOR({0})`，`CreateParameters` 为 `dimension`；这是 FIELD 声明格式，不是关系列授权。

能力表由 **SDK 本地生成**，在嵌入式、REST 与 Frame 连接配置下内容相同，读取 `DataTypes` 本身不发网络请求；它描述当前 SDK 合同，不是远端 Server 版本协商。生成关系 DDL 的 provider 应先筛选 `SupportsRelationalColumn=true`。旧版 SDK 没有扩展列时，必须维持已知类型白名单，不能据此猜测新类型受支持。

`GetSchema("Tables")` / `GetSchema("Columns")` 返回实际关系目录：拒绝的表/列不会出现，measurement FIELD 也不会混入其中。远程连接获取该目录使用 `/v1/db/{db}/schema` 的 REST JSON 请求。

## 官方关系实体替代模型

普通实体可显式采用标量坐标加 JSON 向量载荷：

```sql
CREATE TABLE places (
    id INT,
    name STRING NOT NULL,
    latitude FLOAT NULL,
    longitude FLOAT NULL,
    embedding JSON NULL,
    PRIMARY KEY (id),
    CONSTRAINT ck_places_latitude CHECK (latitude >= -90 AND latitude <= 90),
    CONSTRAINT ck_places_longitude CHECK (longitude >= -180 AND longitude <= 180)
);

INSERT INTO places (id, name, latitude, longitude, embedding)
VALUES (1, 'Beijing', 39.9042, 116.4074, '[1.25,-0.5,3.0]');
```

此模型保留关系行、事务和键约束语义；经纬度是两个普通 `FLOAT`，向量是调用方显式编码的 JSON 文本。调用方负责坐标成对为空、向量维度、有限数值和模型版本校验。它不返回 `GeoPoint` / `float[]`，也不提供原生空间或向量索引。ORM 必须让用户显式配置这些属性映射，不得静默执行此转换。

如果需要原生 `GeoPoint` / `float[]` 与对应专用检索，关系实体可保留业务主数据，另建 measurement 保存观测：

```sql
CREATE MEASUREMENT place_samples (
    entity_id TAG,
    location FIELD GEOPOINT,
    embedding FIELD VECTOR(3)
);

INSERT INTO place_samples (time, entity_id, location, embedding)
VALUES (1000, '1', POINT(39.9042, 116.4074), [1.25,-0.5,3.0]);
```

`entity_id` 是应用维护的字符串标识，`time` 是观测时间；这一关联没有隐式跨模型外键、级联删除或原子双写保证。应用必须显式管理关联、删除、重试与补偿，不应把 companion measurement 命名为关系表向量列。

## 验收与传输边界

`tests/SonnetDB.Tests/RemoteRelationTypeBoundaryTests.cs` 覆盖嵌入式、真实 Kestrel REST/NDJSON 与 `Protocol=frame-http2`：

- 相同非法 DDL 重复执行产生相同错误，CREATE 无半成品，ALTER 后列定义和已写行仍保持原值；受支持关系类型继续写读。
- `DataTypes` 的模型能力、标准字段与返回类型一致，且本地 metadata 读取没有 HTTP 请求。
- measurement 入口实际参数化写读 `float[]` / `GeoPoint`，不作为关系目录返回。
- Frame 配置下 DDL/写入仍使用 REST SQL，实际关系目录仍使用 REST schema；只有测试 SELECT 经 `/v1/frame`。测试同时观察实际请求路径与 `HTTP/2`，不将 DDL 错误包装成帧 DDL 支持。

这是产品边界与本机真实 Server 集成合同，不是“关系表正式支持 VECTOR/GEOPOINT”的声明，也不代替固定硬件容量或生产网络证据。
