---
layout: default
title: 预测 / 异常 / 变点检测
permalink: /forecast/
---

# 预测 / 异常 / 变点检测

SonnetDB 在 PR #55 中提供了三组开箱即用、纯 C# 实现、零外部依赖的时序分析能力：

| 能力 | 形态 | API / SQL |
|------|------|-----------|
| 短期预测 | 表值函数 (TVF) | `forecast(measurement, field, horizon, 'algo'[, season])` |
| 异常检测 | 窗口函数 | `anomaly(field, 'zscore'\|'mad'\|'iqr', threshold)` |
| 变点检测 | 窗口函数 | `changepoint(field, 'cusum', threshold[, drift])` |

更高级 / 定制化的 ARIMA、Prophet、深度学习模型等保留给 **Tier 5 UDF**（PR #56）注册扩展。

---

## 1. `forecast` 表值函数

`forecast` 以**表值函数**形态出现在 `FROM` 子句中，按选定 measurement / field 拉取历史样本并向后外推
若干个采样间隔，返回 `(time, value, lower, upper)` 四列 + 维度标签列。

### 1.1 SQL 语法

```sql
SELECT time, value
FROM forecast(<measurement>, <field>, <horizon>, '<algo>'[, <season>])
WHERE <tag_filter>;
```

| 参数 | 说明 |
|------|------|
| `measurement` | 待预测的 measurement 名（裸标识符）。 |
| `field` | 数值型 FIELD 列（裸标识符）。 |
| `horizon` | 向后外推的步数（正整数字面量），步长 = 历史数据平均采样间隔。 |
| `algo`     | `'linear'`（最小二乘线性外推）或 `'holt_winters'` / `'hw'`（Holt / Holt-Winters 三次指数平滑）。 |
| `season` | 可选；季节周期长度（采样点数）。仅 `holt_winters` 使用，`0` 或省略时退化为 Holt 双重指数平滑。 |

> `forecast` 可使用 `SELECT *` 返回完整稳定列集合，也可投影输出列名，例如
> `SELECT time, value FROM forecast(...)`。表达式投影仍应放在外层查询中完成。
> WHERE 中的标签过滤仍然按通常方式工作，可用于按设备 / 区域分别预测。

### 1.2 输出列

| 列 | 类型 | 说明 |
|----|------|------|
| `time` | 时间戳 (ms) | 预测点对应时间，从最后一个历史样本之后开始递增。 |
| `value` | double | 预测均值。 |
| `lower` | double | 95% 置信区间下界。 |
| `upper` | double | 95% 置信区间上界。 |
| `<tag1>`, `<tag2>`, … | string | 维度标签列，标识该序列。 |

### 1.3 示例

线性外推未来 5 个采样点：

```sql
SELECT time, value, lower, upper
FROM forecast(cpu, usage, 5, 'linear')
WHERE host = 'web-01';
```

Holt-Winters 加性季节预测，季节周期 24（如 24 小时数据）：

```sql
SELECT time, value, device
FROM forecast(temperature, value, 12, 'holt_winters', 24)
WHERE device = 'sensor-A';
```

### 1.4 算法说明

#### 线性外推 (`linear`)

对历史 $(t_i, y_i)$ 用最小二乘拟合 $y = a + b \cdot x$（$x$ 单位秒），
对未来 $h+1$ 步输出 $\hat{y}_{n+h+1}$；置信区间宽度为 $z_{0.975} \cdot \sigma_{\text{residual}} \cdot \sqrt{h+1}$，
$z_{0.975} = 1.96$。

#### Holt-Winters (`holt_winters` / `hw`)

固定平滑系数 $\alpha = 0.4$, $\beta = 0.1$, $\gamma = 0.2$。
当 `season > 1` 且历史数据 $\ge 2 \cdot \text{season}$ 时启用三次指数平滑（加性季节）：

$$\begin{aligned}
L_t &= \alpha (y_t - S_{t-s}) + (1-\alpha)(L_{t-1} + B_{t-1}) \\
B_t &= \beta (L_t - L_{t-1}) + (1-\beta) B_{t-1} \\
S_t &= \gamma (y_t - L_t) + (1-\gamma) S_{t-s}
\end{aligned}$$

否则退化为 Holt 双重指数平滑（无季节项）。
预测值 $\hat{y}_{n+h+1} = L_n + (h+1) B_n + S_{n+h+1-s}$，
置信区间宽度按残差均方根误差 × $z_{0.975}$ × $\sqrt{h+1}$ 给出。

> 季节性偏弱、采样过短（< 2 个完整季节）或残差极小（常数序列）时，
> 输出区间会非常窄甚至为零。请结合业务先验判断。

---

## 2. `anomaly` 窗口函数

`anomaly` 是 [Tier 3 窗口函数](/sql-reference/#tier-3-窗口函数)，对每一行输出 `bool`，
**不**改变行数，缺失输入返回 `null`。

```sql
SELECT time, usage, anomaly(usage, 'zscore', 2.0) AS is_outlier
FROM cpu
WHERE host = 'web-01' AND time > now() - 1h;
```

| 方法 | 公式 | 适用场景 |
|------|------|---------|
| `'zscore'` | $z_i = \dfrac{x_i - \mu}{\sigma}$，触发当 $|z_i| >$ threshold | 近似正态、无极端离群点。 |
| `'mad'`    | $\text{score}_i = \dfrac{|x_i - \tilde{x}|}{1.4826 \cdot \text{MAD}}$ | 鲁棒，对单个离群点不敏感。**推荐**。 |
| `'iqr'`    | 触发当 $x_i < Q_1 - k \cdot \text{IQR}$ 或 $x_i > Q_3 + k \cdot \text{IQR}$ | 偏态分布、箱线图风格。 |

`threshold` 单位：

* `zscore` / `mad`：σ 的倍数。常用 `2.0` ~ `3.0`。
* `iqr`：IQR 的倍数（即 Tukey $k$）。常用 `1.5` ~ `3.0`。

> 注意：`zscore` 对小样本中的极端离群点鲁棒性差，**离群点本身会拉大 σ**，
> 阈值往往需要取较小值（如 2.0）才能触发。生产场景推荐使用 `mad` 或 `iqr`。

---

## 3. `changepoint` 窗口函数

`changepoint` 是 [Tier 3 窗口函数](/sql-reference/#tier-3-窗口函数)，
对每一行输出 `bool`，标识该时刻是否检测到分布发生显著漂移。

```sql
SELECT time, value, changepoint(value, 'cusum', 4.0) AS shift_detected
FROM signal
WHERE source = 's-1';
```

### 3.1 算法：CUSUM (累积和)

* 用前 `max(5, n/4)` 个非空样本估计基线均值 $\mu_0$ 和样本标准差 $\sigma_0$，
  避免变点本身污染参考。
* 双边累积和：

  $$S^+_t = \max(0,\ S^+_{t-1} + (x_t - \mu_0) - k\sigma_0)$$
  $$S^-_t = \min(0,\ S^-_{t-1} + (x_t - \mu_0) + k\sigma_0)$$

* 当 $S^+_t > h\sigma_0$ 或 $S^-_t < -h\sigma_0$ 时输出 `true` 并将累积器复位，
  以便探测下一个变点。

### 3.2 参数

| 参数 | 说明 |
|------|------|
| `field` | FIELD 列。 |
| `'cusum'` | 当前唯一支持的方法。 |
| `threshold` | 触发阈值 $h$（单位：倍 $\sigma_0$）。常用 `3.0` ~ `5.0`。值越大越保守。 |
| `drift` | 可选；漂移容忍度 $k$（单位：倍 $\sigma_0$），默认 `0.5`。值越大对小幅漂移越不敏感。 |

> 要求至少 4 个非空样本；若全部样本恒等（$\sigma_0 = 0$），所有非空行均输出 `false`。

---

## 4. 嵌入式 API

预测算法核心暴露在 `SonnetDB.Query.Functions.Forecasting.TimeSeriesForecaster`：

```csharp
using SonnetDB.Query.Functions.Forecasting;

long[] timestampsMs = ...;
double[] values = ...;

ForecastPoint[] result = TimeSeriesForecaster.Forecast(
    timestampsMs,
    values,
    horizon: 12,
    algorithm: ForecastAlgorithm.HoltWinters,
    season: 24);

foreach (var p in result)
{
    Console.WriteLine($"{p.TimestampMs} {p.Value:F3} [{p.Lower:F3}, {p.Upper:F3}]");
}
```

异常 / 变点的窗口函数则通过 SQL 即可使用，无需额外 API。

---

## 5. 局限与后续

* `forecast` 当前仅支持线性 / Holt-Winters。**ARIMA / Prophet / 神经网络** 留给 PR #56 的 UDF 扩展。
* `anomaly('zscore', …)` 在小样本极端离群点下需要较低阈值；MAD / IQR 方法更鲁棒。
* `changepoint` 只实现 CUSUM；BCPD、PELT 等方法可通过 UDF 扩展。
* TVF 支持 `SELECT *` 或直接投影 `time` / `value` / `lower` / `upper` / tag 输出列；更复杂的表达式投影可通过外层查询扩展。
