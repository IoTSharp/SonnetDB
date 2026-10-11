---
title: 案例：光伏电站运维——基于 SonnetDB 的发电量异常检测
categories: SonnetDB,光伏,参考架构
draft: false
---

本文用合成逆变器数据说明光伏运维分析方法，不对应真实电站部署或增收成果。目标是把功率、辐照、温度和位置保存为可追溯输入，再由业务规则识别需要检查的设备。

## 建立正确单位的 schema

```sql
CREATE MEASUREMENT inverter_metrics (
    inverter_id TAG,
    station_id TAG,
    ac_power_kw FIELD FLOAT,
    capacity_kw FIELD FLOAT,
    irradiance_wm2 FIELD FLOAT,
    temperature FIELD FLOAT,
    location FIELD GEOPOINT
);

INSERT INTO inverter_metrics (time, inverter_id, station_id, ac_power_kw, capacity_kw, irradiance_wm2, temperature, location) VALUES
    (1700000000000, 'INV-01', 'STATION-A', 42, 50, 800, 35, POINT(39.90, 116.40)),
    (1700000001000, 'INV-01', 'STATION-A', 43, 50, 810, 35.5, POINT(39.90, 116.40)),
    (1700000000000, 'INV-02', 'STATION-A', 25, 50, 800, 36, POINT(39.901, 116.401));
```

容量必须是正值，功率和辐照单位在字段名中明确，坐标使用纬度在前的 `POINT`。

## 先做条件相近的比较

```sql
SELECT time, inverter_id, ac_power_kw / capacity_kw AS capacity_fraction
FROM inverter_metrics
WHERE station_id = 'STATION-A' AND irradiance_wm2 >= 700
ORDER BY time;
```

这里是额定容量归一化的瞬时功率比例，不是完整的 Performance Ratio。正式 PR 指标还需按能量、辐照累计、参考效率等一致合同计算，不能任意乘一个面积常数就当作电站效率。

同一时段也要核对朝向、遮挡、组件与采集质量。低功率可以产生检查候选，不能单独判定热斑或设备故障。

## 趋势与异常的分工

```sql
SELECT avg(ac_power_kw) AS average_kw,
       max(ac_power_kw) AS peak_kw,
       avg(temperature) AS average_temp
FROM inverter_metrics
WHERE inverter_id = 'INV-01'
GROUP BY time(1m);
```

有足够历史样本后，可按文档选择 `anomaly` 或预测函数。先定义同设备、同工况的基线，再标注误报与漏报；合成样例不构成算法准确率证据。

功率是瞬时量，发电量需要按真实采样时间积分或使用电表累计值。缺点、变采样周期和重复记录会让简单 `sum(power) * 固定周期` 出错。

## 运维闭环

地图呈现、告警通知和维修工单由上层应用完成。GeoJSON 坐标转换采用经度在前，不能直接复用 SQL POINT 顺序。维修前后对比还要控制天气与设备工况，避免把自然变化算成维护收益。

原稿中的电站数量、容量、损失下降与年度收益没有原证据，本文不引用。

参考：[空间指南](https://github.com/IoTSharp/SonnetDB/blob/main/docs/geo-spatial.md)、[预测与异常](https://github.com/IoTSharp/SonnetDB/blob/main/docs/forecast.md)、[SQL 参考](https://github.com/IoTSharp/SonnetDB/blob/main/docs/sql-reference.md)。
