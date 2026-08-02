---
layout: default
title: "CLI 参考"
description: "sndb 命令行工具的安装、命令和本地/远程示例。"
permalink: /cli-reference/
---

## 安装

作为全局工具：

```bash
dotnet tool install --global SonnetDB.Cli
```

如果你在仓库源码里直接运行，也可以使用：

```bash
dotnet run --project src/SonnetDB.Cli -- version
```

## 命令速览

```text
sndb version
sndb sql     --connection "<conn>" (--command "<sql>" | --file ./q.sql)
sndb repl    --connection "<conn>"

sndb local   --path ./data [--save-profile home] [--default] [--command "<sql>" | --file ./q.sql | --repl]
sndb local   --profile home [--command "<sql>" | --file ./q.sql | --repl]
sndb local   --use-default [--command "<sql>" | --file ./q.sql | --repl]
sndb local   list
sndb local   remove --profile home

sndb remote  --url http://127.0.0.1:5080 --database db [--token t] [--timeout 30] [--save-profile dev] [--default] [--command "<sql>" | --file ./q.sql | --repl]
sndb remote  --profile dev [--command "<sql>" | --file ./q.sql | --repl]
sndb remote  --use-default [--command "<sql>" | --file ./q.sql | --repl]
sndb remote  list
sndb remote  remove --profile dev

sndb connect <profile-name> [--command "<sql>" | --file ./q.sql | --repl]
sndb connect --default      [--command "<sql>" | --file ./q.sql | --repl]

sndb backup create  --path ./data --output ./backup [--overwrite] [--no-fulltext-indexes]
sndb backup inspect --path ./backup
sndb backup verify  --path ./backup
sndb backup restore --path ./backup --target ./restored [--overwrite] [--no-verify]

sndb document import --input <file|mongodump-dir> --collection <name> [--dry-run] [<target-options>] [<import-options>]
```

---

## `version`

```bash
sndb version
```

---

## `local`

### 直接使用路径

输出连接字符串：

```bash
sndb local --path ./demo-data
```

执行 SQL：

```bash
sndb local --path ./demo-data --command "SELECT count(*) FROM cpu"
```

进入 REPL：

```bash
sndb local --path ./demo-data --repl
```

### 保存 local profile

```bash
sndb local --path ./demo-data --save-profile home --default
```

列出已保存的 local profile：

```bash
sndb local list
```

使用 profile：

```bash
sndb local --profile home --command "SELECT count(*) FROM cpu"
sndb local --use-default --repl
```

删除 profile：

```bash
sndb local remove --profile home
```

---

## `remote`

### 直接连接

输出连接字符串：

```bash
sndb remote \
  --url http://127.0.0.1:5080 \
  --database metrics \
  --token your-token
```

执行 SQL：

```bash
sndb remote \
  --url http://127.0.0.1:5080 \
  --database metrics \
  --token your-token \
  --command "SHOW DATABASES"
```

进入 REPL：

```bash
sndb remote \
  --url http://127.0.0.1:5080 \
  --database metrics \
  --token your-token \
  --repl
```

### 保存 remote profile

```bash
sndb remote \
  --url http://127.0.0.1:5080 \
  --database metrics \
  --token your-token \
  --save-profile dev \
  --default
```

列出 / 使用 / 删除：

```bash
sndb remote list
sndb remote --profile dev --command "SHOW DATABASES"
sndb remote --use-default --repl
sndb remote remove --profile dev
```

---

## `connect`

`connect` 是统一快捷入口，按名称在 local/remote 两个 profile 列表中查找（local 优先）并分发。

```bash
# 使用名为 "home" 的 local profile
sndb connect home

# 使用名为 "dev" 的 remote profile，并进入 REPL
sndb connect dev --repl

# 使用默认 profile 执行 SQL
sndb connect --default --command "SELECT count(*) FROM cpu"
```

---

## `backup`

`backup` 是本地数据库目录的离线备份 / 校验 / 恢复入口。完整说明见 [备份与恢复](/backup-restore/)。

```bash
sndb backup create --path ./demo-data --output ./demo-backup
sndb backup inspect --path ./demo-backup
sndb backup verify --path ./demo-backup
sndb backup dry-run --path ./demo-backup --target ./demo-restored
sndb backup restore --path ./demo-backup --target ./demo-restored --rebuild-indexes
sndb backup rebuild-indexes --path ./demo-restored
```

---

## `document import`

`document import` 将 JSON、NDJSON 或 mongodump BSON 文档迁入一个 SonnetDB collection。建议先运行 dry-run，再使用 `replace`、checkpoint 和 report 执行可重试迁移。完整的能力边界和迁移验收步骤见 [MongoDB-like 迁移指南](/mongodb-migration/)。

```text
sndb document import --input <file|mongodump-dir> --collection <name>
  [--format auto|ndjson|json|json-array|bson] [--mode insert|replace]
  [--ordered|--unordered] [--batch-size 500] [--id-path _id]
  [--dry-run] [--no-create] [--report report.json] [--json]
  [--checkpoint state.json [--resume]]
  (--connection <conn>|--path <data>|--profile <name>|--use-default|
   --url <host> --database <db> [--token <token>] [--timeout 100])
```

### 常用流程

先检查输入和转换报告，不连接目标：

```bash
sndb document import \
  --input ./dump/app \
  --collection devices \
  --dry-run \
  --report ./reports/devices-dry-run.json
```

使用已保存的 profile 分批导入；`replace` 按文档 ID replace/upsert，适合重复执行同一来源：

```bash
sndb document import \
  --input ./devices.ndjson \
  --collection devices \
  --profile production \
  --mode replace \
  --batch-size 500 \
  --checkpoint ./reports/devices.checkpoint.json \
  --report ./reports/devices-import.json
```

进程中断后使用原 checkpoint 继续：

```bash
sndb document import \
  --input ./devices.ndjson \
  --collection devices \
  --profile production \
  --mode replace \
  --batch-size 500 \
  --checkpoint ./reports/devices.checkpoint.json \
  --resume \
  --report ./reports/devices-resume.json
```

### 参数

| 参数 | 默认值 | 说明 |
| --- | --- | --- |
| `--input <path>` / `-i <path>` | 必填 | JSON、JSONL/NDJSON、连接 BSON 文件或 mongodump 目录。JSON array 逐项流式读取；大文件优先使用可隔离单项错误的 NDJSON/BSON。目录输入会读取 `<collection>.bson` 及可选的 `<collection>.metadata.json`。 |
| `--collection <name>` / `-c <name>` | 必填 | 目标 collection；对于 mongodump 目录也用于选择源 BSON 和 metadata 文件。 |
| `--format <format>` | `auto` | `auto`、`ndjson`、`json`、`json-array` 或 `bson`。`auto` 按扩展名及 JSON 根节点识别；未识别扩展名按 NDJSON 读取。NDJSON 按有界单行读取，JSON array 按有界单项流式读取。 |
| `--mode <mode>` | `insert` | `insert` 创建新文档；`replace` 按 ID replace/upsert。 |
| `--ordered` | 关闭 | 遇到首个错误时使当前批次零变更，并停止后续批次。 |
| `--unordered` | 开启 | 跳过失败项并继续；同批有效项仍在一个 collection 内原子提交。 |
| `--batch-size <count>` | `500` | 每批文档数，必须为正整数且不超过 `1000`；同时受约 12 MiB CLI 安全预算约束。 |
| `--id-path <path>` | `_id` | 文档 ID 路径；支持 `_id` 或 `$.nested.path` 形式，值必须能稳定转换为字符串 ID。 |
| `--dry-run` | 关闭 | 只读取、规范化并验证源文档，生成 error、gap 和索引建议；不能与 `--resume` 同时使用。 |
| `--no-create` | 关闭 | 不自动创建目标 collection；目标必须已经存在。 |
| `--checkpoint <path>` | 无 | 每个已提交批次后原子保存进度、累计错误数和最多 1000 条错误样本，用于中断恢复。 |
| `--resume` | 关闭 | 从校验通过的 checkpoint 继续；未指定 `--checkpoint` 时查找 `<实际源文件>.sndb-import.checkpoint.json`。 |
| `--report <path>` | 无 | 将 JSON 迁移报告写入文件，并自动创建父目录。报告包含 `errorCount`、`errorsTruncated` 和最多 1000 条 `errors` 样本。 |
| `--json` | 关闭 | 将同一 JSON 迁移报告写到 stdout，替代文本摘要。 |
| `--connection <conn>` | 无 | 使用完整 SonnetDB 连接字符串作为目标。 |
| `--path <data>` / `-p <data>` | 无 | 使用嵌入式 SonnetDB 数据目录作为目标。 |
| `--profile <name>` | 无 | 使用指定的 local 或 remote profile。若同名 profile 同时存在于两类中，命令会拒绝歧义。 |
| `--use-default` | 关闭 | 使用默认 local 或 remote profile。 |
| `--url <host>` / `-u <host>` | 无 | 直接指定远程 SonnetDB 服务地址，必须同时提供 `--database`。 |
| `--database <db>` / `-d <db>` | 无 | 直接远程目标的数据库名。 |
| `--token <token>` / `-t <token>` | 无 | 远程目标的 Bearer token。 |
| `--timeout <seconds>` | `100` | 远程调用超时秒数，必须为正整数。 |
| `--help` / `-h` | 无 | 输出 `document import` 用法。 |

`--connection`、`--path`、`--profile`、`--use-default` 和 `--url` 是互斥的目标来源；非 dry-run 必须选择其中一种。直接远程模式还需要 `--database`，可选 `--token` 和 `--timeout`。

### Dry-run 边界

`--dry-run` 可以不提供目标。它不会打开目标连接、创建 collection 或产生任何目标写入；即使同时提供了目标参数，也只解析目标标识。它会检查源文件读取、文档 JSON/BSON 转换、ID 提取和 CLI 批次预算，但**不会验证目标 validator、unique index 或权限**。这些目标约束必须通过真实的小批次导入确认，report 会记录 `dry_run_target_constraints_not_checked` gap。

### Checkpoint 与恢复

首次长迁移应显式指定 `--checkpoint`；只有已提交的批次才会推进 checkpoint，已推进位置之前的累计错误数和有界样本也会保留，resume 不会把不完整迁移误报为成功。为避免失败密集型输入让内存和 checkpoint 无界增长，`errors` 最多保留 1000 条，`errorCount` 始终记录总数，`errorsTruncated` 表示是否截断。恢复时必须保持相同的 source、target、collection、mode、ordering（`--ordered` / `--unordered`）和 `--batch-size`。命令会在连接目标和写入前校验 checkpoint schema、源内容哈希、目标标识及上述选项；任一不匹配都会拒绝恢复。源文件内容或文档顺序发生变化时，不要复用旧 checkpoint。

同一批次的稳定 `requestId` 由源哈希、目标、collection、模式、顺序、批次序号和 payload 派生，网络结果不确定时可用原命令重试。索引建议只供人工评审，命令不会自动创建索引。

---

## `sql` / `repl`（兼容原有用法）

```bash
sndb sql \
  --connection "Data Source=./demo-data" \
  --command "SELECT count(*) FROM cpu"

sndb sql \
  --connection "Data Source=sonnetdb+http://127.0.0.1:5080/metrics;Token=your-token" \
  --file ./query.sql

sndb repl --connection "Data Source=./demo-data"
```

---

## `copilot`

通过 HTTP 调用服务端 Copilot 知识库接口（需要先启动 SonnetDB 服务端，并在其上配置 Copilot 子系统）。**所有命令都不直接读写本地数据库，仅作为远端 REST 端点的客户端**。

```text
sndb copilot ingest [--root <dir>]... [--endpoint <url>] [--token <bearer>]
                    [--force] [--dry-run] [--timeout <sec>]
sndb copilot skills reload [--root <dir>] [--endpoint <url>] [--token <bearer>]
                           [--force] [--dry-run]
sndb copilot skills list   [--endpoint <url>]
sndb copilot skills show <name> [--endpoint <url>]
```

| 参数 | 说明 |
| --- | --- |
| `--root` / `-r` | 指定文档根目录；`ingest` 可重复多次叠加多目录，`skills` 仅取最后一个。省略时使用服务端默认配置。 |
| `--endpoint` / `--url` | 服务端地址。默认 `http://127.0.0.1:5080`，也可通过环境变量 `SONNETDB_COPILOT_URL` 提供。 |
| `--token` / `-t` | 服务端要求的 Bearer token（admin 范围）。也可通过环境变量 `SONNETDB_COPILOT_TOKEN` 提供。 |
| `--force` | 忽略 mtime / fingerprint，强制重新嵌入所有命中文件。 |
| `--dry-run` | 仅扫描并切片，不实际写入向量库；用于验证根目录是否生效。 |
| `--timeout` | HTTP 调用超时（秒），默认 600。 |

示例：

```bash
# 把 ./docs 下的文档增量入库
sndb copilot ingest --root ./docs

# 强制重建（不看 mtime），并显式指向远端
sndb copilot ingest --root ./docs --endpoint http://copilot.internal:5080 --force

# 列出当前已注册的 skill
sndb copilot skills list --endpoint http://copilot.internal:5080

# 查看单个 skill 的注册详情
sndb copilot skills show query-aggregation
```

`ingest` 返回的统计字段：`扫描文件 / 重新索引 / 跳过未变 / 清理失效 / 写入分块 / DryRun / 耗时 ms`。

---

## profile 文件

所有 profile 保存在：

```text
~/.sndb/profiles.json
```

文件结构示例：

```json
{
  "defaultProfile": "home",
  "profiles": [
    { "name": "dev", "baseUrl": "http://127.0.0.1:5080", "database": "metrics", "token": "...", "timeout": 30 }
  ],
  "localProfiles": [
    { "name": "home", "path": "/data/demo" }
  ]
}
```

---

## 输出形式

| 情况 | 输出 |
| --- | --- |
| 非查询 SQL | `OK (n rows affected)` |
| 查询 SQL | 文本表格 + `(n row(s))` |
| `local` / `remote` 无 SQL 也无 `--repl` | 打印连接字符串 |
| `local list` / `remote list` | profile 列表，默认项前带 `*` |

---

## 连接字符串

`sql` / `repl` 命令与 ADO.NET 使用同一套连接字符串：

- 本地：`Data Source=./demo-data`
- 远程：`Data Source=sonnetdb+http://127.0.0.1:5080/metrics;Token=...`

详细说明见 [ADO.NET 参考]({{ site.docs_baseurl | default: '/help' }}/ado-net/)。
