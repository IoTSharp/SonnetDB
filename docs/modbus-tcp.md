---
layout: default
title: "Modbus TCP 内建映射表合同"
description: "Milestone 34 的 SQL DDL、TCP master 轮询、TCP slave 读取、地址归一化、类型编解码、写入审批与运行时安全边界。"
---

# Modbus TCP 内建映射表合同

本文记录 Milestone 34（#288～#294）已经落地的 SQL、安全、catalog、地址校验、编解码、TCP master 轮询、受限远端写与 TCP slave 读取合同，也是后续外部写治理、管理面和 parity 测试的共同输入。

> 当前状态：Phase A 的 DDL、Parser/AST、独立版本化 catalog、`SHOW/DESCRIBE MODBUS`、地址冲突校验和类型编解码已经可用；#291 的默认关闭 TCP client/master 轮询、#292 的受限 Source 写、#293 的采集质量与 source health，以及 #294 的默认关闭 TCP server/slave 读取均已接入 Server。外部写 staging/审批和管理界面仍属于 #295～#296。

## 角色与方向

SonnetDB 只定义两个互不混淆的 Modbus TCP 角色：

| SQL 对象 | Modbus 角色 | 网络方向 | 列映射 | 数据语义 |
| --- | --- | --- | --- | --- |
| `MODBUS SOURCE` | client / master | SonnetDB 主动连接外部 PLC、RTU 或仪表 | `FROM MODBUS` | #291 周期读取外部地址并写入本地表；#292 经审批后可写远端 Coil 或 Holding Register |
| `MODBUS ENDPOINT` | server / slave | SonnetDB 监听端口，外部 client / master 连接 | `EXPOSE AS MODBUS` | #294 把本地表当前值编码为地址空间；#295 将外部写入拒绝或先放入待审批队列 |

`SOURCE` 和 `ENDPOINT` 的上下文已经决定角色。第一版 DDL 不再接受冗余的 `ROLE MASTER` 或 `ROLE SLAVE`，也不允许用同一个对象同时承担两个方向。

Modbus runtime 全局默认关闭。创建 source、endpoint 或映射表只持久化 catalog；只有同时设置 `SonnetDBServer:Modbus:Enabled=true` 且对应 source 或 endpoint 声明 `ENABLED TRUE` 时，master worker 才会连接和轮询、slave endpoint 才会监听。#292 `PREVIEW/CONFIRM` 同样要求全局门禁与 source 均启用；`DRY RUN` 只做本地校验和编码，不要求启用 runtime。DDL 不能绕过全局门禁。

普通 `SELECT`、`SHOW` 和 `DESCRIBE` 始终读取 SonnetDB 本地状态，不在查询线程中连接 PLC，也不等待现场设备响应。实时采集只由后台 master worker 完成；显式维护命令也不得改变普通查询的这一合同。

## 上下文语法

`MODBUS`、`SOURCE`、`ENDPOINT`、`FROM MODBUS`、`EXPOSE AS MODBUS` 和 `USING MODBUS` 是上下文关键字。Lexer 不应把它们全局保留；Parser 只在下列产生式中赋予特殊含义，现有同名表、列或别名继续遵循普通标识符规则。

### Source DDL

```sql
CREATE MODBUS SOURCE <source_name>
WITH (
    TRANSPORT TCP,
    ENDPOINT '<host>:<port>',
    UNIT_ID <integer>,
    POLL_INTERVAL '<duration>',
    TIMEOUT '<duration>',
    RETRY <integer>,
    ADDRESSING ZERO_BASED | ONE_BASED | MODICON,
    BYTE_ORDER BIG_ENDIAN | LITTLE_ENDIAN,
    WORD_ORDER BIG_ENDIAN | LITTLE_ENDIAN,
    ENABLED TRUE | FALSE
);
```

`TRANSPORT` 第一版只能是 `TCP`。source 级 `ADDRESSING`、`BYTE_ORDER` 和 `WORD_ORDER` 是其列映射的默认值；列可以覆盖字节序和字序，但不能覆盖寻址模式。

`ENDPOINT`、`BYTE_ORDER` 和 `WORD_ORDER` 必须显式声明。`TRANSPORT` 可省略且固定为 `TCP`；其余默认值为 `UNIT_ID 1`、`POLL_INTERVAL '1s'`、`TIMEOUT '3s'`、`RETRY 3`、`ADDRESSING MODICON`、`ENABLED FALSE`。`ENABLED` 只是 catalog 中的对象期望状态，不能打开全局 runtime 门禁；只有全局门禁和对象状态都启用且 worker 正在运行时，稳定元数据中的 `runtime_enabled` 才为 `TRUE`。

主站采集表使用以下形式：

```sql
CREATE TABLE <table_name> (
    <sample_time_column> DATETIME SAMPLE_TIME,
    <quality_column> INT QUALITY,
    <column_definition>
        FROM MODBUS <area>(<address> [, <count>]) [ .BIT(<bit_index>) ]
        AS <wire_type>
        [ BYTE_ORDER BIG_ENDIAN | LITTLE_ENDIAN ]
        [ WORD_ORDER BIG_ENDIAN | LITTLE_ENDIAN ]
        [ SCALE <number> ]
        [ OFFSET <number> ]
        [ ACCESS READ | WRITE | READ_WRITE ],
    ...
)
USING MODBUS SOURCE <source_name>
WITH (
    TABLE_MODE HISTORY | LATEST,
    ON_ERROR KEEP_LAST | NULL | SKIP | MARK_BAD,
    STORE HISTORY
);
```

`SAMPLE_TIME` 与 `QUALITY` 都是可选的列角色，且每张 source 表最多各声明一列。`QUALITY` 必须使用 `INT`，runtime 写入下面定义的稳定质量位。`TABLE_MODE`、`ON_ERROR` 和 `STORE HISTORY` 都可以省略，也可以按任意顺序声明；默认使用 `TABLE_MODE LATEST` 与 `ON_ERROR KEEP_LAST`。`LATEST` 表使用非自增 `INT` 单列主键，runtime 固定 upsert 主键 `0`；`HISTORY` 表使用 `DATETIME` 单列主键或自增 `INT` 单列主键，每次未被 `SKIP` 的采样追加一行。`STORE HISTORY` 保留为 catalog 中的 history retention 声明；当前关系表的可查询行形态仍由 `TABLE_MODE` 决定，不会为 `LATEST` 表隐式创建第二张关系表。

一张表只能绑定一个 Modbus source 或 endpoint。绑定对象必须已经存在于同一数据库，映射在建表时完成地址归一化和冲突校验。

### TCP master runtime（#291）

Server 配置默认值如下；缺少整个 `Modbus` 节点与显式写出 `Enabled: false` 等价：

```json
{
  "SonnetDBServer": {
    "Modbus": {
      "Enabled": false,
      "DiscoveryIntervalMilliseconds": 250,
      "RetryBaseDelayMilliseconds": 100,
      "MaxRetryDelayMilliseconds": 2000,
      "ReconnectBaseDelayMilliseconds": 1000,
      "MaxReconnectDelayMilliseconds": 30000
    }
  }
}
```

显式启用后，Server 周期扫描已注册数据库，为每个 `ENABLED TRUE` 的 source 建立独立 worker。新增数据库、source 或关系表绑定无需重启；source 被移除、数据库关闭或宿主停止会取消对应 worker。#291 起 SQL 新建的 binding 会记录 `binding_enabled=TRUE`；升级前 Phase A 已持久化的 binding 因当时不存在启停 DDL，继续按存在即参与处理，避免升级后永久静默禁用。没有可读绑定或只有 `ACCESS WRITE` 映射时状态为 `idle`，不会建立 TCP 连接。

每轮采集先汇总该 source 的所有 `FROM MODBUS` 绑定，按 Coil、Discrete Input、Input Register、Holding Register 四个独立地址空间排序；连续或重叠区间合并读取，bit 区每个请求最多 2,000 点，register 区每个请求最多 125 个寄存器，超过上限的单个映射会拆成多个请求并在解码前重组。四类读取分别使用 function `0x01`、`0x02`、`0x04`、`0x03`，响应必须匹配 transaction id、protocol id 0、unit id、length、function 和 byte count；设备 exception 返回稳定的 `device_exception_xx` 错误码。

source 的 `TIMEOUT` 覆盖连接、写请求和读响应；宿主停止、数据库/source 移除与调用方取消会直接打断等待和 socket I/O。单轮失败最多按 source `RETRY` 重试，每次重试都丢弃旧连接并按 `RetryBaseDelayMilliseconds` 指数退避到配置上限；一轮彻底失败后按独立的 reconnect 退避继续尝试，成功后恢复初始延迟。成功响应经同一 `ModbusValueCodec` 解码，再按 `TABLE_MODE LATEST` upsert 或 `HISTORY` insert 到本地关系表并写入 `GOOD` 质量。最终失败会保留该次重试最后一个部分快照，按每张绑定的 `ON_ERROR` 独立生成本地行；source 级互斥只覆盖实际 I/O 与本地提交，不覆盖 poll/reconnect 延时。

兼容 `/metrics` 暴露 `sonnetdb_modbus_master_polls_total`、`sonnetdb_modbus_master_poll_failures_total`、`sonnetdb_modbus_master_read_batches_total`、`sonnetdb_modbus_master_rows_written_total` 和 `sonnetdb_modbus_master_reconnects_total`。启用 OpenTelemetry 时，`SonnetDB.Server` meter 还提供对应 poll/read/row/reconnect counter 与 `sonnetdb.modbus.master.poll.duration` histogram；指标不使用数据库名、source 名或地址作为高基数标签。

### TCP slave runtime（#294）

显式启用全局 Modbus runtime 后，Server 周期扫描已注册数据库，为每个 `ENABLED TRUE` 的 endpoint 建立独立 listener。新增数据库、endpoint 或关系表绑定无需重启；endpoint 被移除、数据库关闭或宿主停止会取消 accept、活动连接和正在等待的 socket 读取。客户端连接可连续处理多个顺序请求，`MAX_CONNECTIONS` 限制的是 endpoint 的活动 TCP 长连接数；超额连接立即关闭，连接退出后配额立即归还。

连接先按 `ALLOWLIST` 校验，再占用连接配额。规则支持精确 IPv4/IPv6 地址和 CIDR；空 allowlist 只允许回环客户端。该网络边界不等价于数据库身份或写权限。非回环 `BIND` 仍必须配置非空 allowlist。

读取支持四个标准 function：Coil `0x01`、Discrete Input `0x02`、Holding Register `0x03` 和 Input Register `0x04`。bit 区单次最多 2,000 点，register 区单次最多 125 个寄存器；MBAP protocol id、长度、PDU 形态、地址范围和数量均严格校验。endpoint 的所有表绑定会汇总成同一地址空间，并按各自固定 `ROW KEY` 直接读取当前关系行，再复用 catalog 中已解析的字节序、字序、类型、scale、offset 和 `.BIT(n)` 映射编码响应。请求跨越未映射间隙、只写映射或不存在的固定行时不会返回部分或伪造值。

异常响应保持确定性：不支持的 function（包括 #295 前的全部写请求）返回 `0x01 Illegal Function`；未映射、映射间隙或只写地址返回 `0x02 Illegal Data Address`；非法数量、越界或 PDU 形态返回 `0x03 Illegal Data Value`；固定行缺失、值为 `NULL`、编码失败或 endpoint 内跨表映射歧义返回 `0x04 Server Device Failure`；Unit ID 不匹配返回 `0x0B Gateway Target Device Failed to Respond`。#294 不接收、不暂存也不应用外部写入，catalog 中的 `WRITE_POLICY` 与 `ON_EXTERNAL_WRITE` 继续作为 #295 合同保留。

`SHOW/DESCRIBE MODBUS ENDPOINT` 公开非持久化的 `disabled`、`starting`、`listening`、`degraded` health；绑定失败与监听循环失败分别报告稳定错误码 `endpoint_bind_error`、`endpoint_listener_error`，不会修改 catalog revision。兼容 `/metrics` 暴露 `sonnetdb_modbus_slave_connections_total`、`sonnetdb_modbus_slave_connection_rejections_total`、`sonnetdb_modbus_slave_active_connections`、`sonnetdb_modbus_slave_read_requests_total` 和 `sonnetdb_modbus_slave_read_failures_total`。`SonnetDB.Server` meter 同时提供 `sonnetdb.modbus.slave.connections`、`sonnetdb.modbus.slave.connection.rejections`、`sonnetdb.modbus.slave.connections.active` 和 `sonnetdb.modbus.slave.read.requests`；标签只包含有限的结果、拒绝原因和地址区类型。

### Endpoint DDL

```sql
CREATE MODBUS ENDPOINT <endpoint_name>
WITH (
    TRANSPORT TCP,
    BIND '<ip>:<port>',
    UNIT_ID <integer>,
    ADDRESSING ZERO_BASED | ONE_BASED | MODICON,
    BYTE_ORDER BIG_ENDIAN | LITTLE_ENDIAN,
    WORD_ORDER BIG_ENDIAN | LITTLE_ENDIAN,
    ALLOWLIST ('<ip-or-cidr>' [, ...]),
    MAX_CONNECTIONS <integer>,
    WRITE_POLICY REJECT | STAGED,
    ENABLED TRUE | FALSE
);
```

endpoint 级 `WRITE_POLICY` 默认是 `STAGED`。当前只持久化以下两种策略，#294 读取 runtime 对所有写 function 统一返回 `0x01 Illegal Function`；#295 落地后再按对应写入合同执行：

- `REJECT`：#295 runtime 拒绝全部外部 Modbus 写请求，并记录拒绝审计。
- `STAGED`：#295 runtime 先校验并持久化不可变的待审批请求；协议接受只表示请求已可靠进入待审批队列，不表示业务表已经改变。

后续 runtime 不存在 endpoint 级 `UPDATE_TABLE` 入口策略，也不存在 `AUDIT FALSE`。审计是不可关闭的运行时不变量。

`BIND`、`BYTE_ORDER` 和 `WORD_ORDER` 必须显式声明。`TRANSPORT` 可省略且固定为 `TCP`；其余默认值为 `UNIT_ID 1`、`ADDRESSING MODICON`、`MAX_CONNECTIONS 32`、`WRITE_POLICY STAGED`、空 `ALLOWLIST` 和 `ENABLED FALSE`。非回环 `BIND` 必须配置非空 `ALLOWLIST`；回环地址可以使用空 allowlist。`ENABLED` 不能绕过全局 runtime 门禁。解析器仅为旧草案兼容接受冗余的 `AUDIT TRUE`，`AUDIT FALSE` 始终拒绝；新 DDL 不应写 `AUDIT`。

从站暴露表使用以下形式：

```sql
CREATE TABLE <table_name> (
    <column_definition>
        EXPOSE AS MODBUS <area>(<address> [, <count>]) [ .BIT(<bit_index>) ]
        AS <wire_type>
        [ BYTE_ORDER BIG_ENDIAN | LITTLE_ENDIAN ]
        [ WORD_ORDER BIG_ENDIAN | LITTLE_ENDIAN ]
        [ SCALE <number> ]
        [ OFFSET <number> ]
        [ ACCESS READ | WRITE | READ_WRITE ],
    ...
)
USING MODBUS ENDPOINT <endpoint_name>
WITH (
    ROW KEY <primary_key_value>,
    ON_EXTERNAL_WRITE STAGE_ONLY | UPDATE_TABLE
);
```

`ROW KEY` 固定 endpoint 暴露的单行。第一版不从 Unit ID 或寄存器块推导关系表游标。

`ON_EXTERNAL_WRITE` 可以省略，默认是 `STAGE_ONLY`。`STAGE_ONLY` 只记录审批结果，不更新绑定表；`UPDATE_TABLE` 定义后续 #295 runtime 在审批通过后的应用动作：授权审批者确认待审批请求后，SonnetDB 才按同一份已校验映射更新固定行。两种动作都不能覆盖 endpoint 的 `REJECT`，也不能把 `STAGED` 变成直接写表；#294 只读取并展示该配置，不会接收或审批外部写请求。

## Catalog 持久化与恢复边界

关系表 schema 和 Modbus 定义分别保存在 `tables` 与 `modbus` 两个独立的版本化 catalog 中。正常运行时，映射表创建、关系表 schema 变更、Modbus DDL 和在线备份共享同一个数据库级 schema 锁，因此并发操作不会让备份或其它 DDL 观察到半次映射表创建。单个 catalog 使用临时文件原子替换；任一 catalog 在落盘后、内存发布前失败时会恢复旧文件，绑定提交失败时创建语句还会回滚刚创建的关系表。

这把锁提供的是 DDL 与备份的串行边界，不是两个无锁 catalog 读接口的联合快照。直接分别读取 `TableCatalog` 和 `ModbusCatalog` 的调用方，可能在一次成功映射表创建的两个内存发布点之间短暂观察到“表已存在、绑定尚未发布”；需要跨 catalog 一致性的维护操作必须使用数据库级 schema 边界，不能把两次独立读取解释为原子快照。

Phase A 尚未提供跨两个 catalog 的崩溃 journal。进程被强制终止或掉电若恰好发生在关系表 catalog 已替换、Modbus catalog 尚未替换之间，磁盘上可能留下一个 schema 完整但没有 Modbus 绑定的普通关系表。重启后执行同名的 `CREATE TABLE IF NOT EXISTS ... USING MODBUS` 会明确拒绝这个不完整状态，不会把它报告为成功；应先删除该无绑定表后重试完整 DDL，或通过受控维护流程恢复已知正确的绑定。此边界不能解释为跨 catalog 的掉电原子性。

## 地址空间与 PDU 归一化

Catalog 同时保存 DDL 中的声明地址和归一化后的零基 PDU 地址。网络 runtime 只能使用归一化地址，不得在收发路径再次猜测地址基准。

### 寻址模式

| 模式 | DDL 地址 | PDU 归一化 |
| --- | --- | --- |
| `ZERO_BASED` | `0..65535` | `pdu = declared` |
| `ONE_BASED` | `1..65536` | `pdu = declared - 1` |
| `MODICON` | 传统区域引用号，见下表 | 先校验区域前缀，再减去对应区域基值 |

`MODICON` 支持传统五位引用号，也支持覆盖完整 16 位 PDU 地址的六位扩展引用号：

| 区域 | 五位或常用形式 | 六位扩展形式 | PDU 归一化 |
| --- | --- | --- | --- |
| `COIL` | `1..9999`；前导 `0` 只用于显示 | `1..65536` | `declared - 1` |
| `DISCRETE_INPUT` | `10001..19999` | `100001..165536` | 分别减 `10001` 或 `100001` |
| `INPUT_REGISTER` | `30001..39999` | `300001..365536` | 分别减 `30001` 或 `300001` |
| `HOLDING_REGISTER` | `40001..49999` | `400001..465536` | 分别减 `40001` 或 `400001` |

例如，`HOLDING_REGISTER(40001)`、`HOLDING_REGISTER(400001)`、`ONE_BASED` 下的 `HOLDING_REGISTER(1)` 和 `ZERO_BASED` 下的 `HOLDING_REGISTER(0)` 都归一化为 Holding Register PDU 地址 `0`。

五位范围只表达前 9,999 个地址；更高地址必须使用六位扩展形式。Modicon 区域前缀与 `area` 不匹配时 DDL 失败，不能静默按低位地址解释。

### 范围、位与冲突

- `count` 省略时由 wire type 推导；显式提供时必须与 wire type 所需寄存器数完全一致。
- 归一化后的 `pdu + count - 1` 必须不超过 `65535`。
- Coil、Discrete Input、Holding Register 和 Input Register 是四个独立地址空间；不同空间中的相同 PDU 地址不冲突。
- 同一表绑定中的同一区域映射不得重叠。完整寄存器映射与其 `.BIT(n)` 映射也视为重叠。
- `.BIT(n)` 只适用于 Holding Register 或 Input Register，`n` 必须是 `0..15`，且固定占用一个寄存器。
- 寄存器 `.BIT(n)` 第一版只读，必须声明或继承 `ACCESS READ`。可写单 bit 需要原子 mask-write 或并发安全的 read-modify-write 合同，不在第一版中暗含实现。

## 四类 Modbus 区域

| 区域 | 读取功能码 | 写入功能码 | 可映射值 | 第一版访问上限 |
| --- | --- | --- | --- | --- |
| `COIL(n)` | 01 | 05 / 15 | `BIT` | `READ`、`WRITE`、`READ_WRITE` |
| `DISCRETE_INPUT(n)` | 02 | 无 | `BIT` | 仅 `READ` |
| `HOLDING_REGISTER(n[, count])` | 03 | 06 / 16 | 整数、浮点、BCD、STRING、只读寄存器 BIT | 除寄存器 BIT 外可读写 |
| `INPUT_REGISTER(n[, count])` | 04 | 无 | 整数、浮点、BCD、STRING、只读寄存器 BIT | 仅 `READ` |

所有列的默认访问都是 `ACCESS READ`。`DISCRETE_INPUT`、`INPUT_REGISTER` 或寄存器 `.BIT(n)` 声明为 `WRITE` / `READ_WRITE` 时，DDL 必须失败；runtime 不得等到收到写请求后才发现非法映射。

“四类寄存器读写对拍”在验收中表示四类地址空间都验证读取，只有协议本身可写的 Coil 和完整 Holding Register 验证写入；只读区域必须验证稳定拒绝写入。

## Wire type

| Wire type | 寄存器数 | SQL 语义 | 有效范围或编码 |
| --- | ---: | --- | --- |
| `BIT` | 1 bit | `BOOL` | `0` / `1` |
| `INT16` | 1 | `INT` | 有符号二补码 16 位 |
| `UINT16` | 1 | `INT` | `0..65535` |
| `INT32` | 2 | `INT` | 有符号二补码 32 位 |
| `UINT32` | 2 | `INT` | `0..4294967295` |
| `FLOAT32` | 2 | `FLOAT` | IEEE-754 binary32，必须为有限值 |
| `FLOAT64` | 4 | `FLOAT` | IEEE-754 binary64，必须为有限值 |
| `BCD16` | 1 | `INT` | 无符号 packed BCD，`0..9999` |
| `BCD32` | 2 | `INT` | 无符号 packed BCD，`0..99999999` |
| `STRING(n)` | `ceil(n / 2)` | `STRING` | 固定 `n` 字节 ASCII |

BCD 第一版不定义符号位；任一 nibble 为 `A..F` 时解码失败。`STRING(n)` 中的 `n` 是字节数而不是字符或寄存器数，输入只允许 ASCII `0x01..0x7F`；编码到固定宽度后使用 `0x00` NUL 补齐。解码在第一个 NUL 处结束并移除其后的 padding。超长字符串、非 ASCII 字符和嵌入 NUL 必须拒绝，不能截断或替换。

## 字节序与字序

source 或 endpoint 必须显式声明 `BYTE_ORDER` 和 `WORD_ORDER`。列级设置覆盖对象默认值。单寄存器值不受 `WORD_ORDER` 影响，但 catalog 仍记录继承后的有效设置，确保 metadata 稳定。

以 32 位规范字节 `A B C D` 为例，其中 `A` 是最高有效字节：

| `BYTE_ORDER` | `WORD_ORDER` | 线上的两寄存器布局 | 常用简称 |
| --- | --- | --- | --- |
| `BIG_ENDIAN` | `BIG_ENDIAN` | `AB CD` | `ABCD` |
| `LITTLE_ENDIAN` | `BIG_ENDIAN` | `BA DC` | `BADC` |
| `BIG_ENDIAN` | `LITTLE_ENDIAN` | `CD AB` | `CDAB` |
| `LITTLE_ENDIAN` | `LITTLE_ENDIAN` | `DC BA` | `DCBA` |

对 64 位值，`BYTE_ORDER` 分别控制每个 16 位寄存器内的两个字节，`WORD_ORDER LITTLE_ENDIAN` 反转全部 16 位寄存器的顺序。`STRING(n)` 先形成固定宽度的规范 ASCII 字节流并补 NUL，再应用相同的寄存器内字节序和寄存器顺序。

现有 `modbus_int32`、`modbus_uint32`、`modbus_float32` SQL 函数接受 `ABCD/BADC/CDAB/DCBA`；上表是它们与新 DDL 两级字节序合同的唯一对应关系。

## 缩放与写入逆变换

`SCALE` 默认 `1`，`OFFSET` 默认 `0`。读取公式固定为：

```text
sql_value = raw_value * scale + offset
```

`scale` 和 `offset` 必须是有限数，`scale` 不能为 `0`。写入使用唯一逆变换：

```text
raw_value = (sql_value - offset) / scale
```

写入前必须完成全部校验，且逆变换结果必须能被目标 wire type 精确表示：

- 整数与 BCD 目标要求结果是精确整数，并位于目标范围内。
- `FLOAT32` 要求值能转换为有限 binary32，并按读取公式回算为原 SQL 值；不允许静默降精度。
- `FLOAT64` 只接受有限 binary64；以 `long`、`ulong` 或 `decimal` 提供的有限数值必须能无损转换为 binary64，转换后无法保持原值时拒绝写入。
- `BIT` 只接受布尔值。
- `STRING(n)` 不应用数值缩放。
- 任何隐式四舍五入、截断、饱和、整数溢出、无穷大或 NaN 都必须在 preview 和真实执行前以同一稳定错误拒绝。

preview、dry-run 和提交必须调用同一编解码与校验实现，不能出现“预览可写、提交时换一套转换规则”。

## 采集错误策略

source 表的 `ON_ERROR` 使用以下四个稳定名称：

| 策略 | 本地数据行为 | 质量与诊断行为 |
| --- | --- | --- |
| `KEEP_LAST` | 保留该列最后一次成功值 | 标记为 stale，并记录本次失败 |
| `NULL` | 本次列值写为 `NULL` | 标记为 bad，并记录本次失败 |
| `SKIP` | 本轮不追加 history 行，也不更新 latest 行 | source health 和失败计数仍更新 |
| `MARK_BAD` | 有可解码响应时保存本次值；无可解码值时保存 `NULL` | 明确标记为 bad，不能伪装成正常样本 |

`KEEP_LAST` 不能把旧值重新标为 good；第一次失败且尚无成功行时没有可保留值，因此不创建本地行。`NULL` 与 `MARK_BAD` 可能产生缺值，所有可读映射列必须允许 `NULL`。`SKIP` 不写行，但仍推进 source 的最后尝试、最后错误时间和连续失败数。`LATEST` 更新主键 `0` 的当前行，`HISTORY` 对除 `SKIP` 外的策略追加本次失败行。

`QUALITY` 列保存以下可组合的稳定 Int64 位；调用方必须按位判断，不能把组合值当成封闭枚举：

| 名称 | 值 | 含义 |
| --- | ---: | --- |
| `GOOD` | 0 | 本轮读取、解码与本地写入成功 |
| `STALE` | 1 | 沿用上一次成功值（`KEEP_LAST`） |
| `BAD` | 2 | 本次采样不能作为正常值使用 |
| `PARTIAL` | 4 | 只保存了部分快照中可读取、可解码的字段 |
| `NO_VALUE` | 8 | 至少一个可读映射字段写为 `NULL` |

因此 `NULL` 的常见质量为 `BAD | NO_VALUE`（10），部分 `MARK_BAD` 为 `BAD | PARTIAL | NO_VALUE`（14）。若 source 在别的读取批次失败、但某张绑定的全部字段已经成功解码，该绑定的 `MARK_BAD` 行只设置 `BAD`。

source health 的稳定错误码如下；设备异常使用 `device_exception_xx`，其中 `xx` 为两位小写十六进制：

| 类别 | 稳定错误码 |
| --- | --- |
| timeout / transport | `timeout`、`connection_error` |
| MBAP / PDU | `transaction_mismatch`、`invalid_protocol`、`unit_mismatch`、`invalid_length`、`invalid_exception`、`function_mismatch`、`invalid_payload`、`invalid_byte_count` |
| device | `device_exception_xx` |
| local decode / ingest | `decode_error`、`ingest_error`、`database_closed` |
| fallback | `runtime_error` |

## 写入、权限、审批与审计

> Source 远端写、preview、确认和持久审计已由 #292 实现；Endpoint 外部写 staging、审批、应用及相关审计仍属于 #295。

### Source 的受限 SQL 写（#292）

联网写只在 Server REST SQL 端点 `/v1/db/{db}/sql` 和 batch 端点执行；嵌入式 `SqlExecutor` 与 Frame SQL 明确拒绝，因为它们不持有服务端确认令牌、持久审计和网络运行时。语法为：

```sql
WRITE MODBUS <table> SET <column> = <value> DRY RUN;
WRITE MODBUS <table> SET <column> = <value> PREVIEW;
WRITE MODBUS <table> SET <column> = <value> CONFIRM '<one-time-token>';
SHOW MODBUS WRITE AUDIT;
```

只有 `FROM MODBUS SOURCE`、`TABLE_MODE LATEST`、映射到完整 Coil 或完整 Holding Register 且声明 `ACCESS WRITE` / `READ_WRITE` 的列允许写入。执行前必须证明 catalog 中只有一个目标绑定、表中恰好一个当前逻辑行且语句只指定一个映射列；HISTORY、输入区、寄存器 `BIT(n)`、零行/多行和超过 123 个 Holding Register 的单次编码都直接拒绝，不拆成可能部分成功的多个请求。

普通关系 `INSERT` 和 `IMPORT JSON` 不允许向 Source 映射表创建本地影子行，普通 `UPDATE` 不允许修改映射列；非映射辅助列仍可按普通表约束更新。这样不能绕过远端确认和审计制造“本地已成功”的假象。

执行流程固定为：

1. 同时检查当前数据库的 `Write` 和 `Admin`；`Admin` 是 #292 的专用 Modbus 控制权限，`SHOW MODBUS WRITE AUDIT` 同样只允许数据库 Admin。
2. `DRY RUN` 使用实际 catalog、唯一当前行和 `ModbusValueCodec` 完成校验与编码，返回 source、Unit ID、功能码、声明/PDU 地址、wire type、规范化值和编码寄存器；不联网，也不签发令牌。
3. `PREVIEW` 执行相同校验，并签发五分钟、一次性、仅存于当前 Server 进程的随机令牌。令牌绑定认证用户或静态凭据指纹、数据库、source、表、列、规范化编码值、catalog revision 和整行 SHA-256 指纹；服务重启、过期、重放或任一绑定变化后都必须重新 preview。
4. `CONFIRM` 必须再次提交相同逻辑值和令牌。等待 source 级互斥锁后会在联网前重新核对全部绑定，并先持久化 `started` 审计；轮询与控制写只在实际 TCP I/O 和采集提交期间互斥，不跨 poll interval 持锁。
5. Coil 使用 `0x05`，单 Holding Register 使用 `0x06`，多 Holding Register 使用一个 `0x10` ADU。MBAP transaction/protocol/unit/length、功能码、地址和值/数量回显必须全部匹配；控制写不按 source `RETRY` 自动重放。
6. 设备返回合法成功响应后先把 `remote_succeeded` 审计强制落盘，再在数据库 schema 和表锁内重新核对 catalog revision、整行指纹和 ROWVERSION，最后更新 LATEST 镜像。若本地提交冲突，会记录 `local_failed` 并明确报告“设备已成功、本地未更新”，不会伪造本地成功。

远端失败不得伪造本地表成功、成功审计或 good quality。

### Endpoint 的外部写（#295 后续合同）

Modbus TCP 没有可等价为数据库用户的内建身份。#295 实现 Endpoint 外部写后，`ALLOWLIST` 仍只能作为网络边界，不能授予业务表写权限；外部写不得以对端 IP、Unit ID 或 function code 冒充数据库 principal。

#295 必须让持久化后的 `STAGED` 请求至少绑定 endpoint、对端、Unit ID、transaction、function code、声明地址、PDU 地址、原始寄存器、解码值、映射表与 catalog 版本。审批者必须同时具有目标表写权限和 Modbus 审批权限。映射变化、目标行变化、请求过期或权限撤销后，旧请求必须拒绝并重新 staging。

#295 执行审批后的 `UPDATE_TABLE` 时仍必须走普通表约束、事务和审计；约束失败不得报告为已应用。`REJECT`、staging 持久化失败、审批拒绝、审批过期和应用失败都不能改变表值。

### 审计不变量（#292 已实现 / #295 后续合同）

#292 Source 写审计保存在 Server `.system/modbus-write-audit.ndjson`，每条 source-generated JSON 追加后执行 durable flush；启动时遇到损坏行会拒绝加载。`SHOW MODBUS WRITE AUDIT` 返回当前数据库最近 200 条事件，覆盖 dry-run、preview、确认开始、联网前拒绝、远端成功/失败和本地更新失败；记录操作 ID、时间、凭据身份、source、表列、Unit ID、功能码、声明/PDU 地址、结果、错误码、审批 ID 和 catalog revision，不保存逻辑值、寄存器载荷或确认令牌。#295 后续仍需覆盖 endpoint 外部写拒绝/staged/批准/拒批/应用/失败；现有 DDL 审计继续沿用服务端审计入口。

#292/#295 不得提供通过 DDL、配置或调用参数关闭上述审计的入口。需要产生运行时审计的写操作在审计持久化不可用时必须失败关闭；`started` 无法落盘时不执行远端写，`remote_succeeded` 无法落盘时不更新本地业务表。

## 完整示例

### SonnetDB 作为 client / master

```sql
CREATE MODBUS SOURCE line1_plc
WITH (
    TRANSPORT TCP,
    ENDPOINT '192.168.1.50:502',
    UNIT_ID 1,
    POLL_INTERVAL '1s',
    TIMEOUT '800ms',
    RETRY 3,
    ADDRESSING MODICON,
    BYTE_ORDER BIG_ENDIAN,
    WORD_ORDER BIG_ENDIAN,
    ENABLED TRUE
);

CREATE TABLE pump_runtime (
    sample_time DATETIME SAMPLE_TIME,

    running BOOL
        FROM MODBUS COIL(1)
        AS BIT
        ACCESS READ_WRITE,

    fault BOOL
        FROM MODBUS DISCRETE_INPUT(10002)
        AS BIT,

    speed_rpm INT
        FROM MODBUS HOLDING_REGISTER(40001)
        AS UINT16
        SCALE 1
        OFFSET 0
        ACCESS READ_WRITE,

    temperature FLOAT
        FROM MODBUS INPUT_REGISTER(30001)
        AS INT16
        SCALE 0.1
        OFFSET 0,

    flow_rate FLOAT
        FROM MODBUS HOLDING_REGISTER(40010, 2)
        AS FLOAT32
        BYTE_ORDER BIG_ENDIAN
        WORD_ORDER LITTLE_ENDIAN,

    alarm_bit BOOL
        FROM MODBUS HOLDING_REGISTER(40020).BIT(3)
        AS BIT
        ACCESS READ,

    device_name STRING
        FROM MODBUS HOLDING_REGISTER(40030, 8)
        AS STRING(16),

    PRIMARY KEY (sample_time)
)
USING MODBUS SOURCE line1_plc
WITH (
    TABLE_MODE HISTORY,
    ON_ERROR MARK_BAD
);

CREATE TABLE pump_controls (
    id INT NOT NULL,
    running BOOL
        FROM MODBUS COIL(10)
        AS BIT
        ACCESS READ_WRITE,
    speed_setpoint INT
        FROM MODBUS HOLDING_REGISTER(40050)
        AS UINT16
        ACCESS READ_WRITE,
    PRIMARY KEY (id)
)
USING MODBUS SOURCE line1_plc
WITH (
    TABLE_MODE LATEST,
    ON_ERROR KEEP_LAST
);

WRITE MODBUS pump_controls SET speed_setpoint = 1500 DRY RUN;
WRITE MODBUS pump_controls SET speed_setpoint = 1500 PREVIEW;
-- 从 PREVIEW 结果读取 confirmation_token，并在五分钟内提交相同值：
WRITE MODBUS pump_controls SET speed_setpoint = 1500 CONFIRM '<one-time-token>';
```

该配置中 `DISCRETE_INPUT(10002)` 归一化为 Discrete Input PDU 地址 `1`，`HOLDING_REGISTER(40001)` 和 `INPUT_REGISTER(30001)` 分别归一化为各自地址空间的 PDU 地址 `0`。`flow_rate` 的有效布局是 `CDAB`。HISTORY 表 `pump_runtime` 只保留采集样本，控制写使用恰好一个当前行的 LATEST 表 `pump_controls`。

### SonnetDB 作为 server / slave

```sql
CREATE MODBUS ENDPOINT local_line_shadow
WITH (
    TRANSPORT TCP,
    BIND '192.168.10.20:1502',
    UNIT_ID 1,
    ADDRESSING MODICON,
    BYTE_ORDER BIG_ENDIAN,
    WORD_ORDER BIG_ENDIAN,
    ALLOWLIST ('192.168.10.0/24'),
    MAX_CONNECTIONS 32,
    WRITE_POLICY STAGED,
    ENABLED TRUE
);

CREATE TABLE line_shadow (
    id INT NOT NULL,

    running BOOL
        EXPOSE AS MODBUS COIL(1)
        AS BIT
        ACCESS READ_WRITE,

    speed_rpm INT
        EXPOSE AS MODBUS HOLDING_REGISTER(40001)
        AS UINT16
        ACCESS READ_WRITE,

    temperature FLOAT
        EXPOSE AS MODBUS INPUT_REGISTER(30001)
        AS INT16
        SCALE 0.1
        ACCESS READ,

    PRIMARY KEY (id)
)
USING MODBUS ENDPOINT local_line_shadow
WITH (
    ROW KEY 1,
    ON_EXTERNAL_WRITE UPDATE_TABLE
);
```

当全局 `SonnetDBServer:Modbus:Enabled=true` 且主键 `1` 的固定行存在时，上述 endpoint 会监听 `192.168.10.20:1502`，允许白名单网段读取 Coil 1、Holding Register 40001 和 Input Register 30001。#295 落地前，外部 client 对 Coil 1 或 Holding Register 40001 的写请求统一收到 `0x01 Illegal Function`，不会进入 staging 或改变表值；后续写入必须先进入 staging，只有授权审批完成后才可按策略更新固定行。Input Register 30001 始终只读，DDL 不得提供关闭审计或绕过 staging 的选项。

## SHOW / DESCRIBE 稳定合同

以下只读语句保持稳定：

```sql
SHOW MODBUS SOURCES;
SHOW MODBUS ENDPOINTS;
DESCRIBE MODBUS SOURCE line1_plc;
DESCRIBE MODBUS ENDPOINT local_line_shadow;
DESCRIBE MODBUS TABLE pump_runtime;
```

`SHOW MODBUS SOURCES` 至少稳定返回：

```text
name, transport, endpoint, unit_id, addressing, byte_order, word_order,
poll_interval, timeout, retry, runtime_enabled, health,
last_success_at, last_error_code
```

当前 `poll_interval` 和 `timeout` 以 Int64 毫秒返回。结果还追加 `configured_enabled`、`configuration_source`、`catalog_revision`、`last_attempt_at`、`last_error_at` 和 `consecutive_failures`。`runtime_enabled` 只有在全局门禁、source 配置和 worker 共同启用时为 `TRUE`；`health` 使用 `disabled`、`starting`、`idle`、`healthy`、`degraded`。成功轮次把连续失败数归零，但保留最近错误码及其时间供恢复后诊断；这些运行状态不持久化，执行元数据查询本身也不会探测网络。

`SHOW MODBUS ENDPOINTS` 至少稳定返回：

```text
name, transport, bind, unit_id, addressing, byte_order, word_order,
write_policy, allowlist, max_connections, runtime_enabled, health,
last_error_code
```

Endpoint 结果同样追加 `configured_enabled`、`configuration_source` 和 `catalog_revision`。`DESCRIBE MODBUS SOURCE` / `ENDPOINT` 使用与对应 `SHOW` 相同的列合同并只返回指定对象的一行。

`DESCRIBE MODBUS SOURCE` 和 `DESCRIBE MODBUS ENDPOINT` 返回对应对象的全部有效配置及配置来源，不返回凭据或其他敏感值。Source 反映 #291 全局门禁与 worker 状态；Endpoint 反映 #294 全局门禁与 listener 状态，health 使用 `disabled`、`starting`、`listening`、`degraded`。DDL 存在不等于 runtime 已启用，元数据查询本身也不会建立连接或探测网络。

`DESCRIBE MODBUS TABLE` 每个映射列返回一行，至少稳定包含：

```text
column_name, direction, area, declared_address, pdu_address,
register_count, bit_index, wire_type, byte_order, word_order,
scale, offset, access, table_mode, on_error, external_write_action
```

`direction` 只能是 `FROM` 或 `EXPOSE`。继承的默认值必须展开为有效值，不能要求调用方读取内部 catalog 后自行合并。后续版本只能以 extend-only 方式增加 metadata 列；不得改名、改变含义或依赖内部 catalog 文件布局。

## 第一版非目标

- 不支持 Modbus RTU、ASCII、串口网关或 RTU-over-TCP；只做 Modbus TCP。
- 不把 OPC UA、S7、三菱、FINS、Allen-Bradley 或 MTConnect 合并进 M34。
- 不实现完整 SCADA、PLC 工程站、边缘工作流、发布或回滚系统。
- 不默认监听 TCP 502，不允许空 allowlist 的公网 endpoint，也不提供匿名直接写表。
- 不用普通 `SELECT` 触发实时 PLC 读取。
- 不支持可写的 Holding/Input Register bit、Unit ID 到关系行的动态映射或寄存器地址空间分页游标。
- 不承诺 Modbus Security/TLS profile；生产网络隔离、VPN 或 TLS 终止属于部署边界。
- 不允许 IoTSharp 或其他上层系统读取 SonnetDB 内部 catalog 文件。集成只能消费稳定 SQL metadata、公开 DTO/API、审批和审计合同；Product、Collection Template、Gateway、EdgeNode 和 ReleaseTask 的业务编排在上层仓库维护。

## 后续实现门禁

后续实现不得弱化本文合同：

1. Parser/AST/catalog round-trip 必须覆盖所有 contextual 产生式、三种寻址模式和 SHOW/DESCRIBE，并保持 catalog 版本兼容。
2. codec 必须对四类区域、全部 wire type、四种 32 位字节布局、缩放逆变换、边界与非法输入进行 encode/decode 对拍。
3. 模拟 PLC parity 必须验证四类读取、Coil/Holding 写入、只读区域拒绝、远端失败不提交本地成功。
4. endpoint parity 必须验证 allowlist、连接上限、`REJECT`、`STAGED`、审批后应用、过期/映射变化拒绝及全路径审计。
5. runtime 必须验证默认关闭、取消、超时、退避、重连和普通 `SELECT` 零网络访问。
6. Web/Studio 和 IoTSharp 只能消费公开稳定合同，不能依赖内部 catalog 表或文件。
