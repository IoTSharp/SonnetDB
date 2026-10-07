---
title: 案例：楼宇自动化——SonnetDB 助力智慧园区能耗管理
categories: SonnetDB,楼宇,参考架构
draft: false
---

本文是智慧园区能耗分析的参考架构，使用合成数据，没有真实园区节能收益报告。SonnetDB 保存计量与环境观测，应用负责系统接入、策略与告警，现场控制保留在设备侧。

## 区分功率与电量

功率 kW 是瞬时量，电量 kWh 是累计量。采集应用可从电表累计值计算相邻增量，但必须识别表计复位、换表、缺点和重复上传，不能直接把所有功率样本相加当电量。

```sql
CREATE MEASUREMENT energy_metrics (
    building_id TAG,
    zone_id TAG,
    system_type TAG,
    power_kw FIELD FLOAT,
    energy_delta_kwh FIELD FLOAT,
    temperature FIELD FLOAT,
    occupancy FIELD INT
);

INSERT INTO energy_metrics (time, building_id, zone_id, system_type, power_kw, energy_delta_kwh, temperature, occupancy) VALUES
    (1700000000000, 'B-A', '5F-EAST', 'hvac', 12, 1.0, 24, 20),
    (1700000300000, 'B-A', '5F-EAST', 'hvac', 15, 1.25, 24.5, 25);
```

这里的 `energy_delta_kwh` 是已由采集端核对的区间增量，不能与尚未校正的累计读数混用。

## 汇总与舒适度观察

```sql
SELECT sum(energy_delta_kwh) AS energy_kwh,
       avg(power_kw) AS average_kw,
       avg(temperature) AS average_temp
FROM energy_metrics
WHERE building_id = 'B-A'
  AND zone_id = '5F-EAST'
  AND system_type = 'hvac'
GROUP BY time(1h);
```

桶边界需要与区间电量归属一致。一个跨桶的计量区间不能未经分摊就随意放进任一桶；租户计费还需要合同、计量精度与审计规则。

## 规则留在应用中

```sql
SELECT time, power_kw, temperature, occupancy
FROM energy_metrics
WHERE zone_id = '5F-EAST' AND occupancy = 0 AND power_kw > 10
ORDER BY time;
```

这种查询产生“无人但仍有功率”的检查候选，不能直接等同于浪费。防冻、预冷、设备维护与传感器误差都可能解释结果。

计划运行、通知渠道和工单属于应用逻辑，不使用没有验证的 `CREATE ALERT` DSL。PID 等函数可用于回测，现场动作必须由控制系统执行自身限制与联锁。

## 验证节能效果

真实试验应同时记录天气、人数、使用计划和舒适度，按可比较基线评价能量变化。预测区间与异常信号应先用历史标签评测，不能据一段 SQL 宣称节电率或年度金额。

原稿中的楼宇规模、电费、投诉下降与节能百分比缺少客户原报告，本文不作为上线事实。

参考：[SQL 参考](https://github.com/IoTSharp/SonnetDB/blob/main/docs/sql-reference.md)、[PID 指南](https://github.com/IoTSharp/SonnetDB/blob/main/docs/pid-control.md)、[数据模型](https://github.com/IoTSharp/SonnetDB/blob/main/docs/data-model.md)。
