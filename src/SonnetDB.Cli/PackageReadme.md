# SonnetDB.Cli

`SonnetDB.Cli` 是 SonnetDB 的命令行工具包，安装后命令名为 `sndb`。

CLI 的本地路径参数和连接字符串都指向 SonnetDB 数据库目录，而不是单个数据库文件。服务端、嵌入式和 CLI 使用同一套目录布局与 SQL 语义。

`SonnetDB.Cli` NuGet 包按 .NET tool 分发；如需原生可执行文件，请使用仓库发布的 Native AOT CLI / Server bundle。

## 安装

```bash
dotnet tool install --global SonnetDB.Cli
```

## 常用命令

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

启用服务端 `SonnetDBServer:Observability:DiagnosticDump:Enabled=true` 后，可用 admin token 一键采集仅含 metadata 的诊断快照：

```bash
sndb diag dump --endpoint http://127.0.0.1:5080 --token sonnetdb-admin-token --output ./diagnostic-dump.json
```

## Document 迁移

`sndb document import` 可读取 JSON object、流式 JSON array、JSONL/NDJSON、mongodump 目录，以及连接 BSON 文档文件。大文件优先使用可隔离单项错误的 NDJSON/BSON。常用 Extended JSON 标量会转换为普通 JSON；Decimal128、JavaScript code/code-with-scope 和少用 BSON 类型应先导出 canonical Extended JSON。

先执行不连接目标、保证零写入的 dry-run：

```bash
sndb document import --input ./dump/app --collection devices --dry-run --report ./reports/devices-dry-run.json
```

导入到本地目录，使用 replace/upsert 使同一来源可重复执行：

```bash
sndb document import --input ./devices.ndjson --collection devices --path ./data --mode replace --batch-size 500 --checkpoint ./reports/devices.checkpoint.json --report ./reports/devices-import.json
```

中断后以完全相同的 source、target、collection、mode、ordered 和 batch-size 继续：

```bash
sndb document import --input ./devices.ndjson --collection devices --path ./data --mode replace --batch-size 500 --checkpoint ./reports/devices.checkpoint.json --resume --report ./reports/devices-resume.json
```

目标可通过 `--connection`、`--path`、`--profile`、`--use-default`，或 `--url` + `--database` 指定。`--ordered` 在当前批次首个错误后停止，默认 `--unordered` 会提交同批有效项。每批仅在一个 collection 内原子提交；`requestId` 可稳定重放 24 小时。`--json` 将机器报告写到 stdout，`--report` 将相同报告写入文件；`errorCount` 记录累计错误，`errors` 最多保留 1000 条样本。索引建议只供人工评审，不会自动创建索引。

完整参数：

```text
sndb document import --input <file|mongodump-dir> --collection <name>
  [--format auto|ndjson|json|json-array|bson] [--mode insert|replace]
  [--ordered|--unordered] [--batch-size 500] [--id-path _id]
  [--dry-run] [--no-create] [--report report.json] [--json]
  [--checkpoint state.json [--resume]]
  (--connection <conn>|--path <data>|--profile <name>|--use-default|
   --url <host> --database <db> [--token <token>] [--timeout 100])
```

完整发布产物说明见仓库根目录 `docs/releases/`。
