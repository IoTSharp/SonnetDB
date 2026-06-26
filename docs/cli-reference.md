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
