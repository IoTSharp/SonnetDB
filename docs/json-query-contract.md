---
layout: default
title: "SQL JSON 查询合同"
description: "JSON 标量、数组包含、参数路径、资源预算与索引协作。"
permalink: /json-query-contract/
---

# SQL JSON 查询合同（GH-Issue #180）

`json_exists(json, path)`、`json_array_length(json, path)` 和 `json_contains(json, path, candidate)` 可用于关系表 `JSON` 列、文档集合的 `document` / `json` 伪列，以及字符串表达式。它们沿用 SQL 函数和 ADO.NET 参数接口；不需要 PostgreSQL JSONB 运算符。

```sql
SELECT id, json_array_length(metadata, @tags_path) AS tag_count
FROM devices
WHERE json_exists(metadata, @first_path)
  AND json_contains(metadata, @tags_path, @candidate);
```

将 `tags_path` 绑定为 `$.tags`，`first_path` 绑定为 `$.tags[0]`，`candidate` 绑定为普通字符串 `industrial` 即可查找标签。不要给普通字符串候选额外加 JSON 双引号。ADO.NET 使用 `command.Parameters.AddWithValue("@candidate", "industrial")`；SQL 字符串中的单引号由驱动参数绑定处理。

## path 和输入类型

JSON 和 path 参数必须为 SQL `STRING` 或 `NULL`；关系 `JSON` 列读取为 JSON 文本并使用相同解析器。path 可以是 SQL 字符串字面量或绑定的字符串参数。参数在执行前绑定，嵌入式、REST 和 Frame 使用相同的 JSON path 语法。

支持根 `$`、点属性 `$.owner.name`、引用属性 `$['display-name']` 和显式非负数组下标 `$.tags[0]`；它们可以组合。引用属性中的单引号按两个单引号表示，例如把 path 参数绑定为 `$['O''Reilly']` 可读取 `O'Reilly` 属性。属性名和字符串值均按大小写敏感的 ordinal 规则比较。通配符、负数下标、递归下降和隐式数组 fan-out 不属于这些标量函数的 path 合同；无效 path 返回执行错误。路径存在与否与值是否为 JSON `null` 是两个不同问题。

## NULL、数组、对象和标量

| 输入情况 | `json_exists` | `json_array_length` | `json_contains` |
|---|---|---|---|
| JSON 或 path 为 SQL `NULL` | SQL `NULL` | SQL `NULL` | SQL `NULL` |
| path 不存在 | `FALSE` | SQL `NULL` | `FALSE` |
| path 存在且值为 JSON `null` | `TRUE` | SQL `NULL` | `FALSE` |
| path 指向空数组 | `TRUE` | `0`（`BIGINT`） | 空数组候选为 `TRUE`；普通标量候选为 `FALSE` |
| path 指向非空数组 | `TRUE` | 元素数量（`BIGINT`） | 按下述包含规则比较 |
| path 指向对象、字符串、数字或布尔值 | `TRUE` | 执行错误 | 按下述包含规则比较 |
| candidate 为 SQL `NULL` | 不适用 | 不适用 | SQL `NULL` |

表中 NULL 传播以 JSON/path 参数类型合法为前提：先检查它们是否为字符串或 NULL，再判断 NULL 传播。NULL 传播后不解析 JSON/path 内容；`json_contains` 对缺失路径或 JSON null 目标返回 FALSE 时，也不继续解析容器候选。因此这些函数不是独立输入验证器，例如缺失路径配合未闭合的候选 `[` 仍返回 FALSE。

包含规则：

- 普通 SQL 字符串候选保持字符串身份。`industrial` 匹配 JSON 字符串 `"industrial"`，`'1'` 不匹配 JSON 数字 `1`。SQL 数字匹配数值相等的 JSON 数字，SQL 布尔值匹配同值 JSON 布尔值。
- 对实际进入比较的候选字符串，去除前导空白后若以 `{` 或 `[` 开头，则作为 JSON 对象或数组解析；非法 JSON 候选返回执行错误。JSON 字符串候选的 Unicode 转义会在该容器解析中还原，如 `["\u0061"]` 与 `["a"]` 匹配。
- 对象候选按字段子集递归匹配，字段顺序不影响结果。例如 `{"active":true}` 匹配含同值 `active` 字段的对象，允许目标含额外字段。对象目标加普通字符串候选还可检查字段名是否存在；字段值为 JSON `null` 仍算存在。
- 数组候选按不计顺序的子集匹配，但每个目标数组元素只能匹配一次。因此 `[1]` 不包含 `[1,1]`，`[1,1]` 包含 `[1,1]`。对象数组可用对象候选或包含对象的数组候选执行字段子集匹配；重叠候选会重新分配匹配，例如 `[{"a":1,"b":2},{"a":1}]` 包含 `[{"a":1},{"a":1,"b":2}]`，交换候选顺序仍得到 TRUE。
- 标量候选可匹配数组内的标量或嵌套数组成员；不会把数组元素对象的字段名当作普通字符串成员。JSON 对象字段名查询只适用于 path 直接指向的对象。
- JSON 容器内的 JSON `null` 可参与结构比较；传入 SQL `NULL` 候选本身始终得到 SQL `NULL`。例如 `[null]` 可作为数组候选匹配数组中的 JSON `null`。
- 需要匹配以 `{` 或 `[` 开头的字面字符串成员时，可将其编码为 JSON 数组候选，例如 `["[literal"]`；不要把这样的候选作为普通 SQL 字符串直接传入。

结果列保留 SQL `NULL`、`BOOL` 和 `BIGINT`：ADO.NET 通过 `IsDBNull`、`GetBoolean`、`GetInt64` 读取。REST 和 Frame 的 SELECT 结果遵守相同值合同。

## 资源与错误边界

| 限制 | 上限和行为 |
|---|---|
| JSON 文本 | 8,388,608 个 UTF-16 字符；超限在 JSON 解析前拒绝 |
| path 文本 | 1,024 个字符 |
| path 片段 | 64 个属性/数组下标片段 |
| JSON 解析及递归比较深度 | 64 层 |
| 被计算长度或参与包含比较的单个数组/对象 | 100,000 个元素/字段 |
| 单次 `json_contains` 结构比较和匹配步骤 | 1,000,000 次操作，目标字段查找、数组目标槽位扫描及匹配重新分配均计入预算 |

JSON 全文先通过受深度限制的解析器；集合数量预算在目标数组长度计算、目标包含判断以及实际遍历的子容器上检查。`json_exists` 只判断路径，不遍历集合进行包含比较，因此不能用它代替全树集合大小验证。未访问的嵌套容器也不承诺经过全树数量校验。

数组包含匹配按需寻找增广路径，辅助空间与候选及目标数组长度之和成正比；不保存所有两两比较关系，不按数组长度递归。比较预算检查期间定期响应当前 SQL 执行取消/超时令牌；真正的 JSON 嵌套仍受 64 层限制。超预算时返回错误，不能把未完成匹配当作 FALSE。

非法 JSON/path、错误参数类型、非数组长度查询和超限会抛出带函数名及原因的执行错误，不截断结果。嵌入式接口抛出 `InvalidOperationException`，远程接口将服务端错误作为 `SndbServerException` 返回；同一连接可继续执行后续合法查询。JSON 文本长度是引擎函数上限；网络请求还受各自的 SQL/帧大小限制，Frame SQL 当前最多 1 MiB，不能据函数上限推断所有传输都接受同样大小的单条参数内联 SQL。

Frame 结果列名最多 512 字节。驱动远程参数绑定后，未指定别名的函数投影可能以包含参数值的表达式作为结果列名；大 JSON 或长 path 投影应显式使用短别名，例如 `SELECT json_array_length(@json, '$') AS result FROM devices`。短别名保留原有 JSON 与 Frame 上限。

## 与 JSON path 索引协作

`CREATE JSON INDEX` 用于文档集合。匹配索引 path 的 `json_value(document, path) = value` 等值条件可先缩小候选集，再执行这些 JSON 标量残余谓词：

```sql
CREATE JSON INDEX idx_device_type ON device_docs ('$.type');

EXPLAIN SELECT id
FROM device_docs
WHERE json_value(document, @type_path) = @type
  AND json_exists(document, '$.tags[0]')
  AND json_array_length(document, '$.tags') > 0
  AND json_contains(document, '$.tags', @member);
```

绑定 `type_path = $.type`、`type = pump`、`member = industrial` 后，`EXPLAIN` 报告 `access_path = document_index` 和 `index_name = idx_device_type`。静态字符串 path 和绑定后的同一路径参数都可满足这个条件。这里索引优化的是 `json_value` 等值条件；其余 JSON 标量仍对候选文档求值。

仅有 `json_exists`、`json_array_length` 或 `json_contains` 条件时，当前不会把这些函数改写为 JSON path 索引查找；示例独立包含查询的 `EXPLAIN` 报告 `document_scan`。关系表 `JSON` 列也不继承文档集合的 `CREATE JSON INDEX`。组合索引必须满足其全部 path 的等值要求，稀疏或 partial 索引还必须满足各自的可用条件。包含函数专用索引下推是后续优化，不是本函数正确性合同的承诺。

## 可复现验证

`SqlJsonFunctionTests` 覆盖 Core 语义；`RemoteJsonFunctionTests` 将相同预期值分别通过嵌入式 ADO.NET、真实 Kestrel REST 和真实 Kestrel HTTP/2 Frame 执行。测试观察服务端实际请求端点、端口和协议，每条 Frame 查询必须命中 `/v1/frame` 且使用 `HTTP/2`，不能将 REST 回落算作 Frame 证据。

远程矩阵覆盖关系和文档投影/过滤、path/candidate 绑定、NULL 与缺失路径、空及嵌套数组、对象子集、引号/Unicode/数字/布尔值、无效输入后的连接恢复、路径/深度/数组/比较预算，以及带残余 JSON 条件的索引计划。固定 512 行语料只验证完整结果集，不代表大规模性能或固定硬件容量报告。

```text
dotnet test tests/SonnetDB.Core.Tests/SonnetDB.Core.Tests.csproj --filter FullyQualifiedName~SqlJsonFunctionTests
dotnet test tests/SonnetDB.Tests/SonnetDB.Tests.csproj --filter FullyQualifiedName~RemoteJsonFunctionTests
```

测试是否通过以本次执行记录为准；本页定义可复现合同，不代替运行报告。
