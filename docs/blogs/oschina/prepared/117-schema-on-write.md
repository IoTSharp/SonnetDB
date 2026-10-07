---
title: SonnetDB 受控 Schema-on-Write：写入时自动补齐 Tag 与 Field
categories: SonnetDB,Schema,数据接入
draft: false
---

# SonnetDB 受控 Schema-on-Write：写入时自动补齐 Tag 与 Field

设备升级后增加字段，采集器开始上报新的身份维度，计数值从整数变成小数，这些变化不必都由接入端先执行 DDL。当前主分支提供受控的 measurement schema-on-write：写入时可以创建、扩列和执行兼容数值提升，同时保留角色、类型和增长额度校验。

本文依据当前主分支说明，SQL 和配置示例本次未执行。已发布的 4.0.0 标签不包含这里的 `MeasurementSchemaPolicy` 策略类型；不要把当前主分支的策略选项当作 4.0 安装包已经支持的合同。

## 首次写入和逐步演进

默认策略允许创建与演进，因此可使用：

```sql
INSERT INTO weather (time, station TAG, temperature, humidity)
VALUES (1713676800000, 'beijing', 28.5, 61.2);

INSERT INTO weather (time, station, firmware TAG, pressure)
VALUES (1713676860000, 'beijing', '1.2.0', 1008.5);

SHOW MEASUREMENTS;
DESCRIBE MEASUREMENT weather;
SELECT time, station, firmware, temperature, pressure
FROM weather ORDER BY time;
```

首行显式把 `station` 标为 TAG，两个数值列推断为 FLOAT FIELD。第二行已有列按已保存 schema 解释，新增 `firmware` 为 TAG、`pressure` 为 FLOAT FIELD。不同时间点可以缺少不同 FIELD，查询这些位置得到缺失值。

SQL 未知列默认推断为 FIELD，字符串也一样。字符串类型不能证明数据适合加入 series key；需要身份维度时显式写 `name TAG`。已有列的角色优先，提示与 schema 冲突会拒绝写入。Line Protocol 和 JSON points 本身区分 tags/fields，使用各自格式中的角色边界。

## 允许什么类型变化

INT FIELD 接收小数时可提升为 FLOAT；FLOAT FIELD 接收整数仍保持 FLOAT，写入值转换为浮点。FLOAT 不自动降回 INT。BOOL、STRING、VECTOR、GEOPOINT 与其他类型不能任意互转。

这并不意味着数据可以无限增长。`MeasurementSchemaPolicy` 提供三种模式：`Disabled` 拒绝自动创建和演进；`CreateOnly` 只允许自动创建；`CreateAndEvolve` 允许创建、扩列和兼容数值提升。

当前默认额度为：数据库最多 16,384 个 measurement，单 measurement 最多 256 个 TAG/FIELD 列，一次写入规划最多新增 32 列。业务可以明确设置更小的额度；这些默认值不是推荐容量或性能保证。

按 measurement 覆盖的策略映射使用区分大小写的名称，应采用已保存的原始拼写。SQL 普通名称的大小写绑定规则不等于配置字典的比较规则。策略在数据库打开时校验并复制，修改配置后需要重开数据库或重启相关服务实例。

## 持久化与批量失败边界

写入先规划、校验并持久化 schema，再写 WAL 与数据。schema 保存后、数据写入前失败，可能留下尚无数据的新列；这个顺序避免 WAL 中出现已写数据而持久 schema 没有对应定义。

`WriteMany` 在内部块中先检查角色、类型和 schema 变化，块内校验不通过时不会发布该块的候选 schema。超大调用按最多 8192 点分块，前序成功块不会因后续块失败而回滚。因此不能把一次任意大的 `WriteMany` 当作完整事务。

`MaxNewColumnsPerWrite` 针对一次 `Tsdb.Write` 或一个内部批量块。普通 SQL 多行 INSERT 当前逐行写入，各行分别计算该额度，后续行失败也不回滚前序成功行。需要整句配额和全有或全无导入，应另行设计并验证上层边界。

受控演进适合逐步变化的数据接入。身份维度、高基数字符串及对外稳定字段仍应经过 schema 设计，并结合额度、告警和接入验证使用。

参考：[策略定义](https://github.com/IoTSharp/SonnetDB/blob/main/src/SonnetDB.Core/Engine/MeasurementSchemaPolicy.cs)、[写入实现](https://github.com/IoTSharp/SonnetDB/blob/main/src/SonnetDB.Core/Engine/Tsdb.cs)、[4.0 正式版本](https://github.com/IoTSharp/SonnetDB/releases/tag/v4.0.0)。
