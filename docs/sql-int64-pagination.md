# Int64 SQL 分页合同

`LIMIT`、`OFFSET` 和 `OFFSET … ROWS FETCH NEXT … ROWS ONLY` 的已绑定位置及数量保留非负 `Int64`，范围为 `0..9223372036854775807`。命名和位置参数使用相同规则。超过 `Int64.MaxValue`、负数、NULL、字符串、浮点数和 Decimal 不会被截断或转为可用窗口。

```sql
SELECT id FROM items LIMIT 1 OFFSET 2147483648;
SELECT id FROM items LIMIT 9223372036854775807 OFFSET 1;
SELECT id FROM items OFFSET @skip ROWS FETCH NEXT @take ROWS ONLY;
```

小表遇到超出实际行数的 OFFSET 返回空结果。`LIMIT 0` 返回零行，省略 LIMIT 表示没有分页数量限制；空结果保留关系列声明。`OFFSET Int64.MaxValue` 与非零 LIMIT 组合不会因位置相加而溢出、绕回或预分配该大小的数组。

分页范围与实际资源预算独立。`MaxMaterializedRows`、`MaxMaterializedBytes`、阻塞算子内存及外排预算继续按实际候选与结果计费，大 LIMIT 不提高任何预算。流式执行用 Int64 跳过/剩余计数；只有实际集合边界内的下标才转为 Int32。索引/Top-N 优化只在完整前缀能精确表示为 Int32 时下推；其余排序使用既有完整排序或受预算约束的外排路径。文档、图、向量等模型原有查询形状、候选数和资源限制仍适用。

公开 `PaginationSpec.Offset` 从 `int` 改为 `long`，`Fetch` 从 `int?` 改为 `long?`，属于 **CLR 属性签名兼容性变更**。保留原 `(int, int?)` 构造函数，并新增 `(long, long?)`；直接使用属性的已编译消费者须重新编译，源码中接收属性的 Int32 变量也须改用 Int64 或在验证实际边界后显式转换。SQL 文本和原 Int32 参数用法兼容；不改变持久化文件格式或协议版本。

XCode 的 `PageSplit` 约定 `maximumRows=0` 表示不限制行数，与 SQL `LIMIT 0` 不同。本地 XCode 候选只移除原来的 Int32 范围保护，保留这一约定及原有非正参数处理。候选验收不能替代上游合并、依赖更新和正式发行。

源码验收见 [#256 核查报告](audits/issue-256-int64-pagination-20261016.md)。正式 Core/Data/Server 包的最低支持版本仍待发布核验，因此 #256 继续保持开放。
