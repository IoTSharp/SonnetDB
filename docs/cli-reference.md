---
layout: default
title: "CLI 参考"
description: "sndb 命令行工具的安装、命令和本地/远程示例。"
permalink: /cli-reference/
---

## 安装

作为全局工具：

```bash
dotnet tool install --global SonnetDB.Cli --version 0.1.0
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
