---
title: 案例：数据中心——服务器集群的指标采集与容量预测
categories: SonnetDB,监控,参考架构
draft: false
---

本文是服务器指标分析参考架构，使用合成数据，没有真实云厂商 SLA 或采购收益报告。目标是把资源观测、异常候选和趋势预测分开，让每一步都可以核验。

## 为指标固定单位

```sql
CREATE MEASUREMENT server_metrics (
    host TAG,
    datacenter TAG,
    role TAG,
    cpu_pct FIELD FLOAT,
    mem_pct FIELD FLOAT,
    disk_pct FIELD FLOAT
);

INSERT INTO server_metrics (time, host, datacenter, role, cpu_pct, mem_pct, disk_pct) VALUES
    (1700000000000, 'db-01', 'DC-A', 'db', 45, 60, 70),
    (1700086400000, 'db-01', 'DC-A', 'db', 48, 61, 71),
    (1700172800000, 'db-01', 'DC-A', 'db', 50, 62, 72);
```

百分比使用 0–100，不与 0–1 比例混用。主机、容器与应用指标还需保存稳定身份，处理重建和标签变化，避免把不同生命周期合成一条趋势。

## 观察历史与容量趋势

```sql
SELECT time, cpu_pct, mem_pct, disk_pct
FROM server_metrics
WHERE host = 'db-01'
ORDER BY time;

SELECT time, value, lower, upper
FROM forecast(server_metrics, disk_pct, 3, 'linear')
WHERE host = 'db-01';
```

这里的预测步长依据历史平均采样间隔，输出区间是算法构造的区间，不是容量必然耗尽日期。三条合成记录不能证明长期误差或生产可靠性。

## 告警需要状态与业务上下文

```sql
SELECT time, host, cpu_pct, mem_pct
FROM server_metrics
WHERE datacenter = 'DC-A' AND cpu_pct > 90
ORDER BY time;
```

阈值查询产生候选。实际告警还需持续时间、静默、合并、恢复和通知确认，避免每次轮询重复通知。MAD/IQR 等异常函数也需要足够历史与独立误报评测，不保证自动消除告警疲劳。

采样点分位数与请求尾延迟不同。对不同主机的采样数量不均时，直接汇总所有点可能让高频主机占更大权重；仪表盘应定义统计口径。

## 部署验证

采集器需要持久重试队列、明确确认与身份授权；数据库还需在目标硬件上测试写入、查询并发、保留维护与重启恢复。服务端端点与嵌入式成绩分开计量。

原稿中主机数量、预测提前量、误报下降与成本变化没有可核验原报告，本文不作为部署成绩。

参考：[预测与异常](https://github.com/IoTSharp/SonnetDB/blob/main/docs/forecast.md)、[数据模型](https://github.com/IoTSharp/SonnetDB/blob/main/docs/data-model.md)、[基准方法](https://github.com/IoTSharp/SonnetDB/blob/main/tests/SonnetDB.Benchmarks/README.md)。
