# SonnetDB.Cli

`SonnetDB.Cli` 是 SonnetDB 的命令行工具包，安装后命令名为 `sndb`。

CLI 的本地路径参数和连接字符串都指向 SonnetDB 数据库目录，而不是单个数据库文件。服务端、嵌入式和 CLI 使用同一套目录布局与 SQL 语义。

`SonnetDB.Cli` NuGet 包按 .NET tool 分发；如需原生可执行文件，请使用仓库发布的 Native AOT CLI / Server bundle。

## 安装

```bash
dotnet tool install --global SonnetDB.Cli
```

## 示例

```bash
sndb version
sndb sql --connection "Data Source=./demo-data" --command "CREATE MEASUREMENT cpu (host TAG, value FIELD FLOAT)"
sndb sql --connection "Data Source=./demo-data" --command "SELECT count(*) FROM cpu"
sndb repl --connection "Data Source=./demo-data"
```

远程连接示例：

```bash
sndb sql --connection "Data Source=sonnetdb+http://127.0.0.1:5080/metrics;Token=sonnetdb-admin-token" --command "SHOW DATABASES"
```

完整发布产物说明见仓库根目录 `docs/releases/`。
