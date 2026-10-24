# GH-Issue #349 关系表 VECTOR/GEOPOINT 最终边界审计（2026-10-24）

## 结论

本轮采用 issue #349 允许的第二种产品合同：关系表列继续**禁止** `VECTOR(N)` 与 `GEOPOINT`。二者是 measurement `FIELD` 的专用类型；关系行若需要 ORM/CodeFirst 兼容，应显式使用 `FLOAT` 经纬度列和 `JSON` 向量载荷，或由应用维护关系主数据与 companion measurement 的关联。没有静默把专用类型转换成关系列，也没有声称新增关系存储、比较、索引、事务或恢复能力。

现有 `DataTypes` 元数据已将两种类型列出，但 `SupportsRelationalColumn=false`、`SupportsMeasurementField=true`；`VECTOR` 的 `CreateFormat=VECTOR({0})` 仅描述 measurement FIELD 声明。`GetSchema("DataTypes")` 在嵌入式、REST 和 `Protocol=frame-http2` 下由 SDK 本地生成，不代表远端版本协商。

## 本轮补齐的 provider 合同

- Core `SqlParser.ParseTableDataType` 对关系 `CREATE TABLE`、`ALTER TABLE ADD COLUMN`、`ALTER COLUMN ... TYPE` 的两个分支统一抛 `SqlParseException`，错误码为 `sql_relational_type_unsupported`（`SqlErrorCodes.RelationalTypeUnsupported`），操作为 `parse`，提示明确推荐 measurement `FIELD` 或显式标量/JSON 关系映射。
- Server REST SQL 保留历史通用 `sql_error` 语义用于普通失败，但该产品边界错误将稳定码放入 HTTP JSON 的 `error` 与 `code`，不依赖中文消息；失败不会创建半成品表、列或修改已有行。
- Server Frame SQL 查询/Execute 的错误映射保留 `SqlParseException.Code`。当前 SQL Frame 查询只读，关系 DDL 通常经 REST 发送；映射仍保证未来/直接 Frame 调用不退化为泛化 `sql_error`。
- ADO 嵌入式直接暴露 `SqlParseException.Code`；远程 REST/Frame 的 `SndbServerException.Error` 收到相同稳定码。measurement `VECTOR`/`GEOPOINT` 参数化读写和目录隔离保持既有合同。

## 证据与边界

既有 `tests/SonnetDB.Tests/RemoteRelationTypeBoundaryTests.cs` 覆盖 embedded、实际 Kestrel REST 和 HTTP/2 Frame 配置：非法 CREATE/ALTER 不残留 schema，已写关系数据保持不变，`DataTypes` 标志一致，measurement 专用值可写读且不进入关系目录，标量/JSON 替代模型可往返。本轮新增断言覆盖本地 `Code/Operation/Hint` 与远程稳定错误码。`tests/SonnetDB.Core.Tests/Sql/SqlCastTests.cs` 同时锁定 Core parser 边界。

本环境未安装 .NET 10 SDK（`dotnet: command not found`），因此 `dotnet restore`、Format Check、Core/Server/Remote 定向测试和 Native/AOT 门禁均为 **NOT_RUN**；提交前应在 CI/具备 SDK 的环境执行并回读结果。未修改二进制格式、文件版本或 measurement 存储语义。
