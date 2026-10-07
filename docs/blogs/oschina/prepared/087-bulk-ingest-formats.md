## 批量摄入格式对比：Line Protocol vs JSON vs SQL VALUES

SonnetDB 支持 Line Protocol、JSON points 与 Bulk VALUES 三种批量载荷。选择应根据发送端的数据形态、类型表达、批大小和调试方式；没有同条件的基准时，不能用固定百分比或毫秒表宣称某种格式在所有场景胜出。

本文依据当前批量摄入合同整理，正式 4.0.0 与 main 的差异需要按实际版本核对。下面三种正文表达相同的两条传感器记录，专用 HTTP URL 中的数据库为 `metrics`、measurement 为 `sensor_data`。

### Line Protocol

```text
sensor_data,device_id=s01,location=factory-a temperature=23.5,humidity=65.2,pressure=1013.2 1713676800000
sensor_data,device_id=s02,location=factory-a temperature=24.1,humidity=63.8,pressure=1012.9 1713676801000
```

发送至 `POST /v1/db/metrics/measurements/sensor_data/lp`，Content-Type 为 `text/plain`。该专用端点按毫秒解释时间戳，并使用 URL 中的 measurement。Influx 兼容 `/write` 和 `/api/v2/write` 则允许每行不同 measurement，并默认纳秒，需要显式选择精度。

LP 是有类型标记的文本：裸数字是浮点，整数用 `42i`，布尔用 `true`，字符串用双引号；不是“所有值都没有类型”。measurement/tag/field key 中的空格、逗号、等号要按 reader 合同转义。

### JSON points

```json
{
  "m": "sensor_data",
  "points": [
    {"t":1713676800000,"tags":{"device_id":"s01","location":"factory-a"},"fields":{"temperature":23.5,"humidity":65.2,"pressure":1013.2}},
    {"t":1713676801000,"tags":{"device_id":"s02","location":"factory-a"},"fields":{"temperature":24.1,"humidity":63.8,"pressure":1012.9}}
  ]
}
```

发送至 `/v1/db/metrics/measurements/sensor_data/json`，Content-Type 为 `application/json`。实际键是 `m`、`t`、`tags`、`fields`，不是任意替换为 `measurement` 和 `time`。

这种时序 points 载荷适合应用已经持有字典对象的情况，但不等于文档模型的任意嵌套 JSON 摄入；嵌套业务文档应使用对应文档 API。

### Bulk VALUES 与普通 SQL

```sql
INSERT INTO sensor_data(device_id TAG, location TAG, temperature, humidity, pressure, time) VALUES
('s01', 'factory-a', 23.5, 65.2, 1013.2, 1713676800000),
('s02', 'factory-a', 24.1, 63.8, 1012.9, 1713676801000)
```

发送至 `/v1/db/metrics/measurements/sensor_data/bulk`，Content-Type 为 `text/plain`。这里使用批量 reader 快路径，所以不能一概说 SQL VALUES 都需要完整通用 SQL parser、一定是最慢路径。普通 `/sql` 与 `CommandType.Text` 才按 SQL 执行合同处理。

已有 schema 校验列角色和类型；新列默认按 FIELD 推断，包括字符串。要新增 TAG，应显式标注 `device_id TAG` 或预先建表，而不是仅靠字符串值自动判断。

### 对比与选择

| 维度 | Line Protocol | JSON points | Bulk VALUES |
| --- | --- | --- | --- |
| 数据组织 | 每行一点 | points 数组 | 一条 INSERT 多行 |
| 类型表达 | 数值后缀/引号/布尔标记 | JSON 类型 | SQL 字面量与 schema |
| 适合输入 | 指标采集与现有 LP 客户端 | 应用对象与 Web 服务 | SQL 生成工具和离线文件 |
| 常见错误 | 转义、整数后缀、精度 | 键名、数值/字符串混用 | 引号、列角色、字段顺序 |

三者都可通过 ADO.NET `CommandType.TableDirect` 提交正文，控制参数是 `measurement`、`onerror`、`flush`。HTTP 专用端点回执提供 `writtenRows`、`skippedRows`、`elapsedMilliseconds`，应与输入和查询对账。

`flush=async` 仅发后台信号，不提供固定 1 秒的数据丢失窗口。对坏行选择跳过，也不等于整批自动成功或整批原子事务。

比较性能时固定数据内容、输入行数/字节数、并发、网络、WAL 与 flush 配置，分别记录编码时间、请求时间、实际写入计数和内存峰值。先从小批次验证，再在目标环境增大；这样获得的数字才可用于容量规划。

参考：[批量写入](https://github.com/IoTSharp/SonnetDB/blob/main/docs/bulk-ingest.md)、[LP reader](https://github.com/IoTSharp/SonnetDB/blob/main/src/SonnetDB.Core/Ingest/LineProtocolReader.cs)、[正式 4.0.0](https://github.com/IoTSharp/SonnetDB/releases/tag/v4.0.0)。
