## CLI 高级技巧：配置文件管理、REPL 高效操作与跨平台使用

SonnetDB 的 `sndb` CLI 使用 local/remote profile 管理连接，提供 SQL 文件执行、交互 REPL 和本地数据库备份工具。掌握实际命令比维护一套未经实现验证的 `config set`、`execute` 或 `segments compact` 脚本更可靠。

本文依据当前 CLI 参考整理。正式 4.0.0 与 main 各自有版本边界，安装之后先运行 `sndb version` 和对应子命令的帮助，确认脚本使用的功能。

### 安装与版本

```bash
dotnet tool install --global SonnetDB.Cli
sndb version
```

全局工具依赖匹配的 .NET 运行环境。需要固定版本时依据正式包清单使用 `--version`，不要从历史文章里的 `0.1.0` 或 `0.6.0` 推断当前安装包名称。源码方式是：

```bash
dotnet run --project src/SonnetDB.Cli -- version
```

Windows、Linux、macOS 应下载实际发布页列出的对应运行标识产物；不能假定存在 Homebrew tap 或指定版本 DEB。

### 保存和复用本地 profile

```bash
sndb local --path ./demo-data --save-profile home --default
sndb local list
sndb local --profile home --command "SHOW MEASUREMENTS"
sndb local --use-default --repl
```

profile 保存在用户的 `~/.sndb/profiles.json`，不是 TOML 配置层级。它可以包含远程 token，要把文件权限与备份范围纳入凭据管理，不要提交到项目仓库。

### 远程 profile

```bash
sndb remote --url https://db.example.com --database metrics --token <token> --timeout 30 --save-profile dev
sndb remote list
sndb remote --profile dev --command "SHOW MEASUREMENTS"
sndb remote --profile dev --repl
```

`<token>` 是占位符，应换成部署者自己的凭据并避免命令历史泄露。`connect` 按 profile 名分发 local/remote 连接：

```bash
sndb connect home --command "SHOW MEASUREMENTS"
sndb connect dev --repl
sndb connect --default --command "SHOW MEASUREMENTS"
```

避免给本地和远程 profile 取相同名称；统一 `connect` 入口按本地优先查找。

### SQL 文件优于复制长命令

把以下内容保存为 `query.sql`，假设数据库中已有 `sensor_data`：

```sql
SELECT time, temperature, humidity FROM sensor_data
WHERE device_id = 'sensor-01'
  AND time >= 1713676800000
ORDER BY time DESC LIMIT 10
```

```bash
sndb sql --connection "Data Source=./demo-data" --file ./query.sql
sndb remote --profile dev --file ./query.sql
sndb repl --connection "Data Source=./demo-data"
```

`sql` 使用 `--command` 或 `--file`；不要把未确认的 `sndb execute --format csv` 当作导出合同。REPL 用于探索 schema 和小查询，具体交互控制以当前帮助为准，不应套用其他数据库的 `\timing`、`\multiline` 命令。

### 备份、检查和恢复

```bash
sndb backup create --path ./demo-data --output ./demo-backup
sndb backup inspect --path ./demo-backup
sndb backup verify --path ./demo-backup
sndb backup restore --path ./demo-backup --target ./restored-demo
```

这是本地数据库目录的离线备份入口，应先协调停止写入。备份校验与在新目录恢复是不同步骤；单库备份不能冒称覆盖 Server 全实例的 MQ 状态和其他目录。

脚本应检查退出码、限制总运行时间并保留错误，写入结果未知时先对账，不能仅因重跑命令方便就无条件重放。清理演示目录之前先确认目标绝对路径与数据归属。

参考：[CLI 参考](https://github.com/IoTSharp/SonnetDB/blob/main/docs/cli-reference.md)、[备份与恢复](https://github.com/IoTSharp/SonnetDB/blob/main/docs/backup-restore.md)、[正式 4.0.0](https://github.com/IoTSharp/SonnetDB/releases/tag/v4.0.0)。
