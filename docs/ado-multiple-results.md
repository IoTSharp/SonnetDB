# ADO 多 SELECT 与顺序多结果读取

GH-Issue #229 的当前源码支持单个 `SndbCommand.CommandText` 中以分号分隔的多条 SELECT。SQL lexer 识别字符串、双引号名称及已有注释中的分号；执行第一条之前完整解析全部语句，混入 DML、DDL 或事务控制会拒绝。每批最多 128 条 SELECT、1,048,576 个 SQL 字符，预检响应命令取消。

```csharp
using var command = connection.CreateCommand();
command.CommandText =
    "SELECT id FROM items WHERE id=@first; SELECT name FROM items WHERE id=@second";
command.Parameters.Add(new SndbParameter("first", 1L));
command.Parameters.Add(new SndbParameter("second", 2L));
using var reader = command.ExecuteReader();
while (reader.Read()) Console.WriteLine(reader.GetInt64(0));
if (reader.NextResult())
    while (reader.Read()) Console.WriteLine(reader.GetString(0));
```

`NextResult` / `NextResultAsync` 释放当前结果后才执行下一条，允许首组为空或提前切换。每组独立重置 HasRows、行位置和元数据；`GetName`、`GetFieldType`、`GetSchemaTable` 和列 schema 指向当前组。命名及位置参数在命令启动时冻结，后续更改参数集合或其字节/向量内容不会改变未执行的查询。位置参数按整个批次的出现顺序编号；嵌入式沿用名称优先、缺名按出现序号回退的既有规则，回退序号也跨结果组递增。远程继续严格按名称绑定，缺名不会因批次拆分而获得位置回退。

`DataSet.Load` 与 `DataTable.Load` 可装载包含文本的结果。对已经声明为 STRING、但没有声明长度上限的列，经典 `GetSchemaTable` 的 `ColumnSize` 返回 `-1`，供 DataTable 表示无最大长度；`GetColumnSchema().ColumnSize` 继续返回 `null`，保留未声明长度的信息。此适配也覆盖视图，未知类型等其它未知声明仍返回 `DBNull`，不把未知长度解释为 0 或伪造正长度。

第二条的语义或远程错误在切换/消费该组时按原通道抛出，不能静默丢弃该组或重试失败步骤。`ExecuteNonQuery` 对只读批次消费全部行集并返回 -1；`ExecuteScalar` 返回首组首值并消费剩余结果，因此后续错误也能暴露。`CommandBehavior.SingleResult` 只执行第一组，但仍完整预检。Close/Dispose 释放当前流和命令租约，`CloseConnection` 沿原行为关闭连接。超时、调用方取消和 `Cancel()` 沿现有命令/Reader 合同处理。

嵌入式、真实 REST 与 h2c Frame 复用已有单 SELECT 路径；每组是一次顺序查询，没有新增服务器批次协议。通过同一活动事务执行时复用既有事务会话；未显式开启事务的批次不承诺跨语句一致快照。当前验收范围是关系 SELECT，多模型原子批处理、DML 批次、存储过程和正式 FreeSql 包消费仍另行处理。正式可安装版本尚须随发布验证记录。
