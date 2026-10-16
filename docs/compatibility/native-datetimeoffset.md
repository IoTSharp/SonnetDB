# 原生 DATETIMEOFFSET

`DATETIMEOFFSET` 是独立的 SQL 声明类型。通用 ADO.NET `GetValue`、`GetValues`、`GetFieldType`、列 schema 及 `GetFieldValue<DateTimeOffset>` 返回 `DateTimeOffset`；空结果与全 NULL 结果保留同样的声明。已有 `DATETIME` 仍映射 `DateTime`。

## 值与参数

- 新类型保存 UTC ticks，精度为 100ns，范围从 `DateTimeOffset.MinValue` 到 `DateTimeOffset.MaxValue`。返回值的 `Offset` 统一为零；原始 offset 不保存。
- 带 offset 的日期文本按其 instant 转 UTC，无 offset 的日期文本按 UTC 解释。时间文本必须含日期。整数显式 `CAST(... AS DATETIMEOFFSET)` 表示从公元 1 年起的 UTC ticks。
- 默认 CLR `DateTimeOffset` 参数及显式 `DbType.DateTimeOffset` 参数保留完整 ticks；该 DbType 的 NULL 参数具有原生声明。
- 显式 `DbType.DateTimeOffset` 接收 `DateTime` 时，UTC 保持，Unspecified 按 UTC，Local 在客户端转 UTC。服务器不重新解释客户端 Local 值。
- `CURRENT_DATETIME_OFFSET`、`CURRENT_UTC_DATETIME_OFFSET` 和 `DATE_ADD_DATETIME_OFFSET` 返回原生 UTC 时刻。`DATE_ADD` 接收原生时刻时保留原生类型；`DATE_ADD_DATETIME` 继续返回 `DateTime`。

```sql
CREATE TABLE Events(Id INT, Occurred DATETIMEOFFSET NULL, PRIMARY KEY(Id));
INSERT INTO Events VALUES(1, CAST('2026-10-08T20:34:56.1230001+08:00' AS DATETIMEOFFSET));
SELECT Occurred FROM Events
WHERE Occurred = CAST('2026-10-08T12:34:56.1230001Z' AS DATETIMEOFFSET);
```

相同 instant 的不同 offset 相等，相邻 ticks 不相等。主键、唯一索引、等值查找、排序、分组、DISTINCT 和连接遵循同一精确比较；MIN/MAX 返回原生值。两个原生时刻相减返回 DURATION。范围条件可使用扫描，不承诺新增范围索引优化。

## 旧 DATETIME 兼容边界

**默认 DateTimeOffset 参数用于旧 DATETIME 谓词会被拒绝。** 原生时刻不能隐式与 DATETIME 或 Unix 毫秒整数比较，也不能通过 CASE、COALESCE、UNION 或 VALUES 混成同一声明。调用者需显式选择目标类型；空表、全 NULL、LIMIT 0、布尔短路和索引候选均不豁免此检查。

对旧 DATETIME 列或旧服务器，可设置参数 `DbType.DateTime` 或 `DbType.DateTime2`。客户端先把 DTO 转 UTC DateTime，然后走原有毫秒参数合同：

```csharp
command.Parameters.Add(new SndbParameter("stamp", instant)
{
    DbType = DbType.DateTime2,
});
```

旧 DATETIME 目标列的写入转换仍可接受 DateTimeOffset 并按旧存储精度截断。此转换不使谓词获得隐式兼容性。默认 DateTime 参数及 DATETIME 文件布局仍有既有毫秒边界；本变更不代表 #236 已完成。

## Entity Framework Core 映射

EF Provider 默认将 CLR `DateTimeOffset` 属性声明为 `DATETIMEOFFSET`，保留完整 UTC ticks；`DateTime` 仍声明为 `DATETIME`。生成的 DTO 字面量显式使用 `CAST(... AS DATETIMEOFFSET)`，参数保持原生 `DbType.DateTimeOffset`。

升级已有 EF 应用时，原数据库中的 `DATETIME` 列不会自动改变声明。若继续使用旧布局，应在模型中显式配置 `HasColumnType("DATETIME")`。Provider 对这种 DTO 属性的查询列生成 `CAST(column AS DATETIMEOFFSET)`，使列、参数、常量和日期函数具有一致的表达式类型；写入仍按旧 `DATETIME` 毫秒精度保存，不能恢复历史已截断的 ticks。查询列的 CAST 可能使旧列索引无法用于该表达式，不承诺原生列相同的查询计划。

如果要迁移为完整 ticks 存储，必须明确执行并验收列迁移，同时更新 EF 模型和迁移快照；本次映射修复不会自动执行 DDL，也不代表事务 DDL #227 已完成。显式旧布局的查询适配要求新引擎支持 `DATETIMEOFFSET`，不承诺新版 EF Provider 可连接旧版 Server。

ADO.NET `GetSchema("DataTypes")` 同时列出 `DATETIME`、`DATETIMEOFFSET`、`TIME` 和 `DURATION`；新增的 `DATETIMEOFFSET` 和 `DURATION` 均不作为 measurement FIELD 类型开放。

## 持久化与传输

使用该类型的 catalog 永久提升到 v15，升级屏障先于新行及迁移日志写入；删除最后一个新列也不降级。旧引擎必须拒绝 v15，不能直接降级打开。物化快照含原生声明时使用 v4，包括空结果；旧快照仍可读。SQL 临时 spill 复用已有 DateTimeOffset 编码。

REST/Frame 使用 SQL 结果 v6。Frame 的声明标记为 5、列类型为 11、值标签为 12，值为 8 字节 little-endian UTC ticks；REST 使用 `{"$type":"datetimeoffset","ticks":639270596961230001}`。缺少协商、越界、截断、重复或额外属性均拒绝。旧结果版本遇到原生声明会拒绝，包括空结果、NULL、DML RETURNING，以及会消耗序列的过程调用。

新客户端仅在明确的执行前 `unsupported_result_version` 错误时降级：REST v6→v5→v4，Frame v6→v5→v4→v3→v2。普通 SQL 错误或不确定传输错误不重放。v5 仍可使用 DURATION；显式 DbType.Time 的一天内 TimeSpan 行为保留。

## Chloe 候选边界

最终验证候选以 Chloe `bb7ec8735bd4511c35453eb009f70cc606795703` 为基线，依次应用 #262 的六文件日期补丁、#252 的五文件补丁和本项三文件 DDL/CAST/guard 补丁。框架原 `DateTimeOffset_ValueConverter` 不修改。映射和通用物化可用不代表 DateTimeOffset 的全部 CLR 属性/方法都已具备表达式翻译；例如原 handler 对 DateTime MemberInfo 的匹配不会自动扩展到 DateTimeOffset。

候选源码、源码宿主与正式发行的证据须分别记录。正式 SonnetDB 包和 Chloe 上游交付状态以 Issue #251 的核验记录为准。
